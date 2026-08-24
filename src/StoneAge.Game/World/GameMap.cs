using System.Collections.Concurrent;

namespace StoneAge.Game.World;

public sealed class GameMap
{
    private readonly HashSet<int> _blockedTiles = new();

    public GameMap(int id, string name, short width, short height)
    {
        Id = id;
        Name = name;
        Width = width;
        Height = height;
    }

    public int Id { get; }
    public string Name { get; }
    public short Width { get; }
    public short Height { get; }
    public ConcurrentDictionary<long, PlayerRuntime> Players { get; } = new();

    public bool IsInBounds(short x, short y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public bool IsWalkable(short x, short y)
        => IsInBounds(x, y) && !_blockedTiles.Contains(ToKey(x, y));

    public void Block(short x, short y)
    {
        if (IsInBounds(x, y))
            _blockedTiles.Add(ToKey(x, y));
    }

    private int ToKey(short x, short y) => y * Width + x;
}
