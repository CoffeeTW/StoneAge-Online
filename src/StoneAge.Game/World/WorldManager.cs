using System.Collections.Concurrent;

namespace StoneAge.Game.World;

public sealed class WorldManager
{
    private readonly ConcurrentDictionary<int, GameMap> _maps = new();
    private readonly ConcurrentDictionary<long, PlayerRuntime> _players = new();

    public WorldManager()
    {
        _maps[1000] = new GameMap(1000, "Test Village", 100, 100);
    }

    public string Name => "StoneAge Development World";

    public bool TryGetPlayer(long characterId, out PlayerRuntime? player)
        => _players.TryGetValue(characterId, out player);

    public IReadOnlyCollection<PlayerRuntime> GetPlayersInMap(int mapId)
        => _maps.TryGetValue(mapId, out var map)
            ? map.Players.Values.ToArray()
            : Array.Empty<PlayerRuntime>();

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

        if (!_maps.TryGetValue(player.MapId, out var map) || !map.IsWalkable(targetX, targetY))
            return false;

        var dx = Math.Abs(targetX - player.X);
        var dy = Math.Abs(targetY - player.Y);
        if (dx > 1 || dy > 1 || dx + dy == 0)
            return false;

        player.MoveTo(targetX, targetY, direction);
        return true;
    }
}
