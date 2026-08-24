namespace StoneAge.Domain.Entities;

public sealed class CharacterItem
{
    public long Id { get; set; }
    public long CharacterId { get; set; }
    public int ItemId { get; set; }
    public int Quantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Character? Character { get; set; }
}
