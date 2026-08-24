namespace StoneAge.Game.Npc;

public sealed class NpcDefinition
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int MapId { get; init; }
    public short X { get; init; }
    public short Y { get; init; }
    public byte Direction { get; init; }
    public string Type { get; init; } = "dialogue";
    public string Dialogue { get; init; } = string.Empty;
    public int? WarpMapId { get; init; }
    public short? WarpX { get; init; }
    public short? WarpY { get; init; }
    public byte? WarpDirection { get; init; }
}
