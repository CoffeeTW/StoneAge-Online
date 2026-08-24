using System.Text.Json;

namespace StoneAge.Game.Item;

public sealed class ItemDefinition
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int BuyPrice { get; init; }
    public int SellPrice { get; init; }
    public int MaxStack { get; init; } = 99;
}

public sealed class ItemCatalog
{
    private readonly Dictionary<int, ItemDefinition> _items;

    public ItemCatalog(IReadOnlyCollection<ItemDefinition> items)
        => _items = items.ToDictionary(x => x.Id);

    public static ItemCatalog Load(string path)
    {
        var json = File.ReadAllText(path);
        var items = JsonSerializer.Deserialize<List<ItemDefinition>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
        return new ItemCatalog(items);
    }

    public bool TryGet(int id, out ItemDefinition? item) => _items.TryGetValue(id, out item);
    public IReadOnlyCollection<ItemDefinition> All => _items.Values.ToArray();
}
