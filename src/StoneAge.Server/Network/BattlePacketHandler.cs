using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Game.Battle;
using StoneAge.Game.Item;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class BattlePacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    BattleManager battles,
    ItemCatalog items,
    ILogger<BattlePacketHandler> logger) : IClientPacketHandler
{
    public async Task<bool> TryStartEncounterAsync(GameSession session, NetworkStream stream, int mapId, CancellationToken ct)
    {
        if (session.State != SessionState.InWorld || session.CharacterId is null)
            return false;

        var characterId = session.CharacterId.Value;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var character = await db.Characters.AsNoTracking().SingleAsync(x => x.Id == characterId, ct);
        var equipped = await db.CharacterItems.AsNoTracking()
            .Where(x => x.CharacterId == characterId && x.EquippedSlot != null)
            .ToListAsync(ct);

        var attack = character.Strength;
        var defense = character.Vitality;
        foreach (var row in equipped)
        {
            if (!items.TryGet(row.ItemId, out var item) || item is null) continue;
            attack += item.AttackBonus;
            defense += item.DefenseBonus;
        }

        var battle = battles.TryStart(characterId, mapId, character.Hp, attack, defense);
        if (battle is null)
            return false;

        if (!session.EnterBattle())
        {
            battles.End(characterId);
            return false;
        }

        await SendBattleStartAsync(stream, battle, ct);
        logger.LogInformation("Battle started CharacterId={CharacterId} MonsterId={MonsterId}", characterId, battle.Monster.Id);
        return true;
    }

    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken ct)
    {
        if (packet.Opcode != Opcode.BattleActionRequest || session.State != SessionState.InBattle || session.CharacterId is null)
            return Task.CompletedTask;
        return ResolveTurnAsync(session, packet.Payload, stream, ct);
    }

    public void Disconnect(GameSession session)
    {
        if (session.CharacterId is long id)
            battles.End(id);
    }

    private async Task ResolveTurnAsync(GameSession session, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (payload.Length != 1 || payload[0] is < 1 or > 2 || session.CharacterId is null)
            return;

        var characterId = session.CharacterId.Value;
        if (!battles.TryGet(characterId, out var battle) || battle is null)
            return;

        var action = payload[0]; // 1=Attack, 2=Defend
        var playerDamage = 0;
        var monsterDamage = 0;

        if (action == 1)
        {
            playerDamage = CalculateDamage(battle.PlayerAttack, battle.Monster.Defense);
            battle.MonsterHp = Math.Max(0, battle.MonsterHp - playerDamage);
        }

        var victory = battle.MonsterHp <= 0;
        if (!victory)
        {
            monsterDamage = CalculateDamage(battle.Monster.Attack, battle.PlayerDefense);
            if (action == 2)
                monsterDamage = Math.Max(1, monsterDamage / 2);
            battle.PlayerHp = Math.Max(0, battle.PlayerHp - monsterDamage);
        }

        var defeat = battle.PlayerHp <= 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var character = await db.Characters.SingleAsync(x => x.Id == characterId, ct);
        character.Hp = defeat ? 1 : battle.PlayerHp;

        var gainedExp = 0;
        var levelsGained = 0;
        if (victory)
        {
            gainedExp = battle.Monster.ExpReward;
            character.Experience = checked(character.Experience + gainedExp);
            while (character.Experience >= ExperienceForNextLevel(character.Level))
            {
                character.Experience -= ExperienceForNextLevel(character.Level);
                character.Level++;
                character.MaxHp += 10;
                character.MaxMp += 5;
                character.Strength += 1;
                character.Vitality += 1;
                character.Agility += 1;
                character.Endurance += 1;
                levelsGained++;
            }
            character.Hp = Math.Min(character.Hp, character.MaxHp);
        }

        character.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await SendTurnResultAsync(stream, battle, action, playerDamage, monsterDamage, victory, defeat, ct);

        if (victory || defeat)
        {
            battles.End(characterId);
            session.LeaveBattle();
            await SendBattleEndAsync(stream, victory, gainedExp, levelsGained, character.Level, character.Experience, ct);
            logger.LogInformation("Battle ended CharacterId={CharacterId} Victory={Victory} Level={Level}", characterId, victory, character.Level);
        }
        else
        {
            battle.Turn++;
        }
    }

    private static int CalculateDamage(int attack, int defense)
        => Math.Max(1, attack - (defense / 2) + Random.Shared.Next(-1, 2));

    private static long ExperienceForNextLevel(int level) => checked(level * 100L);

    private static async Task SendBattleStartAsync(NetworkStream stream, BattleSession battle, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(battle.Monster.Id);
        WriteString(writer, battle.Monster.Name);
        writer.Write(battle.Monster.Level);
        writer.Write(battle.PlayerHp);
        writer.Write(battle.MonsterHp);
        writer.Write(battle.Monster.MaxHp);
        await stream.WriteAsync(PacketCodec.Encode(Opcode.BattleStart, ms.ToArray()), ct);
    }

    private static async Task SendTurnResultAsync(NetworkStream stream, BattleSession battle, byte action, int playerDamage, int monsterDamage, bool victory, bool defeat, CancellationToken ct)
    {
        var payload = new byte[1 + 4 + 4 + 4 + 4 + 1 + 1];
        payload[0] = action;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), playerDamage);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), monsterDamage);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), battle.PlayerHp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(13, 4), battle.MonsterHp);
        payload[17] = victory ? (byte)1 : (byte)0;
        payload[18] = defeat ? (byte)1 : (byte)0;
        await stream.WriteAsync(PacketCodec.Encode(Opcode.BattleTurnResult, payload), ct);
    }

    private static async Task SendBattleEndAsync(NetworkStream stream, bool victory, int exp, int levelsGained, int level, long remainingExp, CancellationToken ct)
    {
        var payload = new byte[1 + 4 + 4 + 4 + 8];
        payload[0] = victory ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), exp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), levelsGained);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), level);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(13, 8), remainingExp);
        await stream.WriteAsync(PacketCodec.Encode(Opcode.BattleEnd, payload), ct);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
