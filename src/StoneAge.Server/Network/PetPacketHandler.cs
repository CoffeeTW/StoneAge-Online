using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class PetPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    ILogger<PetPacketHandler> logger) : IClientPacketHandler
{
    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken ct)
    {
        if (session.State != SessionState.InWorld || session.CharacterId is null)
            return Task.CompletedTask;

        var characterId = session.CharacterId.Value;
        return packet.Opcode switch
        {
            Opcode.PetListRequest => SendListAsync(characterId, stream, ct),
            Opcode.PetActivateRequest => ActivateAsync(characterId, packet.Payload, stream, ct),
            Opcode.PetRenameRequest => RenameAsync(characterId, packet.Payload, stream, ct),
            Opcode.PetReleaseRequest => ReleaseAsync(characterId, packet.Payload, stream, ct),
            _ => Task.CompletedTask
        };
    }

    private async Task SendListAsync(long characterId, NetworkStream stream, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pets = await db.CharacterPets.AsNoTracking()
            .Where(x => x.CharacterId == characterId)
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(checked((byte)pets.Count));
        foreach (var pet in pets)
        {
            writer.Write(pet.Id);
            writer.Write(pet.MonsterId);
            WriteString(writer, pet.Name);
            writer.Write(pet.Level);
            writer.Write(pet.Experience);
            writer.Write(pet.Hp);
            writer.Write(pet.MaxHp);
            writer.Write(pet.Attack);
            writer.Write(pet.Defense);
            writer.Write(pet.Agility);
            writer.Write(pet.Loyalty);
            writer.Write(pet.Earth);
            writer.Write(pet.Water);
            writer.Write(pet.Fire);
            writer.Write(pet.Wind);
            writer.Write(pet.IsActive ? (byte)1 : (byte)0);
        }

        await stream.WriteAsync(PacketCodec.Encode(Opcode.PetListResponse, ms.ToArray()), ct);
    }

    private async Task ActivateAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (!TryReadPetId(payload, out var petId))
        {
            await SendResultAsync(stream, Opcode.PetActivateResponse, false, "Invalid pet.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var selected = await db.CharacterPets.SingleOrDefaultAsync(x => x.Id == petId && x.CharacterId == characterId, ct);
        if (selected is null)
        {
            await SendResultAsync(stream, Opcode.PetActivateResponse, false, "Pet not found.", ct);
            return;
        }

        var current = await db.CharacterPets.Where(x => x.CharacterId == characterId && x.IsActive).ToListAsync(ct);
        foreach (var pet in current)
        {
            pet.IsActive = false;
            pet.UpdatedAt = DateTimeOffset.UtcNow;
        }

        selected.IsActive = true;
        selected.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Active pet changed CharacterId={CharacterId} PetId={PetId}", characterId, petId);
        await SendResultAsync(stream, Opcode.PetActivateResponse, true, "Active pet selected.", ct);
    }

    private async Task RenameAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (payload.Length < 10)
        {
            await SendResultAsync(stream, Opcode.PetRenameResponse, false, "Invalid rename request.", ct);
            return;
        }

        var petId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
        if (nameLength is 0 or > 48 || payload.Length != 10 + nameLength)
        {
            await SendResultAsync(stream, Opcode.PetRenameResponse, false, "Invalid pet name.", ct);
            return;
        }

        var name = Encoding.UTF8.GetString(payload, 10, nameLength).Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 24 || name.Any(char.IsControl))
        {
            await SendResultAsync(stream, Opcode.PetRenameResponse, false, "Invalid pet name.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pet = await db.CharacterPets.SingleOrDefaultAsync(x => x.Id == petId && x.CharacterId == characterId, ct);
        if (pet is null)
        {
            await SendResultAsync(stream, Opcode.PetRenameResponse, false, "Pet not found.", ct);
            return;
        }

        pet.Name = name;
        pet.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await SendResultAsync(stream, Opcode.PetRenameResponse, true, "Pet renamed.", ct);
    }

    private async Task ReleaseAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (!TryReadPetId(payload, out var petId))
        {
            await SendResultAsync(stream, Opcode.PetReleaseResponse, false, "Invalid pet.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pet = await db.CharacterPets.SingleOrDefaultAsync(x => x.Id == petId && x.CharacterId == characterId, ct);
        if (pet is null)
        {
            await SendResultAsync(stream, Opcode.PetReleaseResponse, false, "Pet not found.", ct);
            return;
        }

        db.CharacterPets.Remove(pet);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Pet released CharacterId={CharacterId} PetId={PetId}", characterId, petId);
        await SendResultAsync(stream, Opcode.PetReleaseResponse, true, "Pet released.", ct);
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
