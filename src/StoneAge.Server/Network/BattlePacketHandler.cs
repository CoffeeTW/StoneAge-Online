using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Domain.Entities;
using StoneAge.Game.Battle;
using StoneAge.Game.Item;
using StoneAge.Game.Pet;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class BattlePacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    BattleManager battles,
    ItemCatalog items,
    PetSkillCatalog petSkills,
    ILogger<BattlePacketHandler> logger) : IClientPacketHandler
{
    private const int MaxPetsPerCharacter = 5;
    private const int InventoryCapacity = 20;

    private enum Actor : byte { Player, Pet, Monster }

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
        var activePet = await db.CharacterPets.AsNoTracking()
            .Include(x => x.Skills)
            .SingleOrDefaultAsync(x => x.CharacterId == characterId && x.IsActive, ct);

        var attack = character.Strength;
        var defense = character.Vitality;
        var agility = character.Agility;
        foreach (var row in equipped)
        {
            if (!items.TryGet(row.ItemId, out var item) || item is null) continue;
            attack += item.AttackBonus;
            defense += item.DefenseBonus;
            agility += item.AgilityBonus;
        }

        BattlePetSnapshot? pet = activePet is null ? null : new BattlePetSnapshot(
            activePet.Id, activePet.Name, activePet.Level, activePet.Hp, activePet.MaxHp,
            activePet.Attack, activePet.Defense, activePet.Agility, activePet.Loyalty,
            activePet.Earth, activePet.Water, activePet.Fire, activePet.Wind,
            activePet.Skills.OrderBy(x => x.Slot).Select(x => (int?)x.SkillId).FirstOrDefault());

        var battle = battles.TryStart(
            characterId, mapId, character.Hp, attack, defense, agility,
            character.Earth, character.Water, character.Fire, character.Wind, pet);
        if (battle is null)
            return false;

        if (!session.EnterBattle())
        {
            battles.End(characterId);
            return false;
        }

        await SendBattleStartAsync(stream, battle, ct);
        logger.LogInformation("Battle started CharacterId={CharacterId} MonsterId={MonsterId} PetId={PetId}", characterId, battle.Monster.Id, battle.Pet?.Id);
        return true;
    }

    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken ct)
    {
        if (session.State != SessionState.InBattle || session.CharacterId is null)
            return Task.CompletedTask;

        return packet.Opcode switch
        {
            Opcode.BattleActionRequest => ResolveTurnAsync(session, packet.Payload, stream, ct),
            Opcode.BattlePetSkillSelectRequest => SelectPetSkillAsync(session.CharacterId.Value, packet.Payload, stream, ct),
            _ => Task.CompletedTask
        };
    }

    public void Disconnect(GameSession session)
    {
        if (session.CharacterId is long id)
            battles.End(id);
    }

    private async Task SelectPetSkillAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (payload.Length != 1 || payload[0] > 3 ||
            !battles.TryGet(characterId, out var battle) || battle is null ||
            battle.Pet is null || battle.PetHp <= 0)
        {
            await SendPetSkillSelectResponseAsync(stream, false, 0, 0, "Pet skill cannot be selected.", ct);
            return;
        }

        var slot = payload[0];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.CharacterPetSkills.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CharacterPetId == battle.Pet.Id && x.Slot == slot, ct);
        if (row is null || !petSkills.TryGet(row.SkillId, out var skill) || skill is null)
        {
            await SendPetSkillSelectResponseAsync(stream, false, slot, 0, "Pet skill slot is empty or invalid.", ct);
            return;
        }

        battle.SelectedPetSkillId = row.SkillId;
        logger.LogDebug("Battle pet skill selected CharacterId={CharacterId} PetId={PetId} Slot={Slot} SkillId={SkillId}", characterId, battle.Pet.Id, slot, row.SkillId);
        await SendPetSkillSelectResponseAsync(stream, true, slot, row.SkillId, "Pet skill selected.", ct);
    }

    private async Task ResolveTurnAsync(GameSession session, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (payload.Length != 1 || payload[0] is < 1 or > 4 || session.CharacterId is null)
            return;

        var characterId = session.CharacterId.Value;
        if (!battles.TryGet(characterId, out var battle) || battle is null)
            return;

        var action = payload[0]; // 1 Attack, 2 Defend, 3 Escape, 4 Capture
        if (action == 3 && TryEscape(battle))
        {
            await PersistBattleHpAsync(characterId, battle, ct);
            battles.End(characterId);
            session.LeaveBattle();
            await SendBattleEndAsync(stream, 2, 0, 0, 0, 0, 0, 0, "Escaped.", ct);
            return;
        }

        if (action == 4 && await TryCaptureAsync(characterId, battle, ct))
        {
            battles.End(characterId);
            session.LeaveBattle();
            await SendBattleEndAsync(stream, 3, 0, 0, 0, 0, battle.Monster.Id, 0, "Captured.", ct);
            return;
        }

        var playerDamage = 0;
        var petDamage = 0;
        var monsterDamage = 0;
        var monsterTarget = (byte)0;

        var initiative = new List<(Actor Actor, int Agility, int Tie)>
        {
            (Actor.Player, battle.PlayerAgility, Random.Shared.Next()),
            (Actor.Monster, battle.Monster.Agility, Random.Shared.Next())
        };
        if (battle.Pet is not null && battle.PetHp > 0)
            initiative.Add((Actor.Pet, battle.Pet.Agility, Random.Shared.Next()));

        foreach (var entry in initiative.OrderByDescending(x => x.Agility).ThenByDescending(x => x.Tie))
        {
            if (battle.MonsterHp <= 0 || battle.PlayerHp <= 0)
                break;

            if (entry.Actor == Actor.Player)
            {
                if (action != 1) continue;
                playerDamage = CalculateDamage(
                    battle.PlayerAttack, battle.Monster.Defense,
                    battle.PlayerEarth, battle.PlayerWater, battle.PlayerFire, battle.PlayerWind,
                    battle.Monster.Earth, battle.Monster.Water, battle.Monster.Fire, battle.Monster.Wind);
                battle.MonsterHp = Math.Max(0, battle.MonsterHp - playerDamage);
                continue;
            }

            if (entry.Actor == Actor.Pet)
            {
                if (battle.Pet is null || battle.PetHp <= 0 || !PetObeys(battle.Pet.Loyalty)) continue;

                var powerPercent = 100;
                var aEarth = battle.Pet.Earth;
                var aWater = battle.Pet.Water;
                var aFire = battle.Pet.Fire;
                var aWind = battle.Pet.Wind;
                if (battle.SelectedPetSkillId is int skillId && petSkills.TryGet(skillId, out var skill) && skill is not null)
                {
                    powerPercent = Math.Clamp(skill.PowerPercent, 50, 250);
                    ApplySkillElement(skill.Element, ref aEarth, ref aWater, ref aFire, ref aWind);
                }

                var baseDamage = CalculateDamage(
                    battle.Pet.Attack, battle.Monster.Defense,
                    aEarth, aWater, aFire, aWind,
                    battle.Monster.Earth, battle.Monster.Water, battle.Monster.Fire, battle.Monster.Wind);
                petDamage = Math.Max(1, baseDamage * powerPercent / 100);
                battle.MonsterHp = Math.Max(0, battle.MonsterHp - petDamage);
                continue;
            }

            if (battle.Pet is not null && battle.PetHp > 0 && Random.Shared.Next(100) < 35)
            {
                monsterTarget = 2;
                monsterDamage = CalculateDamage(
                    battle.Monster.Attack, battle.Pet.Defense,
                    battle.Monster.Earth, battle.Monster.Water, battle.Monster.Fire, battle.Monster.Wind,
                    battle.Pet.Earth, battle.Pet.Water, battle.Pet.Fire, battle.Pet.Wind);
                battle.PetHp = Math.Max(0, battle.PetHp - monsterDamage);
            }
            else
            {
                monsterTarget = 1;
                monsterDamage = CalculateDamage(
                    battle.Monster.Attack, battle.PlayerDefense,
                    battle.Monster.Earth, battle.Monster.Water, battle.Monster.Fire, battle.Monster.Wind,
                    battle.PlayerEarth, battle.PlayerWater, battle.PlayerFire, battle.PlayerWind);
                if (action == 2)
                    monsterDamage = Math.Max(1, monsterDamage / 2);
                battle.PlayerHp = Math.Max(0, battle.PlayerHp - monsterDamage);
            }
        }

        var victory = battle.MonsterHp <= 0;
        var defeat = battle.PlayerHp <= 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var character = await db.Characters.SingleAsync(x => x.Id == characterId, ct);
        character.Hp = defeat ? 1 : battle.PlayerHp;

        CharacterPet? persistentPet = null;
        if (battle.Pet is not null)
        {
            persistentPet = await db.CharacterPets.SingleOrDefaultAsync(x => x.Id == battle.Pet.Id && x.CharacterId == characterId, ct);
            if (persistentPet is not null)
            {
                persistentPet.Hp = Math.Clamp(battle.PetHp, 0, persistentPet.MaxHp);
                persistentPet.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        var gainedExp = 0;
        var levelsGained = 0;
        var petLevelsGained = 0;
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

            if (persistentPet is not null && persistentPet.Hp > 0)
            {
                persistentPet.Experience = checked(persistentPet.Experience + gainedExp);
                while (persistentPet.Experience >= PetExperienceForNextLevel(persistentPet.Level))
                {
                    persistentPet.Experience -= PetExperienceForNextLevel(persistentPet.Level);
                    persistentPet.Level++;
                    persistentPet.MaxHp += 6;
                    persistentPet.Hp = persistentPet.MaxHp;
                    persistentPet.Attack += 2;
                    persistentPet.Defense += 1;
                    persistentPet.Agility += 1;
                    petLevelsGained++;
                }
                persistentPet.Loyalty = Math.Min(100, persistentPet.Loyalty + 1);
            }
        }

        character.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await SendTurnResultAsync(stream, battle, action, playerDamage, petDamage, monsterDamage, monsterTarget, victory, defeat, ct);

        if (victory || defeat)
        {
            battles.End(characterId);
            session.LeaveBattle();
            await SendBattleEndAsync(
                stream, victory ? (byte)1 : (byte)0, gainedExp, levelsGained,
                character.Level, character.Experience, droppedItemId, petLevelsGained,
                victory ? "Victory." : "Defeat.", ct);
            logger.LogInformation("Battle ended CharacterId={CharacterId} Victory={Victory} PetHp={PetHp}", characterId, victory, battle.PetHp);
        }
        else
        {
            battle.Turn++;
        }
    }

    private async Task PersistBattleHpAsync(long characterId, BattleSession battle, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var character = await db.Characters.SingleAsync(x => x.Id == characterId, ct);
        character.Hp = Math.Max(1, battle.PlayerHp);
        character.UpdatedAt = DateTimeOffset.UtcNow;
        if (battle.Pet is not null)
        {
            var pet = await db.CharacterPets.SingleOrDefaultAsync(x => x.Id == battle.Pet.Id && x.CharacterId == characterId, ct);
            if (pet is not null)
            {
                pet.Hp = Math.Clamp(battle.PetHp, 0, pet.MaxHp);
                pet.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static void ApplySkillElement(string element, ref byte earth, ref byte water, ref byte fire, ref byte wind)
    {
        switch (element.Trim().ToLowerInvariant())
        {
            case "earth": earth = 100; water = fire = wind = 0; break;
            case "water": water = 100; earth = fire = wind = 0; break;
            case "fire": fire = 100; earth = water = wind = 0; break;
            case "wind": wind = 100; earth = water = fire = 0; break;
        }
    }

    private static bool PetObeys(int loyalty)
        => Random.Shared.Next(100) < Math.Clamp(50 + loyalty / 2, 50, 100);

    private static bool TryEscape(BattleSession battle)
        => Random.Shared.Next(100) < 55 + Math.Clamp(battle.PlayerAgility - battle.Monster.Agility, -20, 20);

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
        var pet = new CharacterPet
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
            Wind = monster.Wind,
            Skills = [new CharacterPetSkill { Slot = 0, SkillId = 40001 }]
        };
        db.CharacterPets.Add(pet);
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
        db.CharacterItems.Add(new CharacterItem { CharacterId = characterId, ItemId = itemId, Quantity = 1, Slot = slot });
        return itemId;
    }

    private static int CalculateDamage(
        int attack, int defense,
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
    private static long PetExperienceForNextLevel(int level) => checked(level * 80L);

    private static Task SendBattleStartAsync(NetworkStream stream, BattleSession battle, CancellationToken ct)
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
        writer.Write(battle.Pet is not null ? (byte)1 : (byte)0);
        if (battle.Pet is not null)
        {
            writer.Write(battle.Pet.Id);
            WriteString(writer, battle.Pet.Name);
            writer.Write(battle.Pet.Level);
            writer.Write(battle.PetHp);
            writer.Write(battle.Pet.MaxHp);
            writer.Write(battle.Pet.Loyalty);
            writer.Write(battle.SelectedPetSkillId ?? 0);
        }
        return ConnectionSendGate.SendPacketAsync(stream, Opcode.BattleStart, ms.ToArray(), ct);
    }

    private static Task SendTurnResultAsync(NetworkStream stream, BattleSession battle, byte action, int playerDamage, int petDamage, int monsterDamage, byte monsterTarget, bool victory, bool defeat, CancellationToken ct)
    {
        var payload = new byte[28];
        payload[0] = action;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), playerDamage);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), petDamage);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), monsterDamage);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(13, 4), battle.PlayerHp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(17, 4), battle.PetHp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(21, 4), battle.MonsterHp);
        payload[25] = monsterTarget;
        payload[26] = victory ? (byte)1 : (byte)0;
        payload[27] = defeat ? (byte)1 : (byte)0;
        return ConnectionSendGate.SendPacketAsync(stream, Opcode.BattleTurnResult, payload, ct);
    }

    private static Task SendBattleEndAsync(NetworkStream stream, byte result, int exp, int levelsGained, int level, long remainingExp, int rewardId, int petLevelsGained, string message, CancellationToken ct)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[31 + messageBytes.Length];
        payload[0] = result;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), exp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), levelsGained);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), level);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(13, 8), remainingExp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(21, 4), rewardId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(25, 4), petLevelsGained);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(29, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(31));
        return ConnectionSendGate.SendPacketAsync(stream, Opcode.BattleEnd, payload, ct);
    }

    private static Task SendPetSkillSelectResponseAsync(NetworkStream stream, bool success, byte slot, int skillId, string message, CancellationToken ct)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 1 + 4 + 2 + messageBytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        payload[1] = slot;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2, 4), skillId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(8));
        return ConnectionSendGate.SendPacketAsync(stream, Opcode.BattlePetSkillSelectResponse, payload, ct);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
