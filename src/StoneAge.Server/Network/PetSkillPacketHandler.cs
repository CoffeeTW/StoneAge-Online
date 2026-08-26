using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Domain.Entities;
using StoneAge.Game.Pet;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class PetSkillPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    PetSkillCatalog skills,
    ILogger<PetSkillPacketHandler> logger) : IClientPacketHandler
{
    private const byte MaxSkillSlots = 4;

    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken ct)
    {
        if (session.State != SessionState.InWorld || session.CharacterId is null)
            return Task.CompletedTask;

        var characterId = session.CharacterId.Value;
        return packet.Opcode switch
        {
            Opcode.PetSkillListRequest => SendListAsync(characterId, packet.Payload, stream, ct),
            Opcode.PetSkillLearnRequest => LearnAsync(characterId, packet.Payload, stream, ct),
            Opcode.PetSkillForgetRequest => ForgetAsync(characterId, packet.Payload, stream, ct),
            _ => Task.CompletedTask
        };
    }

    private async Task SendListAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (!TryReadPetId(payload, out var petId))
        {
            await SendResultAsync(stream, Opcode.PetSkillListResponse, false, "Invalid pet.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ownsPet = await db.CharacterPets.AsNoTracking()
            .AnyAsync(x => x.Id == petId && x.CharacterId == characterId, ct);
        if (!ownsPet)
        {
            await SendResultAsync(stream, Opcode.PetSkillListResponse, false, "Pet not found.", ct);
            return;
        }

        var rows = await db.CharacterPetSkills.AsNoTracking()
            .Where(x => x.CharacterPetId == petId)
            .OrderBy(x => x.Slot)
            .ToListAsync(ct);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write((byte)1);
        writer.Write(petId);
        writer.Write(checked((byte)rows.Count));
        foreach (var row in rows)
        {
            writer.Write(row.Slot);
            writer.Write(row.SkillId);
            if (skills.TryGet(row.SkillId, out var skill) && skill is not null)
            {
                WriteString(writer, skill.Name);
                writer.Write(skill.PowerPercent);
                WriteString(writer, skill.Element);
            }
            else
            {
                WriteString(writer, "Unknown");
                writer.Write(100);
                WriteString(writer, "natural");
            }
        }

        await stream.WriteAsync(PacketCodec.Encode(Opcode.PetSkillListResponse, ms.ToArray()), ct);
    }

    private async Task LearnAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (payload.Length != 13)
        {
            await SendResultAsync(stream, Opcode.PetSkillLearnResponse, false, "Invalid request.", ct);
            return;
        }

        var petId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
        var skillId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
        var slot = payload[12];
        if (petId <= 0 || slot >= MaxSkillSlots || !skills.TryGet(skillId, out var definition) || definition is null)
        {
            await SendResultAsync(stream, Opcode.PetSkillLearnResponse, false, "Invalid skill or slot.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ownsPet = await db.CharacterPets.AnyAsync(x => x.Id == petId && x.CharacterId == characterId, ct);
        if (!ownsPet)
        {
            await SendResultAsync(stream, Opcode.PetSkillLearnResponse, false, "Pet not found.", ct);
            return;
        }

        if (await db.CharacterPetSkills.AnyAsync(x => x.CharacterPetId == petId && x.Slot == slot, ct))
        {
            await SendResultAsync(stream, Opcode.PetSkillLearnResponse, false, "Skill slot is occupied.", ct);
            return;
        }

        if (await db.CharacterPetSkills.AnyAsync(x => x.CharacterPetId == petId && x.SkillId == skillId, ct))
        {
            await SendResultAsync(stream, Opcode.PetSkillLearnResponse, false, "Pet already knows this skill.", ct);
            return;
        }

        db.CharacterPetSkills.Add(new CharacterPetSkill
        {
            CharacterPetId = petId,
            Slot = slot,
            SkillId = skillId
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Pet learned skill CharacterId={CharacterId} PetId={PetId} SkillId={SkillId} Slot={Slot}", characterId, petId, skillId, slot);
        await SendResultAsync(stream, Opcode.PetSkillLearnResponse, true, "Skill learned.", ct);
    }

    private async Task ForgetAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (payload.Length != 9)
        {
            await SendResultAsync(stream, Opcode.PetSkillForgetResponse, false, "Invalid request.", ct);
            return;
        }

        var petId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
        var slot = payload[8];
        if (petId <= 0 || slot >= MaxSkillSlots)
        {
            await SendResultAsync(stream, Opcode.PetSkillForgetResponse, false, "Invalid pet or slot.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ownsPet = await db.CharacterPets.AnyAsync(x => x.Id == petId && x.CharacterId == characterId, ct);
        if (!ownsPet)
        {
            await SendResultAsync(stream, Opcode.PetSkillForgetResponse, false, "Pet not found.", ct);
            return;
        }

        var row = await db.CharacterPetSkills.SingleOrDefaultAsync(x => x.CharacterPetId == petId && x.Slot == slot, ct);
        if (row is null)
        {
            await SendResultAsync(stream, Opcode.PetSkillForgetResponse, false, "Skill slot is empty.", ct);
            return;
        }

        db.CharacterPetSkills.Remove(row);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Pet forgot skill CharacterId={CharacterId} PetId={PetId} SkillId={SkillId} Slot={Slot}", characterId, petId, row.SkillId, slot);
        await SendResultAsync(stream, Opcode.PetSkillForgetResponse, true, "Skill forgotten.", ct);
    }

    private static bool TryReadPetId(byte[] payload, out long petId)
    {
        petId = 0;
        if (payload.Length != 8) return false;
        petId = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return petId > 0;
    }

    private static async Task SendResultAsync(NetworkStream stream, Opcode opcode, bool success, string message, CancellationToken ct)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 2 + messageBytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(3));
        await stream.WriteAsync(PacketCodec.Encode(opcode, payload), ct);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
