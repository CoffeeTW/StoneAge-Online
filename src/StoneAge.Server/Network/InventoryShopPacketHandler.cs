using System.Buffers.Binary;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Game.Item;
using StoneAge.Game.Npc;
using StoneAge.Game.World;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class InventoryShopPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    ItemCatalog items,
    NpcManager npcs,
    WorldManager world,
    ILogger<InventoryShopPacketHandler> logger) : IClientPacketHandler
{
    private const short InventoryCapacity = 20;

    public Task HandleAsync(ClientConnection connection, PacketFrame packet, CancellationToken cancellationToken)
    {
        var session = connection.Session;
        if (session.State != SessionState.InWorld || session.CharacterId is null)
            return Task.CompletedTask;

        return packet.Opcode switch
        {
            Opcode.InventoryListRequest => SendInventoryAsync(session.CharacterId.Value, connection, cancellationToken),
            Opcode.ShopListRequest => SendShopListAsync(session.CharacterId.Value, packet.Payload, connection, cancellationToken),
            Opcode.ShopBuyRequest => BuyAsync(session.CharacterId.Value, packet.Payload, connection, cancellationToken),
            Opcode.ShopSellRequest => SellAsync(session.CharacterId.Value, packet.Payload, connection, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task SendInventoryAsync(long characterId, ClientConnection connection, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var character = await db.Characters.AsNoTracking().SingleAsync(x => x.Id == characterId, ct);
        var inventory = await db.CharacterItems.AsNoTracking()
            .Where(x => x.CharacterId == characterId)
            .OrderBy(x => x.Slot)
            .ToListAsync(ct);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(character.Stone);
        writer.Write(InventoryCapacity);
        writer.Write(checked((ushort)inventory.Count));
        foreach (var row in inventory)
        {
            writer.Write(row.Id);
            writer.Write(row.ItemId);
            writer.Write(row.Quantity);
            writer.Write(row.Slot);
            writer.Write(row.EquippedSlot ?? (byte)0);
        }
        await connection.SendAsync(Opcode.InventoryListResponse, ms.ToArray(), ct);
    }

    private async Task SendShopListAsync(long characterId, byte[] payload, ClientConnection connection, CancellationToken ct)
    {
        if (!TryReadNpc(payload, out var npcId) || !CanUseShop(characterId, npcId))
        {
            await connection.SendAsync(Opcode.ShopListResponse, new byte[] { 0, 0 }, ct);
            return;
        }

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(checked((ushort)items.All.Count));
        foreach (var item in items.All.OrderBy(x => x.Id))
        {
            writer.Write(item.Id);
            WriteString(writer, item.Name);
            writer.Write(item.BuyPrice);
            writer.Write(item.SellPrice);
            writer.Write(item.MaxStack);
            WriteString(writer, item.Type);
        }
        await connection.SendAsync(Opcode.ShopListResponse, ms.ToArray(), ct);
    }

    private async Task BuyAsync(long characterId, byte[] payload, ClientConnection connection, CancellationToken ct)
    {
        if (!TryReadTrade(payload, out var npcId, out var itemId, out var quantity) || !CanUseShop(characterId, npcId) || quantity <= 0 || !items.TryGet(itemId, out var item) || item is null)
        {
            await SendTradeResultAsync(connection, Opcode.ShopBuyResponse, false, "Invalid purchase.", ct);
            return;
        }

        int total;
        try { total = checked(item.BuyPrice * quantity); }
        catch (OverflowException)
        {
            await SendTradeResultAsync(connection, Opcode.ShopBuyResponse, false, "Invalid purchase quantity.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var character = await db.Characters.SingleAsync(x => x.Id == characterId, ct);
        if (character.Stone < total)
        {
            await SendTradeResultAsync(connection, Opcode.ShopBuyResponse, false, "Not enough Stone.", ct);
            return;
        }

        var rows = await db.CharacterItems
            .Where(x => x.CharacterId == characterId)
            .OrderBy(x => x.Slot)
            .ToListAsync(ct);
        if (!InventoryStackService.TryAdd(characterId, itemId, quantity, item.MaxStack, InventoryCapacity, rows))
        {
            await SendTradeResultAsync(connection, Opcode.ShopBuyResponse, false, "Inventory does not have enough stack space.", ct);
            return;
        }

        character.Stone -= total;
        foreach (var row in rows.Where(x => x.Id == 0))
            db.CharacterItems.Add(row);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Shop buy CharacterId={CharacterId} NpcId={NpcId} ItemId={ItemId} Quantity={Quantity} Total={Total}", characterId, npcId, itemId, quantity, total);
        await SendTradeResultAsync(connection, Opcode.ShopBuyResponse, true, "Purchase complete.", ct);
    }

    private async Task SellAsync(long characterId, byte[] payload, ClientConnection connection, CancellationToken ct)
    {
        if (!TryReadTrade(payload, out var npcId, out var itemId, out var quantity) || !CanUseShop(characterId, npcId) || quantity <= 0 || !items.TryGet(itemId, out var item) || item is null)
        {
            await SendTradeResultAsync(connection, Opcode.ShopSellResponse, false, "Invalid sale.", ct);
            return;
        }

        int total;
        try { total = checked(item.SellPrice * quantity); }
        catch (OverflowException)
        {
            await SendTradeResultAsync(connection, Opcode.ShopSellResponse, false, "Invalid sale quantity.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var character = await db.Characters.SingleAsync(x => x.Id == characterId, ct);
        var rows = await db.CharacterItems
            .Where(x => x.CharacterId == characterId)
            .OrderBy(x => x.Slot)
            .ToListAsync(ct);
        var removedRows = new List<StoneAge.Domain.Entities.CharacterItem>();
        if (!InventoryStackService.TryRemoveUnequipped(characterId, itemId, quantity, rows, removedRows))
        {
            await SendTradeResultAsync(connection, Opcode.ShopSellResponse, false, "Not enough unequipped items.", ct);
            return;
        }

        character.Stone = checked(character.Stone + total);
        db.CharacterItems.RemoveRange(removedRows);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Shop sell CharacterId={CharacterId} NpcId={NpcId} ItemId={ItemId} Quantity={Quantity} Total={Total}", characterId, npcId, itemId, quantity, total);
        await SendTradeResultAsync(connection, Opcode.ShopSellResponse, true, "Sale complete.", ct);
    }

    private bool CanUseShop(long characterId, int npcId)
    {
        if (!world.TryGetPlayer(characterId, out var player) || player is null || !npcs.TryGet(npcId, out var npc) || npc is null || !npc.Type.Equals("shop", StringComparison.OrdinalIgnoreCase))
            return false;
        return npc.MapId == player.MapId && Math.Abs(npc.X - player.X) <= 1 && Math.Abs(npc.Y - player.Y) <= 1;
    }

    private static bool TryReadNpc(byte[] payload, out int npcId)
    {
        npcId = 0;
        if (payload.Length != 4) return false;
        npcId = BinaryPrimitives.ReadInt32LittleEndian(payload);
        return true;
    }

    private static bool TryReadTrade(byte[] payload, out int npcId, out int itemId, out int quantity)
    {
        npcId = itemId = quantity = 0;
        if (payload.Length != 12) return false;
        npcId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        itemId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        quantity = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
        return true;
    }

    private static Task SendTradeResultAsync(ClientConnection connection, Opcode opcode, bool success, string message, CancellationToken ct)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 2 + messageBytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(3));
        return connection.SendAsync(opcode, payload, ct);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
