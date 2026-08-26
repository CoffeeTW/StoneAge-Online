using System.Collections.Concurrent;
using System.Text.Json;

namespace StoneAge.Game.Battle;

public sealed class MonsterDefinition
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Level { get; init; } = 1;
    public int MaxHp { get; init; } = 30;
    public int Attack { get; init; } = 5;
    public int Defense { get; init; } = 3;
    public int Agility { get; init; } = 3;
    public int ExpReward { get; init; } = 10;
    public int EncounterWeight { get; init; } = 100;
    public byte Earth { get; init; } = 25;
    public byte Water { get; init; } = 25;
    public byte Fire { get; init; } = 25;
    public byte Wind { get; init; } = 25;
    public bool CaptureEnabled { get; init; }
    public int CaptureRate { get; init; }
    public int? DropItemId { get; init; }
    public int DropRate { get; init; }
    public int[] Maps { get; init; } = [];
}

public sealed record BattlePetSnapshot(
    long Id, string Name, int Level, int Hp, int MaxHp, int Attack, int Defense, int Agility, int Loyalty,
    byte Earth, byte Water, byte Fire, byte Wind, int? PrimarySkillId);

public sealed record BattleParticipantSnapshot(long CharacterId, bool IsLeader);

public sealed class MonsterCatalog
{
    private readonly Dictionary<int, MonsterDefinition> _monsters;
    private MonsterCatalog(IEnumerable<MonsterDefinition> monsters) => _monsters = monsters.ToDictionary(x => x.Id);

    public static MonsterCatalog Load(string path)
    {
        var json = File.ReadAllText(path);
        var monsters = JsonSerializer.Deserialize<List<MonsterDefinition>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
        return new MonsterCatalog(monsters);
    }

    public IReadOnlyCollection<MonsterDefinition> GetByMap(int mapId)
        => _monsters.Values.Where(x => x.Maps.Contains(mapId)).ToArray();
}

public sealed class BattleSession
{
    public BattleSession(
        long characterId, MonsterDefinition monster, int playerHp, int playerAttack, int playerDefense, int playerAgility,
        byte earth, byte water, byte fire, byte wind, BattlePetSnapshot? pet, IReadOnlyList<long>? participantIds = null)
    {
        CharacterId = characterId;
        Monster = monster;
        PlayerHp = playerHp;
        PlayerAttack = playerAttack;
        PlayerDefense = playerDefense;
        PlayerAgility = playerAgility;
        PlayerEarth = earth;
        PlayerWater = water;
        PlayerFire = fire;
        PlayerWind = wind;
        Pet = pet;
        PetHp = pet?.Hp ?? 0;
        SelectedPetSkillId = pet?.PrimarySkillId;
        MonsterHp = monster.MaxHp;

        var ids = (participantIds ?? [characterId]).Where(x => x > 0).Distinct().ToList();
        ids.Remove(characterId);
        ids.Insert(0, characterId);
        Participants = ids.Select(id => new BattleParticipantSnapshot(id, id == characterId)).ToArray();
    }

    public long CharacterId { get; }
    public IReadOnlyList<BattleParticipantSnapshot> Participants { get; }
    public bool IsPartyBattleFoundation => Participants.Count > 1;
    public MonsterDefinition Monster { get; }
    public int PlayerHp { get; set; }
    public int PlayerAttack { get; }
    public int PlayerDefense { get; }
    public int PlayerAgility { get; }
    public byte PlayerEarth { get; }
    public byte PlayerWater { get; }
    public byte PlayerFire { get; }
    public byte PlayerWind { get; }
    public BattlePetSnapshot? Pet { get; }
    public int PetHp { get; set; }
    public int? SelectedPetSkillId { get; set; }
    public int MonsterHp { get; set; }
    public int Turn { get; set; } = 1;
}

public sealed class BattleManager(MonsterCatalog monsters)
{
    private readonly ConcurrentDictionary<long, BattleSession> _battles = new();
    private readonly ConcurrentDictionary<long, long[]> _pendingParticipantRosters = new();

    public bool IsInBattle(long characterId) => _battles.ContainsKey(characterId);
    public bool TryGet(long characterId, out BattleSession? battle) => _battles.TryGetValue(characterId, out battle);
    public void End(long characterId) => _battles.TryRemove(characterId, out _);

    public void PrepareParticipantRoster(long leaderId, IEnumerable<long> participantIds)
    {
        var ids = participantIds.Where(x => x > 0).Distinct().ToList();
        ids.Remove(leaderId);
        ids.Insert(0, leaderId);
        _pendingParticipantRosters[leaderId] = ids.ToArray();
    }

    public BattleSession? TryStart(
        long characterId, int mapId, int playerHp, int playerAttack, int playerDefense, int playerAgility,
        byte earth, byte water, byte fire, byte wind, BattlePetSnapshot? pet, int encounterPercent = 20)
    {
        _pendingParticipantRosters.TryRemove(characterId, out var participantIds);
        if (_battles.ContainsKey(characterId) || Random.Shared.Next(100) >= encounterPercent)
            return null;

        var candidates = monsters.GetByMap(mapId).Where(x => x.EncounterWeight > 0).ToArray();
        if (candidates.Length == 0)
            return null;

        var totalWeight = candidates.Sum(x => x.EncounterWeight);
        var roll = Random.Shared.Next(totalWeight);
        MonsterDefinition? selected = null;
        foreach (var candidate in candidates)
        {
            if (roll < candidate.EncounterWeight)
            {
                selected = candidate;
                break;
            }
            roll -= candidate.EncounterWeight;
        }

        var monster = selected ?? candidates[^1];
        var battle = new BattleSession(
            characterId, monster, playerHp, playerAttack, playerDefense, playerAgility,
            earth, water, fire, wind, pet, participantIds);
        return _battles.TryAdd(characterId, battle) ? battle : null;
    }
}
