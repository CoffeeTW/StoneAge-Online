using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Domain.Entities;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class CharacterPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    ILogger<CharacterPacketHandler> logger) : IClientPacketHandler
{
    private const int MaxCharactersPerAccount = 4;

    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (!session.IsAuthenticated || session.AccountId is null)
            return SendErrorForAsync(packet.Opcode, stream, "Authentication required.", cancellationToken);

        return packet.Opcode switch
        {
            Opcode.CharacterListRequest => SendCharacterListAsync(session, stream, cancellationToken),
            Opcode.CharacterCreateRequest => CreateCharacterAsync(session, packet.Payload, stream, cancellationToken),
            Opcode.CharacterSelectRequest => SelectCharacterAsync(session, packet.Payload, stream, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task SendCharacterListAsync(GameSession session, NetworkStream stream, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var characters = await db.Characters
            .AsNoTracking()
            .Where(x => x.AccountId == session.AccountId)
            .OrderBy(x => x.Id)
            .Take(MaxCharactersPerAccount)
            .ToListAsync(cancellationToken);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)characters.Count);
        foreach (var character in characters)
        {
            writer.Write(character.Id);
            WriteString(writer, character.Name);
            writer.Write(character.Level);
            writer.Write(character.MapId);
            writer.Write(character.X);
            writer.Write(character.Y);
        }

        await stream.WriteAsync(PacketCodec.Encode(Opcode.CharacterListResponse, ms.ToArray()), cancellationToken);
    }

    private async Task CreateCharacterAsync(GameSession session, byte[] payload, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (!TryReadName(payload, out var name))
        {
            await SendCreateResponseAsync(stream, false, 0, "Invalid character name.", cancellationToken);
            return;
        }

        name = name.Trim();
        if (name.Length is < 2 or > 12)
        {
            await SendCreateResponseAsync(stream, false, 0, "Character name must be 2-12 characters.", cancellationToken);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var accountId = session.AccountId!.Value;

        if (await db.Characters.CountAsync(x => x.AccountId == accountId, cancellationToken) >= MaxCharactersPerAccount)
        {
            await SendCreateResponseAsync(stream, false, 0, "Character limit reached.", cancellationToken);
            return;
        }

        if (await db.Characters.AnyAsync(x => x.Name == name, cancellationToken))
        {
            await SendCreateResponseAsync(stream, false, 0, "Character name already exists.", cancellationToken);
            return;
        }

        var character = new Character
        {
            AccountId = accountId,
            Name = name,
            Level = 1,
            Experience = 0,
            MapId = 1000,
            X = 50,
            Y = 50,
            Direction = 0
        };

        db.Characters.Add(character);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Character created CharacterId={CharacterId} AccountId={AccountId} Name={Name}", character.Id, accountId, name);
        await SendCreateResponseAsync(stream, true, character.Id, "Character created.", cancellationToken);
    }

    private async Task SelectCharacterAsync(GameSession session, byte[] payload, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (payload.Length != 8)
        {
            await SendSelectResponseAsync(stream, false, 0, "Invalid character selection.", cancellationToken);
            return;
        }

        var characterId = BinaryPrimitives.ReadInt64LittleEndian(payload);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var character = await db.Characters.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == characterId && x.AccountId == session.AccountId,
            cancellationToken);

        if (character is null)
        {
            await SendSelectResponseAsync(stream, false, 0, "Character not found.", cancellationToken);
            return;
        }

        if (!session.SelectCharacter(character.Id))
        {
            await SendSelectResponseAsync(stream, false, 0, "Character cannot be selected in the current session state.", cancellationToken);
            return;
        }

        logger.LogInformation("Character selected CharacterId={CharacterId} AccountId={AccountId} SessionId={SessionId}", character.Id, session.AccountId, session.SessionId);
        await SendSelectResponseAsync(stream, true, character.Id, "Character selected.", cancellationToken);
    }

    private static bool TryReadName(byte[] payload, out string name)
    {
        name = string.Empty;
        if (payload.Length < 2)
            return false;

        var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
        if (length is 0 or > 48 || payload.Length != 2 + length)
            return false;

        name = Encoding.UTF8.GetString(payload, 2, length);
        return true;
    }

    private static async Task SendCreateResponseAsync(NetworkStream stream, bool success, long characterId, string message, CancellationToken cancellationToken)
        => await SendResultAsync(stream, Opcode.CharacterCreateResponse, success, characterId, message, cancellationToken);

    private static async Task SendSelectResponseAsync(NetworkStream stream, bool success, long characterId, string message, CancellationToken cancellationToken)
        => await SendResultAsync(stream, Opcode.CharacterSelectResponse, success, characterId, message, cancellationToken);

    private static async Task SendResultAsync(NetworkStream stream, Opcode opcode, bool success, long characterId, string message, CancellationToken cancellationToken)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 8 + 2 + messageBytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1, 8), characterId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(11));
        await stream.WriteAsync(PacketCodec.Encode(opcode, payload), cancellationToken);
    }

    private static Task SendErrorForAsync(Opcode request, NetworkStream stream, string message, CancellationToken cancellationToken)
        => request switch
        {
            Opcode.CharacterCreateRequest => SendCreateResponseAsync(stream, false, 0, message, cancellationToken),
            Opcode.CharacterSelectRequest => SendSelectResponseAsync(stream, false, 0, message, cancellationToken),
            Opcode.CharacterListRequest => stream.WriteAsync(PacketCodec.Encode(Opcode.CharacterListResponse, new byte[] { 0 }), cancellationToken).AsTask(),
            _ => Task.CompletedTask
        };

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
