using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Game.World;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class WorldPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    WorldManager world,
    WorldConnectionRegistry connections,
    ILogger<WorldPacketHandler> logger) : IClientPacketHandler
{
    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken cancellationToken)
    {
        return packet.Opcode switch
        {
            Opcode.EnterWorld => EnterWorldAsync(session, stream, cancellationToken),
            Opcode.MoveRequest => MoveAsync(session, packet.Payload, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    public async Task DisconnectAsync(GameSession session)
    {
        if (session.CharacterId is not long characterId)
            return;

        if (world.TryGetPlayer(characterId, out var player) && player is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var character = await db.Characters.SingleOrDefaultAsync(x => x.Id == characterId);
            if (character is not null)
            {
                character.MapId = player.MapId;
                character.X = player.X;
                character.Y = player.Y;
                character.Direction = player.Direction;
                character.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        connections.Unregister(characterId);
        world.Leave(characterId);
        logger.LogInformation("Player left world CharacterId={CharacterId} SessionId={SessionId}", characterId, session.SessionId);
    }

    private async Task EnterWorldAsync(GameSession session, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (session.State != SessionState.CharacterSelected || session.AccountId is null || session.CharacterId is null)
        {
            await SendEnterWorldResponseAsync(stream, false, null, "Character selection required.", cancellationToken);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var character = await db.Characters.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == session.CharacterId && x.AccountId == session.AccountId,
            cancellationToken);

        if (character is null)
        {
            await SendEnterWorldResponseAsync(stream, false, null, "Character not found.", cancellationToken);
            return;
        }

        var player = new PlayerRuntime(character.Id, character.Name, character.MapId, character.X, character.Y, character.Direction);
        if (!world.Enter(player) || !connections.Register(character.Id, stream))
        {
            world.Leave(character.Id);
            connections.Unregister(character.Id);
            await SendEnterWorldResponseAsync(stream, false, null, "Character is already online or map is unavailable.", cancellationToken);
            return;
        }

        if (!session.EnterWorld())
        {
            connections.Unregister(character.Id);
            world.Leave(character.Id);
            await SendEnterWorldResponseAsync(stream, false, null, "Invalid session state.", cancellationToken);
            return;
        }

        logger.LogInformation("Player entered world CharacterId={CharacterId} Map={MapId} X={X} Y={Y}", player.CharacterId, player.MapId, player.X, player.Y);
        await SendEnterWorldResponseAsync(stream, true, player, "Entered world.", cancellationToken);
    }

    private async Task MoveAsync(GameSession session, byte[] payload, CancellationToken cancellationToken)
    {
        if (session.State != SessionState.InWorld || session.CharacterId is null || payload.Length != 5)
            return;

        var targetX = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(0, 2));
        var targetY = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(2, 2));
        var direction = payload[4];

        if (!world.TryMove(session.CharacterId.Value, targetX, targetY, direction) ||
            !world.TryGetPlayer(session.CharacterId.Value, out var player) || player is null)
            return;

        var packet = BuildMoveBroadcast(player);
        foreach (var other in world.GetPlayersInMap(player.MapId))
            await connections.SendAsync(other.CharacterId, packet, cancellationToken);
    }

    private static byte[] BuildMoveBroadcast(PlayerRuntime player)
    {
        var payload = new byte[8 + 4 + 2 + 2 + 1];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), player.CharacterId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), player.MapId);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(12, 2), player.X);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(14, 2), player.Y);
        payload[16] = player.Direction;
        return PacketCodec.Encode(Opcode.MoveBroadcast, payload);
    }

    private static async Task SendEnterWorldResponseAsync(
        NetworkStream stream,
        bool success,
        PlayerRuntime? player,
        string message,
        CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(success ? (byte)1 : (byte)0);
        writer.Write(player?.CharacterId ?? 0L);
        writer.Write(player?.MapId ?? 0);
        writer.Write(player?.X ?? (short)0);
        writer.Write(player?.Y ?? (short)0);
        writer.Write(player?.Direction ?? (byte)0);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        writer.Write(checked((ushort)messageBytes.Length));
        writer.Write(messageBytes);
        await stream.WriteAsync(PacketCodec.Encode(Opcode.EnterWorld, ms.ToArray()), cancellationToken);
    }
}
