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
    BattlePacketHandler battleHandler,
    ILogger<WorldPacketHandler> logger) : IClientPacketHandler
{
    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken cancellationToken)
    {
        return packet.Opcode switch
        {
            Opcode.EnterWorld => EnterWorldAsync(session, stream, cancellationToken),
            Opcode.MoveRequest => MoveAsync(session, packet.Payload, stream, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    public async Task DisconnectAsync(GameSession session)
    {
        battleHandler.Disconnect(session);
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

            var leavePacket = BuildPlayerLeaveBroadcast(characterId);
            foreach (var other in world.GetPlayersInMap(player.MapId).Where(x => x.CharacterId != characterId))
                await connections.SendAsync(other.CharacterId, leavePacket, CancellationToken.None);
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
        var existingPlayers = world.GetPlayersInMap(player.MapId).ToArray();

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

        foreach (var existing in existingPlayers)
            await connections.SendAsync(player.CharacterId, BuildPlayerEnterBroadcast(existing), cancellationToken);

        var newPlayerPacket = BuildPlayerEnterBroadcast(player);
        foreach (var other in existingPlayers)
            await connections.SendAsync(other.CharacterId, newPlayerPacket, cancellationToken);
    }

    private async Task MoveAsync(GameSession session, byte[] payload, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (session.State != SessionState.InWorld || session.CharacterId is null)
        {
            await SendMoveResponseAsync(stream, MoveResult.NotOnline, null, cancellationToken);
            return;
        }

        if (payload.Length != 5)
        {
            world.TryGetPlayer(session.CharacterId.Value, out var malformedPlayer);
            await SendMoveResponseAsync(stream, MoveResult.InvalidTarget, malformedPlayer, cancellationToken);
            return;
        }

        var targetX = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(0, 2));
        var targetY = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(2, 2));
        var direction = payload[4];
        var result = world.TryMove(session.CharacterId.Value, targetX, targetY, direction);

        world.TryGetPlayer(session.CharacterId.Value, out var player);
        await SendMoveResponseAsync(stream, result, player, cancellationToken);
        if (result != MoveResult.Success || player is null)
            return;

        var packet = BuildMoveBroadcast(player);
        foreach (var other in world.GetPlayersInMap(player.MapId))
            await connections.SendAsync(other.CharacterId, packet, cancellationToken);

        await battleHandler.TryStartEncounterAsync(session, stream, player.MapId, cancellationToken);
    }

    private static Task SendMoveResponseAsync(NetworkStream stream, MoveResult result, PlayerRuntime? player, CancellationToken cancellationToken)
    {
        var payload = new byte[10];
        payload[0] = (byte)result;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), player?.MapId ?? 0);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(5, 2), player?.X ?? (short)0);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(7, 2), player?.Y ?? (short)0);
        payload[9] = player?.Direction ?? (byte)0;
        return ConnectionSendGate.SendPacketAsync(stream, Opcode.MoveResponse, payload, cancellationToken);
    }

    private static byte[] BuildPlayerEnterBroadcast(PlayerRuntime player)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(player.CharacterId);
        var nameBytes = Encoding.UTF8.GetBytes(player.Name);
        writer.Write(checked((ushort)nameBytes.Length));
        writer.Write(nameBytes);
        writer.Write(player.MapId);
        writer.Write(player.X);
        writer.Write(player.Y);
        writer.Write(player.Direction);
        return PacketCodec.Encode(Opcode.PlayerEnterBroadcast, ms.ToArray());
    }

    private static byte[] BuildPlayerLeaveBroadcast(long characterId)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, characterId);
        return PacketCodec.Encode(Opcode.PlayerLeaveBroadcast, payload);
    }

    private static byte[] BuildMoveBroadcast(PlayerRuntime player)
    {
        var payload = new byte[17];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), player.CharacterId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), player.MapId);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(12, 2), player.X);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(14, 2), player.Y);
        payload[16] = player.Direction;
        return PacketCodec.Encode(Opcode.MoveBroadcast, payload);
    }

    private static Task SendEnterWorldResponseAsync(NetworkStream stream, bool success, PlayerRuntime? player, string message, CancellationToken cancellationToken)
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
        return ConnectionSendGate.SendPacketAsync(stream, Opcode.EnterWorld, ms.ToArray(), cancellationToken);
    }
}
