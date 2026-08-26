using System.Collections.Concurrent;

namespace StoneAge.Game.Battle;

public sealed record PartyBattleParticipant(
    long CharacterId,
    string Name,
    bool IsLeader,
    int Hp,
    int MaxHp,
    int Attack,
    int Defense,
    int Agility,
    byte Earth,
    byte Water,
    byte Fire,
    byte Wind)
{
    public int CurrentHp { get; set; } = Hp;
}

public sealed record PartyBattleAction(long CharacterId, byte Action);

public sealed record PartyBattleHit(long ActorId, long TargetId, int Damage, int TargetHp);

public sealed record PartyBattleTurnResolution(
    int Turn,
    IReadOnlyList<PartyBattleHit> Hits,
    int MonsterHp,
    bool Victory,
    bool Defeat);

public sealed class PartyBattleSession
{
    private readonly object _sync = new();
    private readonly Dictionary<long, byte> _actions = [];

    public PartyBattleSession(Guid id, MonsterDefinition monster, IReadOnlyList<PartyBattleParticipant> participants)
    {
        Id = id;
        Monster = monster;
        MonsterHp = monster.MaxHp;
        Participants = participants;
    }

    public Guid Id { get; }
    public MonsterDefinition Monster { get; }
    public IReadOnlyList<PartyBattleParticipant> Participants { get; }
    public int MonsterHp { get; private set; }
    public int Turn { get; private set; } = 1;

    public bool TrySubmitAction(long characterId, byte action, out PartyBattleTurnResolution? resolution)
    {
        resolution = null;
        lock (_sync)
        {
            var actor = Participants.SingleOrDefault(x => x.CharacterId == characterId);
            if (actor is null || actor.CurrentHp <= 0 || action is < 1 or > 2 || _actions.ContainsKey(characterId))
                return false;

            _actions[characterId] = action;
            var living = Participants.Where(x => x.CurrentHp > 0).ToArray();
            if (living.Any(x => !_actions.ContainsKey(x.CharacterId)))
                return true;

            resolution = ResolveTurn(living);
            _actions.Clear();
            if (!resolution.Victory && !resolution.Defeat)
                Turn++;
            return true;
        }
    }

    private PartyBattleTurnResolution ResolveTurn(IReadOnlyList<PartyBattleParticipant> living)
    {
        var hits = new List<PartyBattleHit>();
        var initiative = living
            .Select(x => (IsMonster: false, Character: x, Agility: x.Agility, Tie: Random.Shared.Next()))
            .Append((IsMonster: true, Character: (PartyBattleParticipant?)null, Agility: Monster.Agility, Tie: Random.Shared.Next()))
            .OrderByDescending(x => x.Agility)
            .ThenByDescending(x => x.Tie)
            .ToArray();

        foreach (var actor in initiative)
        {
            if (MonsterHp <= 0 || Participants.All(x => x.CurrentHp <= 0))
                break;

            if (!actor.IsMonster)
            {
                var player = actor.Character!;
                if (player.CurrentHp <= 0 || _actions[player.CharacterId] != 1)
                    continue;

                var damage = CalculateDamage(
                    player.Attack, Monster.Defense,
                    player.Earth, player.Water, player.Fire, player.Wind,
                    Monster.Earth, Monster.Water, Monster.Fire, Monster.Wind);
                MonsterHp = Math.Max(0, MonsterHp - damage);
                hits.Add(new PartyBattleHit(player.CharacterId, 0, damage, MonsterHp));
                continue;
            }

            var targets = Participants.Where(x => x.CurrentHp > 0).ToArray();
            if (targets.Length == 0)
                break;
            var target = targets[Random.Shared.Next(targets.Length)];
            var monsterDamage = CalculateDamage(
                Monster.Attack, target.Defense,
                Monster.Earth, Monster.Water, Monster.Fire, Monster.Wind,
                target.Earth, target.Water, target.Fire, target.Wind);
            if (_actions.TryGetValue(target.CharacterId, out var targetAction) && targetAction == 2)
                monsterDamage = Math.Max(1, monsterDamage / 2);
            target.CurrentHp = Math.Max(0, target.CurrentHp - monsterDamage);
            hits.Add(new PartyBattleHit(0, target.CharacterId, monsterDamage, target.CurrentHp));
        }

        return new PartyBattleTurnResolution(
            Turn,
            hits,
            MonsterHp,
            MonsterHp <= 0,
            Participants.All(x => x.CurrentHp <= 0));
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
}

public sealed class PartyBattleManager(MonsterCatalog monsters)
{
    private readonly ConcurrentDictionary<Guid, PartyBattleSession> _battles = new();
    private readonly ConcurrentDictionary<long, Guid> _membership = new();

    public bool TryGet(long characterId, out PartyBattleSession? battle)
    {
        battle = null;
        return _membership.TryGetValue(characterId, out var battleId) && _battles.TryGetValue(battleId, out battle);
    }

    public PartyBattleSession? TryStart(int mapId, IReadOnlyList<PartyBattleParticipant> participants, int encounterPercent = 20)
    {
        if (participants.Count < 2 || participants.Any(x => _membership.ContainsKey(x.CharacterId)) || Random.Shared.Next(100) >= encounterPercent)
            return null;

        var candidates = monsters.GetByMap(mapId).Where(x => x.EncounterWeight > 0).ToArray();
        if (candidates.Length == 0)
            return null;

        var totalWeight = candidates.Sum(x => x.EncounterWeight);
        var roll = Random.Shared.Next(totalWeight);
        var monster = candidates[^1];
        foreach (var candidate in candidates)
        {
            if (roll < candidate.EncounterWeight)
            {
                monster = candidate;
                break;
            }
            roll -= candidate.EncounterWeight;
        }

        var session = new PartyBattleSession(Guid.NewGuid(), monster, participants);
        if (!_battles.TryAdd(session.Id, session))
            return null;

        foreach (var participant in participants)
            _membership[participant.CharacterId] = session.Id;
        return session;
    }

    public void End(PartyBattleSession battle)
    {
        _battles.TryRemove(battle.Id, out _);
        foreach (var participant in battle.Participants)
            _membership.TryRemove(participant.CharacterId, out _);
    }

    public void Disconnect(long characterId)
    {
        if (!TryGet(characterId, out var battle) || battle is null)
            return;
        End(battle);
    }
}
