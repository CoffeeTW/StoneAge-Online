namespace StoneAge.Domain.Entities;

public sealed class CharacterPetSkill
{
    public long Id { get; set; }
    public long CharacterPetId { get; set; }
    public byte Slot { get; set; }
    public int SkillId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public CharacterPet? Pet { get; set; }
}
