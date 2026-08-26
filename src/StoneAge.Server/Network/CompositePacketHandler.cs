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
    PetSkillPacketHandler petSkillHandler,
    ILogger<CompositePacketHandler> logger) : IClientPacketHandler, IClientConnectionLifecycle
{
    public Task HandleAsync(
        ClientConnection connection,
        PacketFrame packet,
        CancellationToken cancellationToken)
    {
        return packet.Opcode switch
        {
            Opcode.LoginRequest => DispatchAsync(loginHandler, connection, packet, cancellationToken),
            Opcode.CharacterListRequest or
            Opcode.CharacterCreateRequest or
            Opcode.CharacterSelectRequest => DispatchAsync(characterHandler, connection, packet, cancellationToken),
            Opcode.EnterWorld or Opcode.MoveRequest => worldHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.NpcListRequest or Opcode.NpcInteractRequest => DispatchAsync(npcHandler, connection, packet, cancellationToken),
            Opcode.InventoryListRequest or
            Opcode.ShopListRequest or
            Opcode.ShopBuyRequest or
            Opcode.ShopSellRequest => DispatchAsync(inventoryShopHandler, connection, packet, cancellationToken),
            Opcode.ItemUseRequest or
            Opcode.EquipmentListRequest or
            Opcode.ItemEquipRequest or
            Opcode.ItemUnequipRequest => DispatchAsync(itemEquipmentHandler, connection, packet, cancellationToken),
            Opcode.BattleActionRequest or
            Opcode.BattlePetSkillSelectRequest => DispatchAsync(battleHandler, connection, packet, cancellationToken),
            Opcode.PetListRequest or
            Opcode.PetActivateRequest or
            Opcode.PetRenameRequest or
            Opcode.PetReleaseRequest => DispatchAsync(petHandler, connection, packet, cancellationToken),
            Opcode.PetSkillListRequest or
            Opcode.PetSkillLearnRequest or
            Opcode.PetSkillForgetRequest => DispatchAsync(petSkillHandler, connection, packet, cancellationToken),
            Opcode.Ping => connection.SendAsync(Opcode.Pong, packet.Payload, cancellationToken),
            _ => HandleUnknownAsync(connection.Session, packet)
        };
    }

    public Task HandleAsync(
        GameSession session,
        PacketFrame packet,
        NetworkStream stream,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("CompositePacketHandler requires ClientConnection.");

    public Task OnDisconnectedAsync(ClientConnection connection, CancellationToken cancellationToken)
        => worldHandler.DisconnectAsync(connection.Session);

    public Task OnDisconnectedAsync(GameSession session, CancellationToken cancellationToken)
        => worldHandler.DisconnectAsync(session);

    private static Task DispatchAsync(
        IClientPacketHandler handler,
        ClientConnection connection,
        PacketFrame packet,
        CancellationToken cancellationToken)
        => handler.HandleAsync(connection, packet, cancellationToken);

    private Task HandleUnknownAsync(GameSession session, PacketFrame packet)
    {
        logger.LogWarning("Unhandled opcode {Opcode} SessionId={SessionId}", packet.Opcode, session.SessionId);
        return Task.CompletedTask;
    }
}
