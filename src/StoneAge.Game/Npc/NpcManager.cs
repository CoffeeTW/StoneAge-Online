using System.Text.Json;

namespace StoneAge.Game.Npc;

public sealed class NpcManager
{
    private readonly Dictionary<int, NpcDefinition> _npcs;

    public NpcManager()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "npcs", "npcs.json");
        if (!File.Exists(path))
        {
            _npcs = new Dictionary<int, NpcDefinition>();
            return;
        }

        var json = File.ReadAllText(path);
        var definitions = JsonSerializer.Deserialize<List<NpcDefinition>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
        _npcs = definitions.ToDictionary(x => x.Id);
    }

    public IReadOnlyCollection<NpcDefinition> GetByMap(int mapId)
        => _npcs.Values.Where(x => x.MapId == mapId).OrderBy(x => x.Id).ToArray();

    public bool TryGet(int npcId, out NpcDefinition? npc)
        => _npcs.TryGetValue(npcId, out npc);
}
