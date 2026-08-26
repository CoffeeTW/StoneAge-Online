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
    SocialPacketHandler socialHandler,
    ILogger<CompositePacketHandler> logger) : IClientPacketHandler, IClientConnectionLifecycle
{
    public Task HandleAsync(
        ClientConnection connection,
        PacketFrame packet,
        CancellationToken cancellationToken)
    {
        return packet.Opcode switch
        {
            Opcode.LoginRequest => loginHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.CharacterListRequest or
            Opcode.CharacterCreateRequest or
            Opcode.CharacterSelectRequest => characterHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.EnterWorld or Opcode.MoveRequest => worldHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.NpcListRequest or Opcode.NpcInteractRequest => npcHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.InventoryListRequest or
            Opcode.ShopListRequest or
            Opcode.ShopBuyRequest or
            Opcode.ShopSellRequest => inventoryShopHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.ItemUseRequest or
            Opcode.EquipmentListRequest or
            Opcode.ItemEquipRequest or
            Opcode.ItemUnequipRequest => itemEquipmentHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.BattleActionRequest or
            Opcode.BattlePetSkillSelectRequest => battleHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.PetListRequest or
            Opcode.PetActivateRequest or
            Opcode.PetRenameRequest or
            Opcode.PetReleaseRequest or
            Opcode.PetHealRequest or
            Opcode.PetReviveRequest => petHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.PetSkillListRequest or
            Opcode.PetSkillLearnRequest or
            Opcode.PetSkillForgetRequest => petSkillHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.ChatSayRequest or
            Opcode.PartyInviteRequest or
            Opcode.PartyAnswerRequest or
            Opcode.PartyLeaveRequest => socialHandler.HandleAsync(connection, packet, cancellationToken),
            Opcode.Ping => connection.SendAsync(Opcode.Pong, packet.Payload, cancellationToken),
            _ => HandleUnknownAsync(connection.Session, packet)
        };
    }

    public async Task OnDisconnectedAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        var characterId = connection.Session.CharacterId;
        await worldHandler.DisconnectAsync(connection.Session);
        if (characterId is long id)
            await socialHandler.OnDisconnectedAsync(id, cancellationToken);
    }

    private Task HandleUnknownAsync(GameSession session, PacketFrame packet)
    {
        logger.LogWarning("Unhandled opcode {Opcode} SessionId={SessionId}", packet.Opcode, session.SessionId);
        return Task.CompletedTask;
    }
}
