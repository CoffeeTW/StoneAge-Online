using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Domain.Entities;
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
    private const int MaxPetsPerCharacter = 5;
    private const int InventoryCapacity = 20;

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

        var battle = battles.TryStart(
            characterId, mapId, character.Hp, attack, defense,
            character.Earth, character.Water, character.Fire, character.Wind);
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
        if (payload.Length != 1 || payload[0] is < 1 or > 4 || session.CharacterId is null)
            return;

        var characterId = session.CharacterId.Value;
        if (!battles.TryGet(characterId, out var battle) || battle is null)
            return;

        var action = payload[0]; // 1=Attack, 2=Defend, 3=Escape, 4=Capture
        if (action == 3)
        {
            var escaped = TryEscape(battle);
            if (escaped)
            {
                battles.End(characterId);
                session.LeaveBattle();
                await SendBattleEndAsync(stream, 2, 0, 0, 0, 0, 0, "Escaped.", ct);
                return;
            }
        }

        if (action == 4)
        {
            var captured = await TryCaptureAsync(characterId, battle, ct);
            if (captured)
            {
                battles.End(characterId);
                session.LeaveBattle();
                await SendBattleEndAsync(stream, 3, 0, 0, 0, 0, battle.Monster.Id, "Captured.", ct);
                return;
            }
        }

        var playerDamage = 0;
        var monsterDamage = 0;

        if (action == 1)
        {
            playerDamage = CalculateDamage(
                battle.PlayerAttack,
                battle.Monster.Defense,
                battle.PlayerEarth, battle.PlayerWater, battle.PlayerFire, battle.PlayerWind,
                battle.Monster.Earth, battle.Monster.Water, battle.Monster.Fire, battle.Monster.Wind);
            battle.MonsterHp = Math.Max(0, battle.MonsterHp - playerDamage);
        }

        var victory = battle.MonsterHp <= 0;
        if (!victory)
        {
            monsterDamage = CalculateDamage(
                battle.Monster.Attack,
                battle.PlayerDefense,
                battle.Monster.Earth, battle.Monster.Water, battle.Monster.Fire, battle.Monster.Wind,
                battle.PlayerEarth, battle.PlayerWater, battle.PlayerFire, battle.PlayerWind);
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
        var droppedItemId = 0;
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
                character.Strength++;
                character.Vitality++;
                character.Agility++;
                character.Endurance++;
                levelsGained++;
            }
            character.Hp = Math.Min(character.Hp, character.MaxHp);
            droppedItemId = await TryGrantDropAsync(db, characterId, battle.Monster, ct);
        }

        character.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await SendTurnResultAsync(stream, battle, action, playerDamage, monsterDamage, victory, defeat, ct);

        if (victory || defeat)
        {
            battles.End(characterId);
            session.LeaveBattle();
            await SendBattleEndAsync(
                stream,
                victory ? (byte)1 : (byte)0,
                gainedExp,
                levelsGained,
                character.Level,
                character.Experience,
                droppedItemId,
                victory ? "Victory." : "Defeat.",
                ct);
            logger.LogInformation("Battle ended CharacterId={CharacterId} Victory={Victory} Level={Level}", characterId, victory, character.Level);
        }
        else
        {
            battle.Turn++;
        }
    }

    private static bool TryEscape(BattleSession battle)
    {
        var baseChance = 55 + Math.Clamp(battle.PlayerAgilityEquivalent() - battle.Monster.Agility, -20, 20);
        return Random.Shared.Next(100) < baseChance;
    }

    private async Task<bool> TryCaptureAsync(long characterId, BattleSession battle, CancellationToken ct)
    {
        if (!battle.Monster.CaptureEnabled || battle.MonsterHp <= 0)
            return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.CharacterPets.CountAsync(x => x.CharacterId == characterId, ct) >= MaxPetsPerCharacter)
            return false;

        var missingHpPercent = 100 - (battle.MonsterHp * 100 / battle.Monster.MaxHp);
        var chance = Math.Clamp(battle.Monster.CaptureRate + missingHpPercent / 2, 1, 95);
        if (Random.Shared.Next(100) >= chance)
            return false;

        var monster = battle.Monster;
        db.CharacterPets.Add(new CharacterPet
        {
            CharacterId = characterId,
            MonsterId = monster.Id,
            Name = monster.Name,
            Level = monster.Level,
            Hp = monster.MaxHp,
            MaxHp = monster.MaxHp,
            Attack = monster.Attack,
            Defense = monster.Defense,
            Agility = monster.Agility,
            Loyalty = 50,
            Earth = monster.Earth,
            Water = monster.Water,
            Fire = monster.Fire,
            Wind = monster.Wind
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<int> TryGrantDropAsync(GameDbContext db, long characterId, MonsterDefinition monster, CancellationToken ct)
    {
        if (monster.DropItemId is not int itemId || monster.DropRate <= 0 || Random.Shared.Next(100) >= monster.DropRate)
            return 0;
        if (!items.TryGet(itemId, out var item) || item is null)
            return 0;

        var row = await db.CharacterItems.SingleOrDefaultAsync(x => x.CharacterId == characterId && x.ItemId == itemId, ct);
        if (row is not null && row.Quantity < item.MaxStack)
        {
            row.Quantity++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            return itemId;
        }

        var usedSlots = await db.CharacterItems.Where(x => x.CharacterId == characterId).Select(x => x.Slot).ToListAsync(ct);
        if (usedSlots.Count >= InventoryCapacity)
            return 0;

        short slot = 0;
        while (usedSlots.Contains(slot)) slot++;
        db.CharacterItems.Add(new CharacterItem
        {
            CharacterId = characterId,
            ItemId = itemId,
            Quantity = 1,
            Slot = slot
        });
        return itemId;
    }

    private static int CalculateDamage(
        int attack,
        int defense,
        byte aEarth, byte aWater, byte aFire, byte aWind,
        byte dEarth, byte dWater, byte dFire, byte dWind)
    {
        var raw = Math.Max(1, attack - defense / 2 + Random.Shared.Next(-1, 2));
        var advantage = (aWater * dFire + aFire * dWind + aWind * dEarth + aEarth * dWater)
                      - (aFire * dWater + aWind * dFire + aEarth * dWind + aWater * dEarth);
        var multiplier = Math.Clamp(1.0 + advantage / 20000.0, 0.75, 1.25);
        return Math.Max(1, (int)Math.Round(raw * multiplier));
    }

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
        writer.Write(battle.Monster.Earth);
        writer.Write(battle.Monster.Water);
        writer.Write(battle.Monster.Fire);
        writer.Write(battle.Monster.Wind);
        writer.Write(battle.Monster.CaptureEnabled ? (byte)1 : (byte)0);
        await stream.WriteAsync(PacketCodec.Encode(Opcode.BattleStart, ms.ToArray()), ct);
    }

    private static async Task SendTurnResultAsync(NetworkStream stream, BattleSession battle, byte action, int playerDamage, int monsterDamage, bool victory, bool defeat, CancellationToken ct)
    {
        var payload = new byte[19];
        payload[0] = action;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), playerDamage);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), monsterDamage);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), battle.PlayerHp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(13, 4), battle.MonsterHp);
        payload[17] = victory ? (byte)1 : (byte)0;
        payload[18] = defeat ? (byte)1 : (byte)0;
        await stream.WriteAsync(PacketCodec.Encode(Opcode.BattleTurnResult, payload), ct);
    }

    private static async Task SendBattleEndAsync(NetworkStream stream, byte result, int exp, int levelsGained, int level, long remainingExp, int rewardId, string message, CancellationToken ct)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 4 + 4 + 4 + 8 + 4 + 2 + messageBytes.Length];
        payload[0] = result; // 0 defeat, 1 victory, 2 escaped, 3 captured
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), exp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), levelsGained);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), level);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(13, 8), remainingExp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(21, 4), rewardId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(25, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(27));
        await stream.WriteAsync(PacketCodec.Encode(Opcode.BattleEnd, payload), ct);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}

file static class BattleSessionExtensions
{
    public static int PlayerAgilityEquivalent(this BattleSession battle) => 5;
}
