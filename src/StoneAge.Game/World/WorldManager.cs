using System.Collections.Concurrent;

namespace StoneAge.Game.World;

public sealed class WorldManager
{
    private static readonly TimeSpan MinimumMoveInterval = TimeSpan.FromMilliseconds(120);
    private readonly ConcurrentDictionary<int, GameMap> _maps = new();
    private readonly ConcurrentDictionary<long, PlayerRuntime> _players = new();

    public WorldManager()
    {
        var testVillage = new GameMap(1000, "Test Village", 100, 100);
        for (short x = 45; x <= 55; x++)
        {
            if (x != 50)
                testVillage.Block(x, 45);
        }
        _maps[1000] = testVillage;
    }

    public string Name => "StoneAge Development World";

    public bool TryGetPlayer(long characterId, out PlayerRuntime? player)
        => _players.TryGetValue(characterId, out player);

    public IReadOnlyCollection<PlayerRuntime> GetPlayersInMap(int mapId)
        => _maps.TryGetValue(mapId, out var map)
            ? map.Players.Values.ToArray()
            : Array.Empty<PlayerRuntime>();

    public IReadOnlyCollection<PlayerRuntime> GetAllPlayers() => _players.Values.ToArray();

    public bool Enter(PlayerRuntime player)
    {
        if (!_maps.TryGetValue(player.MapId, out var map) || !map.IsWalkable(player.X, player.Y))
            return false;

        if (!_players.TryAdd(player.CharacterId, player))
            return false;

        if (!map.Players.TryAdd(player.CharacterId, player))
        {
            _players.TryRemove(player.CharacterId, out _);
            return false;
        }

        return true;
    }

    public bool Leave(long characterId)
    {
        if (!_players.TryRemove(characterId, out var player))
            return false;

        if (_maps.TryGetValue(player.MapId, out var map))
            map.Players.TryRemove(characterId, out _);

        return true;
    }

    public bool TryMove(long characterId, short targetX, short targetY, byte direction)
    {
        if (!_players.TryGetValue(characterId, out var player))
            return false;

        if (direction > 7)
            return false;

        if (DateTimeOffset.UtcNow - player.LastMoveAt < MinimumMoveInterval)
            return false;

        if (!_maps.TryGetValue(player.MapId, out var map) || !map.IsWalkable(targetX, targetY))
            return false;

        var dx = targetX - player.X;
        var dy = targetY - player.Y;
        if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0))
            return false;

        if (!DirectionMatches(direction, dx, dy))
            return false;

        player.MoveTo(targetX, targetY, direction);
        return true;
    }

    private static bool DirectionMatches(byte direction, int dx, int dy)
        => direction switch
        {
            0 => dx == 0 && dy == -1,
            1 => dx == 1 && dy == -1,
            2 => dx == 1 && dy == 0,
            3 => dx == 1 && dy == 1,
            4 => dx == 0 && dy == 1,
            5 => dx == -1 && dy == 1,
            6 => dx == -1 && dy == 0,
            7 => dx == -1 && dy == -1,
            _ => false
        };
}
