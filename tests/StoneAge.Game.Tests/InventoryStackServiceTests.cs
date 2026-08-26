using StoneAge.Domain.Entities;
using StoneAge.Game.Item;

namespace StoneAge.Game.Tests;

public sealed class InventoryStackServiceTests
{
    [Fact]
    public void TryAdd_FillsPartialStacksThenAllocatesNewSlots()
    {
        var rows = new List<CharacterItem>
        {
            new() { CharacterId = 1, ItemId = 100, Quantity = 80, Slot = 0 }
        };

        var result = InventoryStackService.TryAdd(1, 100, 150, 99, 20, rows);

        Assert.True(result);
        Assert.Equal(3, rows.Count);
        Assert.Equal(99, rows.Single(x => x.Slot == 0).Quantity);
        Assert.Equal(99, rows.Single(x => x.Slot == 1).Quantity);
        Assert.Equal(32, rows.Single(x => x.Slot == 2).Quantity);
    }

    [Fact]
    public void TryAdd_WhenCapacityIsInsufficient_DoesNotMutateRows()
    {
        var rows = new List<CharacterItem>
        {
            new() { CharacterId = 1, ItemId = 100, Quantity = 99, Slot = 0 },
            new() { CharacterId = 1, ItemId = 200, Quantity = 1, Slot = 1 }
        };

        var result = InventoryStackService.TryAdd(1, 100, 1, 99, 2, rows);

        Assert.False(result);
        Assert.Equal(2, rows.Count);
        Assert.Equal(99, rows.Single(x => x.ItemId == 100).Quantity);
    }

    [Fact]
    public void TryRemoveUnequipped_ConsumesAcrossStacksAndSkipsEquippedRows()
    {
        var equipped = new CharacterItem
        {
            CharacterId = 1,
            ItemId = 100,
            Quantity = 1,
            Slot = 0,
            EquippedSlot = 1
        };
        var rows = new List<CharacterItem>
        {
            equipped,
            new() { CharacterId = 1, ItemId = 100, Quantity = 5, Slot = 1 },
            new() { CharacterId = 1, ItemId = 100, Quantity = 7, Slot = 2 }
        };
        var removed = new List<CharacterItem>();

        var result = InventoryStackService.TryRemoveUnequipped(1, 100, 8, rows, removed);

        Assert.True(result);
        Assert.Equal(1, equipped.Quantity);
        Assert.Contains(equipped, rows);
        Assert.Single(removed);
        Assert.DoesNotContain(rows, x => x.Slot == 1);
        Assert.Equal(4, rows.Single(x => x.Slot == 2).Quantity);
    }

    [Fact]
    public void TryRemoveUnequipped_WhenQuantityIsInsufficient_DoesNotMutateRows()
    {
        var rows = new List<CharacterItem>
        {
            new() { CharacterId = 1, ItemId = 100, Quantity = 2, Slot = 0 }
        };
        var removed = new List<CharacterItem>();

        var result = InventoryStackService.TryRemoveUnequipped(1, 100, 3, rows, removed);

        Assert.False(result);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].Quantity);
        Assert.Empty(removed);
    }
}
