using System.Net.Sockets;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class CompositePacketHandler(
    LoginPacketHandler loginHandler,
    CharacterPacketHandler characterHandler,
    WorldPacketHandler worldHandler,
    NpcPacketHandler npcHandler,
    InventoryShopPacketHandler inventoryShopHandler,
    ItemEquipmentPacketHandler itemEquipmentHandler,
    BattlePacketHandler battleHandler,
    PetPacketHandler petHandler,
    ILogger<CompositePacketHandler> logger) : IClientPacketHandler, IClientConnectionLifecycle
{
    public Task HandleAsync(
        GameSession session,
        PacketFrame packet,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        return packet.Opcode switch
        {
            Opcode.LoginRequest => loginHandler.HandleAsync(session, packet, stream, cancellationToken),
            Opcode.CharacterListRequest or
            Opcode.CharacterCreateRequest or
            Opcode.CharacterSelectRequest => characterHandler.HandleAsync(session, packet, stream, cancellationToken),
            Opcode.EnterWorld or Opcode.MoveRequest => worldHandler.HandleAsync(session, packet, stream, cancellationToken),
            Opcode.NpcListRequest or Opcode.NpcInteractRequest => npcHandler.HandleAsync(session, packet, stream, cancellationToken),
            Opcode.InventoryListRequest or
            Opcode.ShopListRequest or
            Opcode.ShopBuyRequest or
            Opcode.ShopSellRequest => inventoryShopHandler.HandleAsync(session, packet, stream, cancellationToken),
            Opcode.ItemUseRequest or
            Opcode.EquipmentListRequest or
            Opcode.ItemEquipRequest or
            Opcode.ItemUnequipRequest => itemEquipmentHandler.HandleAsync(session, packet, stream, cancellationToken),
            Opcode.BattleActionRequest => battleHandler.HandleAsync(session, packet, stream, cancellationToken),
            Opcode.PetListRequest or
            Opcode.PetActivateRequest or
            Opcode.PetRenameRequest or
            Opcode.PetReleaseRequest => petHandler.HandleAsync(session, packet, stream, cancellationToken),
            Opcode.Ping => stream.WriteAsync(PacketCodec.Encode(Opcode.Pong, packet.Payload), cancellationToken).AsTask(),
            _ => HandleUnknownAsync(session, packet)
        };
    }

    public Task OnDisconnectedAsync(GameSession session, CancellationToken cancellationToken)
        => worldHandler.DisconnectAsync(session);

    private Task HandleUnknownAsync(GameSession session, PacketFrame packet)
    {
        logger.LogWarning("Unhandled opcode {Opcode} SessionId={SessionId}", packet.Opcode, session.SessionId);
        return Task.CompletedTask;
    }
}
