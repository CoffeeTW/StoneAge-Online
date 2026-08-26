using StoneAge.Domain.Entities;

namespace StoneAge.Game.Item;

public static class InventoryStackService
{
    public static bool TryAdd(
        long characterId,
        int itemId,
        int quantity,
        int maxStack,
        short capacity,
        IList<CharacterItem> rows)
    {
        if (quantity <= 0 || maxStack <= 0 || capacity <= 0)
            return false;

        var matching = rows
            .Where(x => x.CharacterId == characterId && x.ItemId == itemId)
            .OrderBy(x => x.Slot)
            .ToArray();

        var availableInStacks = matching.Sum(x => Math.Max(0, maxStack - x.Quantity));
        var remainingAfterStacks = Math.Max(0, quantity - availableInStacks);
        var requiredNewSlots = (remainingAfterStacks + maxStack - 1) / maxStack;
        var freeSlots = capacity - rows.Count(x => x.CharacterId == characterId);
        if (requiredNewSlots > freeSlots)
            return false;

        var remaining = quantity;
        foreach (var row in matching)
        {
            if (remaining == 0)
                break;

            var room = Math.Max(0, maxStack - row.Quantity);
            if (room == 0)
                continue;

            var added = Math.Min(room, remaining);
            row.Quantity += added;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            remaining -= added;
        }

        if (remaining == 0)
            return true;

        var usedSlots = rows
            .Where(x => x.CharacterId == characterId)
            .Select(x => x.Slot)
            .ToHashSet();

        while (remaining > 0)
        {
            short? freeSlot = null;
            for (short slot = 0; slot < capacity; slot++)
            {
                if (!usedSlots.Contains(slot))
                {
                    freeSlot = slot;
                    break;
                }
            }

            if (freeSlot is null)
                throw new InvalidOperationException("Inventory capacity changed after preflight validation.");

            var stackQuantity = Math.Min(maxStack, remaining);
            var row = new CharacterItem
            {
                CharacterId = characterId,
                ItemId = itemId,
                Quantity = stackQuantity,
                Slot = freeSlot.Value
            };
            rows.Add(row);
            usedSlots.Add(freeSlot.Value);
            remaining -= stackQuantity;
        }

        return true;
    }

    public static bool TryRemoveUnequipped(
        long characterId,
        int itemId,
        int quantity,
        IList<CharacterItem> rows,
        ICollection<CharacterItem> removedRows)
    {
        if (quantity <= 0)
            return false;

        var candidates = rows
            .Where(x => x.CharacterId == characterId && x.ItemId == itemId && x.EquippedSlot is null)
            .OrderBy(x => x.Slot)
            .ToArray();

        if (candidates.Sum(x => x.Quantity) < quantity)
            return false;

        var remaining = quantity;
        foreach (var row in candidates)
        {
            if (remaining == 0)
                break;

            var removed = Math.Min(row.Quantity, remaining);
            row.Quantity -= removed;
            remaining -= removed;
            if (row.Quantity == 0)
            {
                rows.Remove(row);
                removedRows.Add(row);
            }
            else
            {
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        return true;
    }
}
