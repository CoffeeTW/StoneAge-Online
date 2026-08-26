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

public sealed class PartyBattlePacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    PartyBattleManager battles,
    ItemCatalog items,
    PetSkillCatalog petSkills,
    WorldConnectionRegistry connections,
    ILogger<PartyBattlePacketHandler> logger) : IClientPacketHandler
{
    private const short InventoryCapacity = 20;
    private const int MaxPetsPerCharacter = 5;

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
        var activePets = await db.CharacterPets.AsNoTracking()
            .Include(x => x.Skills)
            .Where(x => ids.Contains(x.CharacterId) && x.IsActive)
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

            PartyBattlePet? battlePet = null;
            var pet = activePets.SingleOrDefault(x => x.CharacterId == id);
            if (pet is not null)
            {
                var skillId = pet.Skills.OrderBy(x => x.Slot).Select(x => (int?)x.SkillId).FirstOrDefault();
                PetSkillDefinition? skill = null;
                if (skillId is int value)
                    petSkills.TryGet(value, out skill);

                battlePet = new PartyBattlePet(
                    pet.Id, pet.Name, pet.Hp, pet.MaxHp, pet.Attack, pet.Defense, pet.Agility, pet.Loyalty,
                    pet.Earth, pet.Water, pet.Fire, pet.Wind,
                    skillId,
                    skill?.PowerPercent ?? 100,
                    skill?.Element ?? "natural",
                    skill?.Effect ?? "damage",
                    skill?.EffectPower ?? 0);
            }

            participants.Add(new PartyBattleParticipant(
                character.Id, character.Name, index == 0,
                character.Hp, character.MaxHp, attack, defense, agility,
                character.Earth, character.Water, character.Fire, character.Wind,
                battlePet));
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

        logger.LogInformation("Party battle started BattleId={BattleId} Leader={LeaderId} Participants={Count} Pets={PetCount} Monster={MonsterId}",
            battle.Id, battle.Participants.First(x => x.IsLeader).CharacterId, battle.Participants.Count,
            battle.Participants.Count(x => x.Pet is not null), battle.Monster.Id);
        return true;
    }

    public async Task HandleAsync(ClientConnection connection, PacketFrame packet, CancellationToken ct)
    {
        if (packet.Opcode is not (Opcode.PartyBattleActionRequest or Opcode.BattleActionRequest) ||
            connection.Session.State != SessionState.InBattle || connection.Session.CharacterId is not long characterId)
            return;

        if (packet.Payload.Length != 1 || packet.Payload[0] is < 1 or > 4 || !battles.TryGet(characterId, out var battle) || battle is null)
        {
            await SendActionResponseAsync(connection, false, "Invalid party battle action.", ct);
            return;
        }

        var action = packet.Payload[0];
        string? specialFailureMessage = null;
        if (action is 3 or 4)
        {
            var leader = battle.Participants.First(x => x.IsLeader);
            if (leader.CharacterId != characterId)
            {
                await SendActionResponseAsync(connection, false, "Only the party leader may escape or capture.", ct);
                return;
            }
            if (!battle.CanSubmitAction(characterId))
            {
                await SendActionResponseAsync(connection, false, "Action already submitted or actor cannot act.", ct);
                return;
            }

            if (action == 3 && TryEscape(battle, leader))
            {
                await SendActionResponseAsync(connection, true, "Party escaped.", ct);
                await PersistEndStateAsync(battle, 0, ct);
                await FinishBattleAsync(battle, 3, 0, 0, 0, "Party escaped.", ct);
                return;
            }

            if (action == 4 && await TryCaptureAsync(battle, leader.CharacterId, ct))
            {
                await SendActionResponseAsync(connection, true, "Monster captured by party leader.", ct);
                await PersistEndStateAsync(battle, 0, ct);
                await FinishBattleAsync(battle, 4, 0, 0, leader.CharacterId, "Monster captured by party leader.", ct);
                return;
            }

            specialFailureMessage = action == 3
                ? "Escape failed; defending this turn."
                : "Capture failed; defending this turn.";
            action = 2;
        }

        if (!battle.TrySubmitAction(characterId, action, out var resolution))
        {
            await SendActionResponseAsync(connection, false, "Action already submitted or actor cannot act.", ct);
            return;
        }

        await SendActionResponseAsync(connection, true,
            specialFailureMessage ?? (resolution is null ? "Action submitted; waiting for party." : "Action submitted."), ct);
        if (resolution is null)
            return;

        var turnPacket = BuildTurnPacket(resolution);
        foreach (var participant in battle.Participants)
            await connections.SendAsync(participant.CharacterId, turnPacket, ct);

        if (!resolution.Victory && !resolution.Defeat)
            return;

        var expEach = resolution.Victory ? Math.Max(1, battle.Monster.ExpReward / battle.Participants.Count) : 0;
        var reward = resolution.Victory
            ? await PersistVictoryStateAsync(battle, expEach, ct)
            : await PersistEndStateAsync(battle, 0, ct);
        await FinishBattleAsync(
            battle,
            resolution.Victory ? (byte)1 : (byte)0,
            expEach,
            reward.ItemId,
            reward.OwnerCharacterId,
            resolution.Victory ? "Party victory." : "Party defeat.",
            ct);
    }

    public async Task DisconnectAsync(long characterId, CancellationToken ct)
    {
        if (!battles.TryGet(characterId, out var battle) || battle is null)
            return;

        await PersistEndStateAsync(battle, 0, ct);
        battles.End(battle);
        var endPacket = BuildEndPacket(2, 0, battle.Monster.Id, 0, 0, "Party battle aborted because a member disconnected.");
        foreach (var participant in battle.Participants.Where(x => x.CharacterId != characterId))
        {
            if (connections.TryGetConnection(participant.CharacterId, out var peer) && peer is not null)
                peer.Session.LeaveBattle();
            await connections.SendAsync(participant.CharacterId, endPacket, ct);
        }
    }

    private async Task FinishBattleAsync(PartyBattleSession battle, byte result, int expEach, int rewardItemId, long rewardOwnerId, string message, CancellationToken ct)
    {
        var endPacket = BuildEndPacket(result, expEach, battle.Monster.Id, rewardItemId, rewardOwnerId, message);
        foreach (var participant in battle.Participants)
        {
            if (connections.TryGetConnection(participant.CharacterId, out var peer) && peer is not null)
                peer.Session.LeaveBattle();
            await connections.SendAsync(participant.CharacterId, endPacket, ct);
        }
        battles.End(battle);
    }

    private static bool TryEscape(PartyBattleSession battle, PartyBattleParticipant leader)
        => Random.Shared.Next(100) < 55 + Math.Clamp(leader.Agility - battle.Monster.Agility, -20, 20);

    private async Task<bool> TryCaptureAsync(PartyBattleSession battle, long leaderCharacterId, CancellationToken ct)
    {
        if (!battle.Monster.CaptureEnabled || battle.MonsterHp <= 0)
            return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.CharacterPets.CountAsync(x => x.CharacterId == leaderCharacterId, ct) >= MaxPetsPerCharacter)
            return false;

        var missingHpPercent = 100 - (battle.MonsterHp * 100 / battle.Monster.MaxHp);
        var chance = Math.Clamp(battle.Monster.CaptureRate + missingHpPercent / 2, 1, 95);
        if (Random.Shared.Next(100) >= chance)
            return false;

        var monster = battle.Monster;
        db.CharacterPets.Add(new CharacterPet
        {
            CharacterId = leaderCharacterId,
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
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<(int ItemId, long OwnerCharacterId)> PersistVictoryStateAsync(PartyBattleSession battle, int expEach, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ApplyPersistentStateAsync(db, battle, expEach, ct);

        var rewardOwnerId = 0L;
        var rewardItemId = 0;
        if (battle.Monster.DropItemId is int itemId && battle.Monster.DropRate > 0 && Random.Shared.Next(100) < battle.Monster.DropRate)
        {
            var owner = battle.Participants[Random.Shared.Next(battle.Participants.Count)];
            if (await TryGrantDropAsync(db, owner.CharacterId, itemId, ct))
            {
                rewardOwnerId = owner.CharacterId;
                rewardItemId = itemId;
            }
        }

        await db.SaveChangesAsync(ct);
        return (rewardItemId, rewardOwnerId);
    }

    private async Task<(int ItemId, long OwnerCharacterId)> PersistEndStateAsync(PartyBattleSession battle, int expEach, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ApplyPersistentStateAsync(db, battle, expEach, ct);
        await db.SaveChangesAsync(ct);
        return (0, 0);
    }

    private async Task ApplyPersistentStateAsync(GameDbContext db, PartyBattleSession battle, int expEach, CancellationToken ct)
    {
        var ids = battle.Participants.Select(x => x.CharacterId).ToArray();
        var rows = await db.Characters.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var petIds = battle.Participants.Where(x => x.Pet is not null).Select(x => x.Pet!.PetId).ToArray();
        var petRows = petIds.Length == 0
            ? []
            : await db.CharacterPets.Where(x => petIds.Contains(x.Id)).ToListAsync(ct);

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

            if (participant.Pet is not { } pet)
                continue;
            var petRow = petRows.SingleOrDefault(x => x.Id == pet.PetId && x.CharacterId == participant.CharacterId);
            if (petRow is null)
                continue;
            petRow.Hp = Math.Clamp(pet.CurrentHp, 0, petRow.MaxHp);
            if (expEach > 0 && petRow.Hp > 0)
            {
                petRow.Experience = checked(petRow.Experience + expEach);
                while (petRow.Experience >= PetExperienceForNextLevel(petRow.Level))
                {
                    petRow.Experience -= PetExperienceForNextLevel(petRow.Level);
                    petRow.Level++;
                    petRow.MaxHp += 6;
                    petRow.Hp = petRow.MaxHp;
                    petRow.Attack += 2;
                    petRow.Defense += 1;
                    petRow.Agility += 1;
                }
                petRow.Loyalty = Math.Min(100, petRow.Loyalty + 1);
            }
            petRow.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<bool> TryGrantDropAsync(GameDbContext db, long characterId, int itemId, CancellationToken ct)
    {
        if (!items.TryGet(itemId, out var item) || item is null)
            return false;

        var rows = await db.CharacterItems
            .Where(x => x.CharacterId == characterId)
            .OrderBy(x => x.Slot)
            .ToListAsync(ct);
        if (!InventoryStackService.TryAdd(characterId, itemId, 1, item.MaxStack, InventoryCapacity, rows))
            return false;

        foreach (var row in rows.Where(x => x.Id == 0))
            db.CharacterItems.Add(row);
        return true;
    }

    private static long ExperienceForNextLevel(int level) => checked(level * 100L);
    private static long PetExperienceForNextLevel(int level) => checked(level * 80L);

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
            writer.Write(participant.Pet is not null ? (byte)1 : (byte)0);
            if (participant.Pet is { } pet)
            {
                writer.Write(pet.PetId);
                WriteString(writer, pet.Name);
                writer.Write(pet.CurrentHp);
                writer.Write(pet.MaxHp);
                writer.Write(pet.SkillId ?? 0);
            }
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
            writer.Write((byte)hit.ActorType);
            writer.Write(hit.ActorId);
            writer.Write((byte)hit.TargetType);
            writer.Write(hit.TargetId);
            writer.Write(hit.Amount);
            writer.Write(hit.TargetHp);
            writer.Write(hit.IsHeal ? (byte)1 : (byte)0);
        }
        return PacketCodec.Encode(Opcode.PartyBattleTurnResult, ms.ToArray());
    }

    private static byte[] BuildEndPacket(byte result, int expEach, int monsterId, int rewardItemId, long rewardOwnerCharacterId, string message)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(result);
        writer.Write(expEach);
        writer.Write(monsterId);
        writer.Write(rewardItemId);
        writer.Write(rewardOwnerCharacterId);
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
