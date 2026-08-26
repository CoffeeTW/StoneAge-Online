using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Game.Battle;
using StoneAge.Game.Item;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class PartyBattlePacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    PartyBattleManager battles,
    ItemCatalog items,
    WorldConnectionRegistry connections,
    ILogger<PartyBattlePacketHandler> logger) : IClientPacketHandler
{
    public bool IsInBattle(long characterId)
        => battles.TryGet(characterId, out _);

    public async Task<bool> TryStartEncounterAsync(int mapId, IReadOnlyList<long> participantIds, CancellationToken ct)
    {
        if (participantIds.Count < 2)
            return false;

        var connected = participantIds
            .Select(id => connections.TryGetConnection(id, out var connection) ? connection : null)
            .Where(x => x is not null && x.Session.State == SessionState.InWorld && x.Session.CharacterId is not null)
            .Cast<ClientConnection>()
            .ToArray();
        if (connected.Length < 2)
            return false;

        var ids = connected.Select(x => x.Session.CharacterId!.Value).ToArray();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var characters = await db.Characters.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var equipped = await db.CharacterItems.AsNoTracking()
            .Where(x => ids.Contains(x.CharacterId) && x.EquippedSlot != null)
            .ToListAsync(ct);

        var participants = new List<PartyBattleParticipant>();
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            var character = characters.SingleOrDefault(x => x.Id == id);
            if (character is null)
                continue;

            var attack = character.Strength;
            var defense = character.Vitality;
            var agility = character.Agility;
            foreach (var row in equipped.Where(x => x.CharacterId == id))
            {
                if (!items.TryGet(row.ItemId, out var item) || item is null) continue;
                attack += item.AttackBonus;
                defense += item.DefenseBonus;
                agility += item.AgilityBonus;
            }

            participants.Add(new PartyBattleParticipant(
                character.Id, character.Name, index == 0,
                character.Hp, character.MaxHp, attack, defense, agility,
                character.Earth, character.Water, character.Fire, character.Wind));
        }

        if (participants.Count < 2)
            return false;

        var battle = battles.TryStart(mapId, participants);
        if (battle is null)
            return false;

        var entered = new List<ClientConnection>();
        foreach (var participant in battle.Participants)
        {
            if (!connections.TryGetConnection(participant.CharacterId, out var connection) || connection is null || !connection.Session.EnterBattle())
            {
                foreach (var previous in entered)
                    previous.Session.LeaveBattle();
                battles.End(battle);
                return false;
            }
            entered.Add(connection);
        }

        var startPacket = BuildStartPacket(battle);
        foreach (var participant in battle.Participants)
            await connections.SendAsync(participant.CharacterId, startPacket, ct);

        logger.LogInformation("Party battle started BattleId={BattleId} Leader={LeaderId} Participants={Count} Monster={MonsterId}",
            battle.Id, battle.Participants.First(x => x.IsLeader).CharacterId, battle.Participants.Count, battle.Monster.Id);
        return true;
    }

    public async Task HandleAsync(ClientConnection connection, PacketFrame packet, CancellationToken ct)
    {
        if (packet.Opcode is not (Opcode.PartyBattleActionRequest or Opcode.BattleActionRequest) ||
            connection.Session.State != SessionState.InBattle || connection.Session.CharacterId is not long characterId)
            return;

        if (packet.Payload.Length != 1 || packet.Payload[0] is < 1 or > 2 || !battles.TryGet(characterId, out var battle) || battle is null)
        {
            await SendActionResponseAsync(connection, false, "Invalid party battle action.", ct);
            return;
        }

        if (!battle.TrySubmitAction(characterId, packet.Payload[0], out var resolution))
        {
            await SendActionResponseAsync(connection, false, "Action already submitted or actor cannot act.", ct);
            return;
        }

        await SendActionResponseAsync(connection, true, resolution is null ? "Action submitted; waiting for party." : "Action submitted.", ct);
        if (resolution is null)
            return;

        var turnPacket = BuildTurnPacket(resolution);
        foreach (var participant in battle.Participants)
            await connections.SendAsync(participant.CharacterId, turnPacket, ct);

        if (!resolution.Victory && !resolution.Defeat)
            return;

        var expEach = resolution.Victory ? Math.Max(1, battle.Monster.ExpReward / battle.Participants.Count) : 0;
        await PersistEndStateAsync(battle, expEach, ct);
        var endPacket = BuildEndPacket(resolution.Victory ? (byte)1 : (byte)0, expEach, battle.Monster.Id,
            resolution.Victory ? "Party victory." : "Party defeat.");

        foreach (var participant in battle.Participants)
        {
            if (connections.TryGetConnection(participant.CharacterId, out var peer) && peer is not null)
                peer.Session.LeaveBattle();
            await connections.SendAsync(participant.CharacterId, endPacket, ct);
        }
        battles.End(battle);
    }

    public async Task DisconnectAsync(long characterId, CancellationToken ct)
    {
        if (!battles.TryGet(characterId, out var battle) || battle is null)
            return;

        await PersistEndStateAsync(battle, 0, ct);
        battles.End(battle);
        var endPacket = BuildEndPacket(2, 0, battle.Monster.Id, "Party battle aborted because a member disconnected.");
        foreach (var participant in battle.Participants.Where(x => x.CharacterId != characterId))
        {
            if (connections.TryGetConnection(participant.CharacterId, out var peer) && peer is not null)
                peer.Session.LeaveBattle();
            await connections.SendAsync(participant.CharacterId, endPacket, ct);
        }
    }

    private async Task PersistEndStateAsync(PartyBattleSession battle, int expEach, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = battle.Participants.Select(x => x.CharacterId).ToArray();
        var rows = await db.Characters.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        foreach (var participant in battle.Participants)
        {
            var row = rows.SingleOrDefault(x => x.Id == participant.CharacterId);
            if (row is null) continue;
            row.Hp = participant.CurrentHp <= 0 ? 1 : Math.Min(participant.CurrentHp, row.MaxHp);
            if (expEach > 0)
            {
                row.Experience = checked(row.Experience + expEach);
                while (row.Experience >= ExperienceForNextLevel(row.Level))
                {
                    row.Experience -= ExperienceForNextLevel(row.Level);
                    row.Level++;
                    row.MaxHp += 10;
                    row.MaxMp += 5;
                    row.Strength++;
                    row.Vitality++;
                    row.Agility++;
                    row.Endurance++;
                }
                row.Hp = Math.Min(row.Hp, row.MaxHp);
            }
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private static long ExperienceForNextLevel(int level) => checked(level * 100L);

    private static byte[] BuildStartPacket(PartyBattleSession battle)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(battle.Id.ToByteArray());
        writer.Write(battle.Monster.Id);
        WriteString(writer, battle.Monster.Name);
        writer.Write(battle.Monster.Level);
        writer.Write(battle.MonsterHp);
        writer.Write(battle.Monster.MaxHp);
        writer.Write(checked((byte)battle.Participants.Count));
        foreach (var participant in battle.Participants)
        {
            writer.Write(participant.CharacterId);
            WriteString(writer, participant.Name);
            writer.Write(participant.IsLeader ? (byte)1 : (byte)0);
            writer.Write(participant.CurrentHp);
            writer.Write(participant.MaxHp);
        }
        return PacketCodec.Encode(Opcode.PartyBattleStart, ms.ToArray());
    }

    private static byte[] BuildTurnPacket(PartyBattleTurnResolution resolution)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(resolution.Turn);
        writer.Write(resolution.MonsterHp);
        writer.Write(resolution.Victory ? (byte)1 : (byte)0);
        writer.Write(resolution.Defeat ? (byte)1 : (byte)0);
        writer.Write(checked((byte)resolution.Hits.Count));
        foreach (var hit in resolution.Hits)
        {
            writer.Write(hit.ActorId);
            writer.Write(hit.TargetId);
            writer.Write(hit.Damage);
            writer.Write(hit.TargetHp);
        }
        return PacketCodec.Encode(Opcode.PartyBattleTurnResult, ms.ToArray());
    }

    private static byte[] BuildEndPacket(byte result, int expEach, int monsterId, string message)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(result);
        writer.Write(expEach);
        writer.Write(monsterId);
        WriteString(writer, message);
        return PacketCodec.Encode(Opcode.PartyBattleEnd, ms.ToArray());
    }

    private static Task SendActionResponseAsync(ClientConnection connection, bool success, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(success ? (byte)1 : (byte)0);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
        return connection.SendAsync(Opcode.PartyBattleActionResponse, ms.ToArray(), ct);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
