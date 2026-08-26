using System.Buffers.Binary;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Game.Battle;
using StoneAge.Game.Party;
using StoneAge.Game.World;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class WorldPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    WorldManager world,
    WorldConnectionRegistry connections,
    PartyManager parties,
    BattleManager battles,
    BattlePacketHandler battleHandler,
    ILogger<WorldPacketHandler> logger) : IClientPacketHandler
{
    private sealed record FollowPosition(long CharacterId, int MapId, short X, short Y, byte Direction);

    public Task HandleAsync(ClientConnection connection, PacketFrame packet, CancellationToken cancellationToken)
        => packet.Opcode switch
        {
            Opcode.EnterWorld => EnterWorldAsync(connection, cancellationToken),
            Opcode.MoveRequest => MoveAsync(connection, packet.Payload, cancellationToken),
            _ => Task.CompletedTask
        };

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

            await BroadcastPartyPresenceAsync(characterId, player, false, CancellationToken.None);
        }

        connections.Unregister(characterId);
        world.Leave(characterId);
        logger.LogInformation("Player left world CharacterId={CharacterId} SessionId={SessionId}", characterId, session.SessionId);
    }

    private async Task EnterWorldAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        var session = connection.Session;
        if (session.State != SessionState.CharacterSelected || session.AccountId is null || session.CharacterId is null)
        {
            await SendEnterWorldResponseAsync(connection, false, null, "Character selection required.", cancellationToken);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var character = await db.Characters.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == session.CharacterId && x.AccountId == session.AccountId,
            cancellationToken);
        if (character is null)
        {
            await SendEnterWorldResponseAsync(connection, false, null, "Character not found.", cancellationToken);
            return;
        }

        var player = new PlayerRuntime(character.Id, character.Name, character.MapId, character.X, character.Y, character.Direction);
        var existingPlayers = world.GetPlayersInMap(player.MapId).ToArray();
        if (!world.Enter(player) || !connections.Register(character.Id, connection))
        {
            world.Leave(character.Id);
            connections.Unregister(character.Id);
            await SendEnterWorldResponseAsync(connection, false, null, "Character is already online or map is unavailable.", cancellationToken);
            return;
        }

        if (!session.EnterWorld())
        {
            connections.Unregister(character.Id);
            world.Leave(character.Id);
            await SendEnterWorldResponseAsync(connection, false, null, "Invalid session state.", cancellationToken);
            return;
        }

        await SendEnterWorldResponseAsync(connection, true, player, "Entered world.", cancellationToken);
        foreach (var existing in existingPlayers)
            await connections.SendAsync(player.CharacterId, BuildPlayerEnterBroadcast(existing), cancellationToken);
        var newPlayerPacket = BuildPlayerEnterBroadcast(player);
        foreach (var other in existingPlayers)
            await connections.SendAsync(other.CharacterId, newPlayerPacket, cancellationToken);

        await BroadcastPartyPresenceAsync(player.CharacterId, player, true, cancellationToken);
        logger.LogInformation("Player entered world CharacterId={CharacterId} Map={MapId} X={X} Y={Y}", player.CharacterId, player.MapId, player.X, player.Y);
    }

    private async Task MoveAsync(ClientConnection connection, byte[] payload, CancellationToken cancellationToken)
    {
        var session = connection.Session;
        if (session.State != SessionState.InWorld || session.CharacterId is null)
        {
            await SendMoveResponseAsync(connection, MoveResult.NotOnline, null, cancellationToken);
            return;
        }

        if (payload.Length != 5)
        {
            world.TryGetPlayer(session.CharacterId.Value, out var malformedPlayer);
            await SendMoveResponseAsync(connection, MoveResult.InvalidTarget, malformedPlayer, cancellationToken);
            return;
        }

        var characterId = session.CharacterId.Value;
        var followChain = CaptureFollowChain(characterId);
        var targetX = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(0, 2));
        var targetY = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(2, 2));
        var direction = payload[4];
        var result = world.TryMove(characterId, targetX, targetY, direction);
        world.TryGetPlayer(characterId, out var player);
        await SendMoveResponseAsync(connection, result, player, cancellationToken);
        if (result != MoveResult.Success || player is null)
            return;

        await BroadcastMoveAsync(player, cancellationToken);
        await BroadcastPartyPresenceAsync(player.CharacterId, player, true, cancellationToken);
        await ApplyFollowChainAsync(followChain, cancellationToken);

        var party = parties.GetParty(characterId);
        if (party is not null && party.LeaderId == characterId)
        {
            var encounterMembers = party.MemberIds
                .Where(id => world.TryGetPlayer(id, out var member) && member is not null && member.MapId == player.MapId)
                .ToArray();
            battles.PrepareParticipantRoster(characterId, encounterMembers);
        }

        await battleHandler.TryStartEncounterAsync(connection, player.MapId, cancellationToken);
    }

    private IReadOnlyList<FollowPosition> CaptureFollowChain(long leaderId)
    {
        var party = parties.GetParty(leaderId);
        if (party is null || party.LeaderId != leaderId)
            return Array.Empty<FollowPosition>();

        var result = new List<FollowPosition>();
        foreach (var memberId in party.MemberIds)
        {
            if (!world.TryGetPlayer(memberId, out var member) || member is null)
                continue;
            result.Add(new FollowPosition(memberId, member.MapId, member.X, member.Y, member.Direction));
        }
        return result;
    }

    private async Task ApplyFollowChainAsync(IReadOnlyList<FollowPosition> chain, CancellationToken ct)
    {
        if (chain.Count < 2)
            return;

        var previous = chain[0];
        for (var i = 1; i < chain.Count; i++)
        {
            var followerOld = chain[i];
            if (followerOld.MapId != previous.MapId ||
                !world.TryFollowMove(followerOld.CharacterId, previous.MapId, previous.X, previous.Y, previous.Direction) ||
                !world.TryGetPlayer(followerOld.CharacterId, out var follower) || follower is null)
            {
                previous = followerOld;
                continue;
            }

            await BroadcastMoveAsync(follower, ct);
            await BroadcastPartyPresenceAsync(follower.CharacterId, follower, true, ct);
            previous = followerOld;
        }
    }

    private async Task BroadcastMoveAsync(PlayerRuntime player, CancellationToken ct)
    {
        var packet = BuildMoveBroadcast(player);
        foreach (var other in world.GetPlayersInMap(player.MapId))
            await connections.SendAsync(other.CharacterId, packet, ct);
    }

    private async Task BroadcastPartyPresenceAsync(long characterId, PlayerRuntime player, bool online, CancellationToken ct)
    {
        var party = parties.GetParty(characterId);
        if (party is null)
            return;

        var payload = new byte[18];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), characterId);
        payload[8] = online ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), player.MapId);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(13, 2), player.X);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(15, 2), player.Y);
        payload[17] = player.Direction;
        var packet = PacketCodec.Encode(Opcode.PartyPresenceBroadcast, payload);
        foreach (var memberId in party.MemberIds.Where(x => x != characterId))
            await connections.SendAsync(memberId, packet, ct);
    }

    private static Task SendMoveResponseAsync(ClientConnection connection, MoveResult result, PlayerRuntime? player, CancellationToken cancellationToken)
    {
        var payload = new byte[10];
        payload[0] = (byte)result;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), player?.MapId ?? 0);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(5, 2), player?.X ?? (short)0);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(7, 2), player?.Y ?? (short)0);
        payload[9] = player?.Direction ?? (byte)0;
        return connection.SendAsync(Opcode.MoveResponse, payload, cancellationToken);
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

    private static Task SendEnterWorldResponseAsync(ClientConnection connection, bool success, PlayerRuntime? player, string message, CancellationToken cancellationToken)
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
        return connection.SendAsync(Opcode.EnterWorld, ms.ToArray(), cancellationToken);
    }
}
