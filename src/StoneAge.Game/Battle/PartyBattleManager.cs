using System.Collections.Concurrent;

namespace StoneAge.Game.Battle;

public enum PartyBattleActorType : byte
{
    Monster = 0,
    Player = 1,
    Pet = 2
}

public sealed record PartyBattlePet(
    long PetId,
    string Name,
    int Hp,
    int MaxHp,
    int Attack,
    int Defense,
    int Agility,
    int Loyalty,
    byte Earth,
    byte Water,
    byte Fire,
    byte Wind,
    int? SkillId,
    int SkillPowerPercent,
    string SkillElement,
    string SkillEffect,
    int SkillEffectPower)
{
    public int CurrentHp { get; set; } = Hp;
}

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
    byte Wind,
    PartyBattlePet? Pet)
{
    public int CurrentHp { get; set; } = Hp;
}

public sealed record PartyBattleHit(
    PartyBattleActorType ActorType,
    long ActorId,
    PartyBattleActorType TargetType,
    long TargetId,
    int Amount,
    int TargetHp,
    bool IsHeal);

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
        var initiative = new List<(PartyBattleActorType Type, PartyBattleParticipant? Player, PartyBattlePet? Pet, int Agility, int Tie)>();
        foreach (var player in living)
        {
            initiative.Add((PartyBattleActorType.Player, player, null, player.Agility, Random.Shared.Next()));
            if (player.Pet is { CurrentHp: > 0 } pet)
                initiative.Add((PartyBattleActorType.Pet, player, pet, pet.Agility, Random.Shared.Next()));
        }
        initiative.Add((PartyBattleActorType.Monster, null, null, Monster.Agility, Random.Shared.Next()));

        foreach (var actor in initiative.OrderByDescending(x => x.Agility).ThenByDescending(x => x.Tie))
        {
            if (MonsterHp <= 0 || Participants.All(x => x.CurrentHp <= 0))
                break;

            if (actor.Type == PartyBattleActorType.Player)
            {
                var player = actor.Player!;
                if (player.CurrentHp <= 0 || _actions[player.CharacterId] != 1)
                    continue;

                var damage = CalculateDamage(
                    player.Attack, Monster.Defense,
                    player.Earth, player.Water, player.Fire, player.Wind,
                    Monster.Earth, Monster.Water, Monster.Fire, Monster.Wind);
                MonsterHp = Math.Max(0, MonsterHp - damage);
                hits.Add(new PartyBattleHit(PartyBattleActorType.Player, player.CharacterId, PartyBattleActorType.Monster, 0, damage, MonsterHp, false));
                continue;
            }

            if (actor.Type == PartyBattleActorType.Pet)
            {
                var owner = actor.Player!;
                var pet = actor.Pet!;
                if (owner.CurrentHp <= 0 || pet.CurrentHp <= 0 || Random.Shared.Next(100) >= Math.Clamp(50 + pet.Loyalty / 2, 50, 100))
                    continue;

                if (pet.SkillEffect.Equals("heal_self", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = Math.Max(1, pet.MaxHp * Math.Clamp(pet.SkillEffectPower, 1, 100) / 100);
                    var oldHp = pet.CurrentHp;
                    pet.CurrentHp = Math.Min(pet.MaxHp, pet.CurrentHp + amount);
                    hits.Add(new PartyBattleHit(PartyBattleActorType.Pet, pet.PetId, PartyBattleActorType.Pet, pet.PetId, pet.CurrentHp - oldHp, pet.CurrentHp, true));
                    continue;
                }

                var earth = pet.Earth;
                var water = pet.Water;
                var fire = pet.Fire;
                var wind = pet.Wind;
                ApplySkillElement(pet.SkillElement, ref earth, ref water, ref fire, ref wind);
                var baseDamage = CalculateDamage(
                    pet.Attack, Monster.Defense,
                    earth, water, fire, wind,
                    Monster.Earth, Monster.Water, Monster.Fire, Monster.Wind);
                var damage = Math.Max(1, baseDamage * Math.Clamp(pet.SkillPowerPercent, 50, 250) / 100);
                MonsterHp = Math.Max(0, MonsterHp - damage);
                hits.Add(new PartyBattleHit(PartyBattleActorType.Pet, pet.PetId, PartyBattleActorType.Monster, 0, damage, MonsterHp, false));
                continue;
            }

            var playerTargets = Participants.Where(x => x.CurrentHp > 0).ToArray();
            var petTargets = playerTargets.Where(x => x.Pet is { CurrentHp: > 0 }).Select(x => x.Pet!).ToArray();
            var targetCount = playerTargets.Length + petTargets.Length;
            if (targetCount == 0)
                break;

            var targetIndex = Random.Shared.Next(targetCount);
            if (targetIndex < playerTargets.Length)
            {
                var target = playerTargets[targetIndex];
                var damage = CalculateDamage(
                    Monster.Attack, target.Defense,
                    Monster.Earth, Monster.Water, Monster.Fire, Monster.Wind,
                    target.Earth, target.Water, target.Fire, target.Wind);
                if (_actions.TryGetValue(target.CharacterId, out var targetAction) && targetAction == 2)
                    damage = Math.Max(1, damage / 2);
                target.CurrentHp = Math.Max(0, target.CurrentHp - damage);
                hits.Add(new PartyBattleHit(PartyBattleActorType.Monster, 0, PartyBattleActorType.Player, target.CharacterId, damage, target.CurrentHp, false));
            }
            else
            {
                var target = petTargets[targetIndex - playerTargets.Length];
                var damage = CalculateDamage(
                    Monster.Attack, target.Defense,
                    Monster.Earth, Monster.Water, Monster.Fire, Monster.Wind,
                    target.Earth, target.Water, target.Fire, target.Wind);
                target.CurrentHp = Math.Max(0, target.CurrentHp - damage);
                hits.Add(new PartyBattleHit(PartyBattleActorType.Monster, 0, PartyBattleActorType.Pet, target.PetId, damage, target.CurrentHp, false));
            }
        }

        return new PartyBattleTurnResolution(
            Turn,
            hits,
            MonsterHp,
            MonsterHp <= 0,
            Participants.All(x => x.CurrentHp <= 0));
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
