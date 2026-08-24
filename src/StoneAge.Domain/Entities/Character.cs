namespace StoneAge.Domain.Entities;

public sealed class Character
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public required string Name { get; set; }
    public int Level { get; set; } = 1;
    public long Experience { get; set; }
    public int Hp { get; set; } = 100;
    public int MaxHp { get; set; } = 100;
    public int Mp { get; set; } = 50;
    public int MaxMp { get; set; } = 50;
    public int Strength { get; set; } = 5;
    public int Vitality { get; set; } = 5;
    public int Agility { get; set; } = 5;
    public int Endurance { get; set; } = 5;
    public byte Earth { get; set; } = 25;
    public byte Water { get; set; } = 25;
    public byte Fire { get; set; } = 25;
    public byte Wind { get; set; } = 25;
    public int Stone { get; set; } = 1000;
    public int MapId { get; set; } = 1000;
    public short X { get; set; } = 50;
    public short Y { get; set; } = 50;
    public byte Direction { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Account? Account { get; set; }
    public List<CharacterItem> Inventory { get; set; } = [];
}
