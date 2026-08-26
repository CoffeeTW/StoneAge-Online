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
        _maps[1001] = new GameMap(1001, "Training Field", 50, 50);
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

    public MoveResult TryMove(long characterId, short targetX, short targetY, byte direction)
    {
        if (!_players.TryGetValue(characterId, out var player))
            return MoveResult.NotOnline;

        if (direction > 7)
            return MoveResult.InvalidDirection;

        if (DateTimeOffset.UtcNow - player.LastMoveAt < MinimumMoveInterval)
            return MoveResult.TooFast;

        if (!_maps.TryGetValue(player.MapId, out var map) || !map.IsWalkable(targetX, targetY))
            return MoveResult.Blocked;

        var dx = targetX - player.X;
        var dy = targetY - player.Y;
        if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0))
            return MoveResult.InvalidTarget;

        if (!DirectionMatches(direction, dx, dy))
            return MoveResult.DirectionMismatch;

        player.MoveTo(targetX, targetY, direction);
        return MoveResult.Success;
    }

    public bool TryTeleport(long characterId, int targetMapId, short targetX, short targetY, byte direction, out int oldMapId)
    {
        oldMapId = 0;
        if (!_players.TryGetValue(characterId, out var player) || direction > 7)
            return false;

        if (!_maps.TryGetValue(targetMapId, out var targetMap) || !targetMap.IsWalkable(targetX, targetY))
            return false;

        oldMapId = player.MapId;
        if (!_maps.TryGetValue(oldMapId, out var oldMap))
            return false;

        var oldX = player.X;
        var oldY = player.Y;
        var oldDirection = player.Direction;

        oldMap.Players.TryRemove(characterId, out _);
        player.TeleportTo(targetMapId, targetX, targetY, direction);
        if (!targetMap.Players.TryAdd(characterId, player))
        {
            player.TeleportTo(oldMapId, oldX, oldY, oldDirection);
            oldMap.Players.TryAdd(characterId, player);
            return false;
        }

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
