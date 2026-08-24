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
    public int[] Maps { get; init; } = [];
}

public sealed class MonsterCatalog
{
    private readonly Dictionary<int, MonsterDefinition> _monsters;

    private MonsterCatalog(IEnumerable<MonsterDefinition> monsters)
        => _monsters = monsters.ToDictionary(x => x.Id);

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
    public BattleSession(long characterId, MonsterDefinition monster, int playerHp, int playerAttack, int playerDefense)
    {
        CharacterId = characterId;
        Monster = monster;
        PlayerHp = playerHp;
        PlayerAttack = playerAttack;
        PlayerDefense = playerDefense;
        MonsterHp = monster.MaxHp;
    }

    public long CharacterId { get; }
    public MonsterDefinition Monster { get; }
    public int PlayerHp { get; set; }
    public int PlayerAttack { get; }
    public int PlayerDefense { get; }
    public int MonsterHp { get; set; }
    public int Turn { get; set; } = 1;
}

public sealed class BattleManager(MonsterCatalog monsters)
{
    private readonly ConcurrentDictionary<long, BattleSession> _battles = new();

    public bool IsInBattle(long characterId) => _battles.ContainsKey(characterId);
    public bool TryGet(long characterId, out BattleSession? battle) => _battles.TryGetValue(characterId, out battle);
    public void End(long characterId) => _battles.TryRemove(characterId, out _);

    public BattleSession? TryStart(long characterId, int mapId, int playerHp, int playerAttack, int playerDefense, int encounterPercent = 20)
    {
        if (_battles.ContainsKey(characterId) || Random.Shared.Next(100) >= encounterPercent)
            return null;

        var candidates = monsters.GetByMap(mapId).ToArray();
        if (candidates.Length == 0)
            return null;

        var monster = candidates[Random.Shared.Next(candidates.Length)];
        var battle = new BattleSession(characterId, monster, playerHp, playerAttack, playerDefense);
        return _battles.TryAdd(characterId, battle) ? battle : null;
    }
}
