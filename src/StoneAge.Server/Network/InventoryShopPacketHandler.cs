using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Domain.Entities;
using StoneAge.Game.Item;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class InventoryShopPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    ItemCatalog items,
    ILogger<InventoryShopPacketHandler> logger) : IClientPacketHandler
{
    public Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (session.State != SessionState.InWorld || session.CharacterId is null)
            return Task.CompletedTask;

        return packet.Opcode switch
        {
            Opcode.InventoryListRequest => SendInventoryAsync(session.CharacterId.Value, stream, cancellationToken),
            Opcode.ShopListRequest => SendShopListAsync(stream, cancellationToken),
            Opcode.ShopBuyRequest => BuyAsync(session.CharacterId.Value, packet.Payload, stream, cancellationToken),
            Opcode.ShopSellRequest => SellAsync(session.CharacterId.Value, packet.Payload, stream, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task SendInventoryAsync(long characterId, NetworkStream stream, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var character = await db.Characters.AsNoTracking().SingleAsync(x => x.Id == characterId, ct);
        var inventory = await db.CharacterItems.AsNoTracking().Where(x => x.CharacterId == characterId).OrderBy(x => x.Id).ToListAsync(ct);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, true);
        writer.Write(character.Stone);
        writer.Write(checked((ushort)inventory.Count));
        foreach (var row in inventory)
        {
            writer.Write(row.Id);
            writer.Write(row.ItemId);
            writer.Write(row.Quantity);
        }
        await stream.WriteAsync(PacketCodec.Encode(Opcode.InventoryListResponse, ms.ToArray()), ct);
    }

    private async Task SendShopListAsync(NetworkStream stream, CancellationToken ct)
    {
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
        }
        await stream.WriteAsync(PacketCodec.Encode(Opcode.ShopListResponse, ms.ToArray()), ct);
    }

    private async Task BuyAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (!TryReadTrade(payload, out var itemId, out var quantity) || quantity <= 0 || !items.TryGet(itemId, out var item) || item is null)
        {
            await SendTradeResultAsync(stream, Opcode.ShopBuyResponse, false, "Invalid purchase.", ct);
            return;
        }

        var total = checked(item.BuyPrice * quantity);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var character = await db.Characters.SingleAsync(x => x.Id == characterId, ct);
        if (character.Stone < total)
        {
            await SendTradeResultAsync(stream, Opcode.ShopBuyResponse, false, "Not enough Stone.", ct);
            return;
        }

        var row = await db.CharacterItems.SingleOrDefaultAsync(x => x.CharacterId == characterId && x.ItemId == itemId, ct);
        var current = row?.Quantity ?? 0;
        if (current + quantity > item.MaxStack)
        {
            await SendTradeResultAsync(stream, Opcode.ShopBuyResponse, false, "Stack limit reached.", ct);
            return;
        }

        character.Stone -= total;
        if (row is null)
            db.CharacterItems.Add(new CharacterItem { CharacterId = characterId, ItemId = itemId, Quantity = quantity });
        else
        {
            row.Quantity += quantity;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Shop buy CharacterId={CharacterId} ItemId={ItemId} Quantity={Quantity} Total={Total}", characterId, itemId, quantity, total);
        await SendTradeResultAsync(stream, Opcode.ShopBuyResponse, true, "Purchase complete.", ct);
    }

    private async Task SellAsync(long characterId, byte[] payload, NetworkStream stream, CancellationToken ct)
    {
        if (!TryReadTrade(payload, out var itemId, out var quantity) || quantity <= 0 || !items.TryGet(itemId, out var item) || item is null)
        {
            await SendTradeResultAsync(stream, Opcode.ShopSellResponse, false, "Invalid sale.", ct);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var character = await db.Characters.SingleAsync(x => x.Id == characterId, ct);
        var row = await db.CharacterItems.SingleOrDefaultAsync(x => x.CharacterId == characterId && x.ItemId == itemId, ct);
        if (row is null || row.Quantity < quantity)
        {
            await SendTradeResultAsync(stream, Opcode.ShopSellResponse, false, "Not enough items.", ct);
            return;
        }

        var total = checked(item.SellPrice * quantity);
        row.Quantity -= quantity;
        character.Stone = checked(character.Stone + total);
        if (row.Quantity == 0) db.CharacterItems.Remove(row);
        else row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Shop sell CharacterId={CharacterId} ItemId={ItemId} Quantity={Quantity} Total={Total}", characterId, itemId, quantity, total);
        await SendTradeResultAsync(stream, Opcode.ShopSellResponse, true, "Sale complete.", ct);
    }

    private static bool TryReadTrade(byte[] payload, out int itemId, out int quantity)
    {
        itemId = 0; quantity = 0;
        if (payload.Length != 8) return false;
        itemId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        quantity = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        return true;
    }

    private static async Task SendTradeResultAsync(NetworkStream stream, Opcode opcode, bool success, string message, CancellationToken ct)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 2 + messageBytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(3));
        await stream.WriteAsync(PacketCodec.Encode(opcode, payload), ct);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
