namespace StoneAge.Game.World;

public sealed class PlayerRuntime
{
    public PlayerRuntime(long characterId, string name, int mapId, short x, short y, byte direction)
    {
        CharacterId = characterId;
        Name = name;
        MapId = mapId;
        X = x;
        Y = y;
        Direction = direction;
    }

    public long CharacterId { get; }
    public string Name { get; }
    public int MapId { get; private set; }
    public short X { get; private set; }
    public short Y { get; private set; }
    public byte Direction { get; private set; }
    public DateTimeOffset LastMoveAt { get; private set; } = DateTimeOffset.MinValue;

    public void MoveTo(short x, short y, byte direction)
    {
        X = x;
        Y = y;
        Direction = direction;
        LastMoveAt = DateTimeOffset.UtcNow;
    }
}
