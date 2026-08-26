using System.Buffers.Binary;
using System.Text;
using StoneAge.Game.Party;
using StoneAge.Game.World;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class SocialPacketHandler(
    WorldManager world,
    WorldConnectionRegistry connections,
    PartyManager parties,
    ILogger<SocialPacketHandler> logger) : IClientPacketHandler
{
    private const int MaxChatBytes = 200;

    public Task HandleAsync(ClientConnection connection, PacketFrame packet, CancellationToken ct)
    {
        var session = connection.Session;
        if (session.State != SessionState.InWorld || session.CharacterId is null)
            return Task.CompletedTask;

        return packet.Opcode switch
        {
            Opcode.ChatSayRequest => SayAsync(connection, packet.Payload, ct),
            Opcode.PartyInviteRequest => InviteAsync(connection, packet.Payload, ct),
            Opcode.PartyAnswerRequest => AnswerAsync(connection, packet.Payload, ct),
            Opcode.PartyLeaveRequest => LeaveAsync(connection, ct),
            Opcode.PartyChatRequest => PartyChatAsync(connection, packet.Payload, ct),
            Opcode.PartyKickRequest => KickAsync(connection, packet.Payload, ct),
            Opcode.PartyLeaderTransferRequest => TransferLeaderAsync(connection, packet.Payload, ct),
            _ => Task.CompletedTask
        };
    }

    public async Task OnDisconnectedAsync(long characterId, CancellationToken ct)
    {
        if (!parties.Leave(characterId, out var remaining, out var affected))
            return;

        if (remaining is not null)
            await BroadcastPartyStateAsync(remaining, ct);
        else
            await BroadcastPartyClearedAsync(affected.Where(x => x != characterId), ct);
    }

    private async Task SayAsync(ClientConnection connection, byte[] payload, CancellationToken ct)
    {
        var characterId = connection.Session.CharacterId!.Value;
        if (!TryReadChat(payload, out var text) || !world.TryGetPlayer(characterId, out var sender) || sender is null)
            return;

        var packet = BuildChatBroadcast(Opcode.ChatSayBroadcast, sender.CharacterId, sender.Name, text);
        foreach (var player in world.GetPlayersInMap(sender.MapId))
            await connections.SendAsync(player.CharacterId, packet, ct);
    }

    private async Task PartyChatAsync(ClientConnection connection, byte[] payload, CancellationToken ct)
    {
        var characterId = connection.Session.CharacterId!.Value;
        var party = parties.GetParty(characterId);
        if (party is null || !TryReadChat(payload, out var text) || !world.TryGetPlayer(characterId, out var sender) || sender is null)
            return;

        var packet = BuildChatBroadcast(Opcode.PartyChatBroadcast, sender.CharacterId, sender.Name, text);
        foreach (var memberId in party.MemberIds)
            await connections.SendAsync(memberId, packet, ct);
    }

    private async Task InviteAsync(ClientConnection connection, byte[] payload, CancellationToken ct)
    {
        var inviterId = connection.Session.CharacterId!.Value;
        if (payload.Length != 8)
        {
            await SendInviteResultAsync(connection, PartyInviteResult.InvalidTarget, 0, "Invalid party target.", ct);
            return;
        }

        var targetId = BinaryPrimitives.ReadInt64LittleEndian(payload);
        if (!world.TryGetPlayer(inviterId, out var inviter) || inviter is null ||
            !world.TryGetPlayer(targetId, out var target) || target is null || inviter.MapId != target.MapId)
        {
            await SendInviteResultAsync(connection, PartyInviteResult.InvalidTarget, targetId, "Target must be online on the same map.", ct);
            return;
        }

        var result = parties.Invite(inviterId, targetId);
        if (result != PartyInviteResult.Success)
        {
            await SendInviteResultAsync(connection, result, targetId, result.ToString(), ct);
            return;
        }

        await SendInviteResultAsync(connection, result, targetId, "Party invite sent.", ct);
        await connections.SendAsync(targetId, BuildInviteNotification(inviterId, inviter.Name), ct);
    }

    private async Task AnswerAsync(ClientConnection connection, byte[] payload, CancellationToken ct)
    {
        var targetId = connection.Session.CharacterId!.Value;
        if (payload.Length != 9)
        {
            await SendAnswerResultAsync(connection, PartyAnswerResult.InviteNotFound, false, "Invalid party answer.", ct);
            return;
        }

        var inviterId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
        var accept = payload[8] == 1;
        if (!world.TryGetPlayer(inviterId, out var inviter) || inviter is null ||
            !world.TryGetPlayer(targetId, out var target) || target is null || inviter.MapId != target.MapId)
        {
            await SendAnswerResultAsync(connection, PartyAnswerResult.InviterUnavailable, accept, "Inviter is unavailable.", ct);
            return;
        }

        var result = parties.Answer(inviterId, targetId, accept, out var snapshot);
        await SendAnswerResultAsync(connection, result, accept, result == PartyAnswerResult.Success ? (accept ? "Party joined." : "Party invite rejected.") : result.ToString(), ct);
        if (result == PartyAnswerResult.Success && accept && snapshot is not null)
            await BroadcastPartyStateAsync(snapshot, ct);
    }

    private async Task LeaveAsync(ClientConnection connection, CancellationToken ct)
    {
        var characterId = connection.Session.CharacterId!.Value;
        if (!parties.Leave(characterId, out var remaining, out var affected))
        {
            await SendSimpleAsync(connection, Opcode.PartyLeaveResponse, false, "Not in a party.", ct);
            return;
        }

        await SendSimpleAsync(connection, Opcode.PartyLeaveResponse, true, "Left party.", ct);
        await connection.SendEncodedAsync(BuildPartyCleared(), ct);
        if (remaining is not null)
            await BroadcastPartyStateAsync(remaining, ct);
        else
            await BroadcastPartyClearedAsync(affected.Where(x => x != characterId), ct);
    }

    private async Task KickAsync(ClientConnection connection, byte[] payload, CancellationToken ct)
    {
        if (payload.Length != 8)
        {
            await SendManageResultAsync(connection, Opcode.PartyKickResponse, PartyManageResult.InvalidTarget, 0, "Invalid target.", ct);
            return;
        }

        var leaderId = connection.Session.CharacterId!.Value;
        var targetId = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var result = parties.Kick(leaderId, targetId, out var remaining);
        await SendManageResultAsync(connection, Opcode.PartyKickResponse, result, targetId, result == PartyManageResult.Success ? "Member kicked." : result.ToString(), ct);
        if (result != PartyManageResult.Success)
            return;

        await connections.SendAsync(targetId, BuildPartyCleared(), ct);
        if (remaining is not null)
            await BroadcastPartyStateAsync(remaining, ct);
        else
            await connection.SendEncodedAsync(BuildPartyCleared(), ct);
    }

    private async Task TransferLeaderAsync(ClientConnection connection, byte[] payload, CancellationToken ct)
    {
        if (payload.Length != 8)
        {
            await SendManageResultAsync(connection, Opcode.PartyLeaderTransferResponse, PartyManageResult.InvalidTarget, 0, "Invalid target.", ct);
            return;
        }

        var leaderId = connection.Session.CharacterId!.Value;
        var targetId = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var result = parties.TransferLeader(leaderId, targetId, out var snapshot);
        await SendManageResultAsync(connection, Opcode.PartyLeaderTransferResponse, result, targetId, result == PartyManageResult.Success ? "Leadership transferred." : result.ToString(), ct);
        if (result == PartyManageResult.Success && snapshot is not null)
            await BroadcastPartyStateAsync(snapshot, ct);
    }

    private async Task BroadcastPartyStateAsync(PartySnapshot party, CancellationToken ct)
    {
        var packet = BuildPartyState(party);
        foreach (var memberId in party.MemberIds)
            await connections.SendAsync(memberId, packet, ct);
    }

    private async Task BroadcastPartyClearedAsync(IEnumerable<long> characterIds, CancellationToken ct)
    {
        var packet = BuildPartyCleared();
        foreach (var characterId in characterIds.Distinct())
            await connections.SendAsync(characterId, packet, ct);
    }

    private byte[] BuildPartyState(PartySnapshot party)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(party.PartyId.ToByteArray());
        writer.Write(party.LeaderId);
        writer.Write(checked((byte)party.MemberIds.Count));
        foreach (var memberId in party.MemberIds)
        {
            writer.Write(memberId);
            var name = world.TryGetPlayer(memberId, out var player) && player is not null ? player.Name : string.Empty;
            WriteString(writer, name);
        }
        return PacketCodec.Encode(Opcode.PartyStateBroadcast, ms.ToArray());
    }

    private static byte[] BuildPartyCleared()
        => PacketCodec.Encode(Opcode.PartyStateBroadcast, new byte[25]);

    private static byte[] BuildChatBroadcast(Opcode opcode, long characterId, string name, string text)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(characterId);
        WriteString(writer, name);
        WriteString(writer, text);
        return PacketCodec.Encode(opcode, ms.ToArray());
    }

    private static byte[] BuildInviteNotification(long inviterId, string inviterName)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(inviterId);
        WriteString(writer, inviterName);
        return PacketCodec.Encode(Opcode.PartyInviteNotification, ms.ToArray());
    }

    private static Task SendInviteResultAsync(ClientConnection connection, PartyInviteResult result, long targetId, string message, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write((byte)result);
        writer.Write(targetId);
        WriteString(writer, message);
        return connection.SendAsync(Opcode.PartyInviteResponse, ms.ToArray(), ct);
    }

    private static Task SendAnswerResultAsync(ClientConnection connection, PartyAnswerResult result, bool accepted, string message, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write((byte)result);
        writer.Write(accepted ? (byte)1 : (byte)0);
        WriteString(writer, message);
        return connection.SendAsync(Opcode.PartyAnswerResponse, ms.ToArray(), ct);
    }

    private static Task SendManageResultAsync(ClientConnection connection, Opcode opcode, PartyManageResult result, long targetId, string message, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write((byte)result);
        writer.Write(targetId);
        WriteString(writer, message);
        return connection.SendAsync(opcode, ms.ToArray(), ct);
    }

    private static Task SendSimpleAsync(ClientConnection connection, Opcode opcode, bool success, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[3 + bytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), checked((ushort)bytes.Length));
        bytes.CopyTo(payload.AsSpan(3));
        return connection.SendAsync(opcode, payload, ct);
    }

    private static bool TryReadChat(byte[] payload, out string text)
    {
        text = string.Empty;
        if (payload.Length < 3)
            return false;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
        if (length is 0 or > MaxChatBytes || payload.Length != 2 + length)
            return false;
        text = Encoding.UTF8.GetString(payload, 2, length).Trim();
        return text.Length > 0 && !text.Any(char.IsControl);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
