using System.Text.Json;

namespace StoneAge.Game.Item;

public enum EquipmentSlot : byte
{
    Weapon = 1,
    Armor = 2,
    Accessory = 3
}

public sealed class ItemDefinition
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int BuyPrice { get; init; }
    public int SellPrice { get; init; }
    public int MaxStack { get; init; } = 99;
    public string Type { get; init; } = "material";
    public string? EquipSlot { get; init; }
    public int HpRestore { get; init; }
    public int MpRestore { get; init; }
    public int AttackBonus { get; init; }
    public int DefenseBonus { get; init; }
    public int AgilityBonus { get; init; }

    public bool IsConsumable => Type.Equals("consumable", StringComparison.OrdinalIgnoreCase);
    public bool IsEquipment => TryGetEquipmentSlot(out _);

    public bool TryGetEquipmentSlot(out EquipmentSlot slot)
    {
        slot = default;
        if (string.IsNullOrWhiteSpace(EquipSlot))
            return false;

        return Enum.TryParse(EquipSlot, ignoreCase: true, out slot) && Enum.IsDefined(slot);
    }
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
