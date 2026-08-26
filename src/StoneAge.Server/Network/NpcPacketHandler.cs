using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using StoneAge.Game.Npc;
using StoneAge.Game.World;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class NpcPacketHandler(
    NpcManager npcs,
    WorldManager world,
    WorldConnectionRegistry connections,
    ILogger<NpcPacketHandler> logger) : IClientPacketHandler
{
    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (session.State != SessionState.InWorld || session.CharacterId is null)
            return Task.CompletedTask;

        return packet.Opcode switch
        {
            Opcode.NpcListRequest => SendNpcListAsync(session.CharacterId.Value, stream, cancellationToken),
            Opcode.NpcInteractRequest => InteractAsync(session.CharacterId.Value, packet.Payload, stream, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task SendNpcListAsync(long characterId, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (!world.TryGetPlayer(characterId, out var player) || player is null)
            return;

        var list = npcs.GetByMap(player.MapId);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(checked((ushort)list.Count));
        foreach (var npc in list)
        {
            writer.Write(npc.Id);
            WriteString(writer, npc.Name);
            writer.Write(npc.X);
            writer.Write(npc.Y);
            writer.Write(npc.Direction);
            WriteString(writer, npc.Type);
        }

        await ConnectionSendGate.SendPacketAsync(stream, Opcode.NpcListResponse, ms.ToArray(), cancellationToken);
    }

    private async Task InteractAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (payload.Length != 4 || !world.TryGetPlayer(characterId, out var player) || player is null)
            return;

        var npcId = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (!npcs.TryGet(npcId, out var npc) || npc is null || npc.MapId != player.MapId)
        {
            await SendDialogueAsync(stream, npcId, false, "NPC not found.", cancellationToken);
            return;
        }

        if (Math.Abs(player.X - npc.X) > 1 || Math.Abs(player.Y - npc.Y) > 1)
        {
            await SendDialogueAsync(stream, npcId, false, "You are too far away.", cancellationToken);
            return;
        }

        await SendDialogueAsync(stream, npc.Id, true, npc.Dialogue, cancellationToken);

        if (!npc.Type.Equals("warp", StringComparison.OrdinalIgnoreCase) ||
            npc.WarpMapId is null || npc.WarpX is null || npc.WarpY is null)
            return;

        var targetDirection = npc.WarpDirection ?? player.Direction;
        var oldMapId = player.MapId;
        var oldMapPlayers = world.GetPlayersInMap(oldMapId).Where(x => x.CharacterId != characterId).ToArray();

        if (!world.TryTeleport(characterId, npc.WarpMapId.Value, npc.WarpX.Value, npc.WarpY.Value, targetDirection, out _))
        {
            await SendWarpAsync(stream, false, player, "Warp failed.", cancellationToken);
            return;
        }

        foreach (var other in oldMapPlayers)
            await connections.SendAsync(other.CharacterId, BuildLeavePacket(characterId), cancellationToken);

        var newMapPlayers = world.GetPlayersInMap(player.MapId).Where(x => x.CharacterId != characterId).ToArray();
        foreach (var other in newMapPlayers)
        {
            await connections.SendAsync(other.CharacterId, BuildEnterPacket(player), cancellationToken);
            await connections.SendAsync(characterId, BuildEnterPacket(other), cancellationToken);
        }

        logger.LogInformation("NPC warp CharacterId={CharacterId} NpcId={NpcId} Map={MapId} X={X} Y={Y}", characterId, npc.Id, player.MapId, player.X, player.Y);
        await SendWarpAsync(stream, true, player, "Warped.", cancellationToken);
    }

    private static Task SendDialogueAsync(NetworkStream stream, int npcId, bool success, string text, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(success ? (byte)1 : (byte)0);
        writer.Write(npcId);
        WriteString(writer, text);
        return ConnectionSendGate.SendPacketAsync(stream, Opcode.NpcDialogueResponse, ms.ToArray(), cancellationToken);
    }

    private static Task SendWarpAsync(NetworkStream stream, bool success, PlayerRuntime player, string message, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(success ? (byte)1 : (byte)0);
        writer.Write(player.MapId);
        writer.Write(player.X);
        writer.Write(player.Y);
        writer.Write(player.Direction);
        WriteString(writer, message);
        return ConnectionSendGate.SendPacketAsync(stream, Opcode.NpcWarpResponse, ms.ToArray(), cancellationToken);
    }

    private static byte[] BuildEnterPacket(PlayerRuntime player)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(player.CharacterId);
        WriteString(writer, player.Name);
        writer.Write(player.MapId);
        writer.Write(player.X);
        writer.Write(player.Y);
        writer.Write(player.Direction);
        return PacketCodec.Encode(Opcode.PlayerEnterBroadcast, ms.ToArray());
    }

    private static byte[] BuildLeavePacket(long characterId)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, characterId);
        return PacketCodec.Encode(Opcode.PlayerLeaveBroadcast, payload);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
