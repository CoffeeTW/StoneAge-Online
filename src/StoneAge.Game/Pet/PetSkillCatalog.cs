using System.Text.Json;

namespace StoneAge.Game.Pet;

public sealed class PetSkillDefinition
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int PowerPercent { get; init; } = 100;
    public string Element { get; init; } = "natural";
    public string Effect { get; init; } = "damage";
    public int EffectPower { get; init; }
}

public sealed class PetSkillCatalog
{
    private readonly Dictionary<int, PetSkillDefinition> _skills;

    private PetSkillCatalog(IEnumerable<PetSkillDefinition> skills)
        => _skills = skills.ToDictionary(x => x.Id);

    public static PetSkillCatalog Load(string path)
    {
        var json = File.ReadAllText(path);
        var skills = JsonSerializer.Deserialize<List<PetSkillDefinition>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
        return new PetSkillCatalog(skills);
    }

    public bool TryGet(int id, out PetSkillDefinition? skill) => _skills.TryGetValue(id, out skill);
    public IReadOnlyCollection<PetSkillDefinition> All => _skills.Values.ToArray();
}
