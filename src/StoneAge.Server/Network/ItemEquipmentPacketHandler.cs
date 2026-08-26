using System.Buffers.Binary;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Game.Item;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class ItemEquipmentPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    ItemCatalog items,
    ILogger<ItemEquipmentPacketHandler> logger) : IClientPacketHandler
{
    public Task HandleAsync(ClientConnection connection, PacketFrame packet, CancellationToken ct)
    {
        var session = connection.Session;
        if (session.State != SessionState.InWorld || session.CharacterId is null)
            return Task.CompletedTask;

        var characterId = session.CharacterId.Value;
        return packet.Opcode switch
        {
            Opcode.ItemUseRequest => UseAsync(characterId, packet.Payload, connection, ct),
            Opcode.EquipmentListRequest => SendEquipmentAsync(characterId, connection, ct),
            Opcode.ItemEquipRequest => EquipAsync(characterId, packet.Payload, connection, ct),
            Opcode.ItemUnequipRequest => UnequipAsync(characterId, packet.Payload, connection, ct),
            _ => Task.CompletedTask
        };
    }

    private async Task UseAsync(long characterId, byte[] payload, ClientConnection connection, CancellationToken ct)
    {
        if (!TryReadInventoryId(payload, out var inventoryId))
        {
            await SendResultAsync(connection, Opcode.ItemUseResponse, false, "Invalid item.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.CharacterItems.SingleOrDefaultAsync(x => x.Id == inventoryId && x.CharacterId == characterId, ct);
        if (row is null || row.EquippedSlot is not null || !items.TryGet(row.ItemId, out var item) || item is null || !item.IsConsumable)
        {
            await SendResultAsync(connection, Opcode.ItemUseResponse, false, "Item cannot be used.", ct);
            return;
        }

        var character = await db.Characters.SingleAsync(x => x.Id == characterId, ct);
        var oldHp = character.Hp;
        var oldMp = character.Mp;
        character.Hp = Math.Min(character.MaxHp, checked(character.Hp + item.HpRestore));
        character.Mp = Math.Min(character.MaxMp, checked(character.Mp + item.MpRestore));
        if (character.Hp == oldHp && character.Mp == oldMp)
        {
            await SendResultAsync(connection, Opcode.ItemUseResponse, false, "Item has no effect right now.", ct);
            return;
        }

        row.Quantity--;
        if (row.Quantity <= 0) db.CharacterItems.Remove(row);
        else row.UpdatedAt = DateTimeOffset.UtcNow;
        character.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Item used CharacterId={CharacterId} ItemId={ItemId} InventoryId={InventoryId}", characterId, row.ItemId, inventoryId);
        await SendUseResultAsync(connection, true, character.Hp, character.Mp, "Item used.", ct);
    }

    private async Task EquipAsync(long characterId, byte[] payload, ClientConnection connection, CancellationToken ct)
    {
        if (!TryReadInventoryId(payload, out var inventoryId))
        {
            await SendResultAsync(connection, Opcode.ItemEquipResponse, false, "Invalid item.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.CharacterItems.SingleOrDefaultAsync(x => x.Id == inventoryId && x.CharacterId == characterId, ct);
        if (row is null || row.Quantity != 1 || !items.TryGet(row.ItemId, out var item) || item is null || !item.TryGetEquipmentSlot(out var slot))
        {
            await SendResultAsync(connection, Opcode.ItemEquipResponse, false, "Item cannot be equipped.", ct);
            return;
        }

        var slotValue = (byte)slot;
        var previous = await db.CharacterItems.SingleOrDefaultAsync(x => x.CharacterId == characterId && x.EquippedSlot == slotValue, ct);
        if (previous is not null && previous.Id != row.Id)
        {
            previous.EquippedSlot = null;
            previous.UpdatedAt = DateTimeOffset.UtcNow;
        }

        row.EquippedSlot = slotValue;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Item equipped CharacterId={CharacterId} ItemId={ItemId} Slot={Slot}", characterId, row.ItemId, slot);
        await SendResultAsync(connection, Opcode.ItemEquipResponse, true, $"Equipped {slot}.", ct);
    }

    private async Task UnequipAsync(long characterId, byte[] payload, ClientConnection connection, CancellationToken ct)
    {
        if (payload.Length != 1 || payload[0] is < 1 or > 3)
        {
            await SendResultAsync(connection, Opcode.ItemUnequipResponse, false, "Invalid equipment slot.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var slot = payload[0];
        var row = await db.CharacterItems.SingleOrDefaultAsync(x => x.CharacterId == characterId && x.EquippedSlot == slot, ct);
        if (row is null)
        {
            await SendResultAsync(connection, Opcode.ItemUnequipResponse, false, "Equipment slot is empty.", ct);
            return;
        }

        row.EquippedSlot = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await SendResultAsync(connection, Opcode.ItemUnequipResponse, true, "Unequipped.", ct);
    }

    private async Task SendEquipmentAsync(long characterId, ClientConnection connection, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var character = await db.Characters.AsNoTracking().SingleAsync(x => x.Id == characterId, ct);
        var equipped = await db.CharacterItems.AsNoTracking()
            .Where(x => x.CharacterId == characterId && x.EquippedSlot != null)
            .OrderBy(x => x.EquippedSlot)
            .ToListAsync(ct);

        var attackBonus = 0;
        var defenseBonus = 0;
        var agilityBonus = 0;
        foreach (var row in equipped)
        {
            if (!items.TryGet(row.ItemId, out var item) || item is null) continue;
            attackBonus += item.AttackBonus;
            defenseBonus += item.DefenseBonus;
            agilityBonus += item.AgilityBonus;
        }

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(checked((byte)equipped.Count));
        foreach (var row in equipped)
        {
            writer.Write(row.EquippedSlot!.Value);
            writer.Write(row.Id);
            writer.Write(row.ItemId);
        }
        writer.Write(character.Strength + attackBonus);
        writer.Write(character.Vitality + defenseBonus);
        writer.Write(character.Agility + agilityBonus);
        await connection.SendAsync(Opcode.EquipmentListResponse, ms.ToArray(), ct);
    }

    private static bool TryReadInventoryId(byte[] payload, out long inventoryId)
    {
        inventoryId = 0;
        if (payload.Length != 8) return false;
        inventoryId = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return inventoryId > 0;
    }

    private static Task SendUseResultAsync(ClientConnection connection, bool success, int hp, int mp, string message, CancellationToken ct)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 4 + 4 + 2 + messageBytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), hp);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), mp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(11));
        return connection.SendAsync(Opcode.ItemUseResponse, payload, ct);
    }

    private static Task SendResultAsync(ClientConnection connection, Opcode opcode, bool success, string message, CancellationToken ct)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 2 + messageBytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(3));
        return connection.SendAsync(opcode, payload, ct);
    }
}
