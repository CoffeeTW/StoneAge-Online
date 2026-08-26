namespace StoneAge.Domain.Entities;

public sealed class CharacterPet
{
    public long Id { get; set; }
    public long CharacterId { get; set; }
    public int MonsterId { get; set; }
    public required string Name { get; set; }
    public int Level { get; set; } = 1;
    public long Experience { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int Agility { get; set; }
    public int Loyalty { get; set; } = 50;
    public byte Earth { get; set; }
    public byte Water { get; set; }
    public byte Fire { get; set; }
    public byte Wind { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Character? Character { get; set; }
    public List<CharacterPetSkill> Skills { get; set; } = [];
}
