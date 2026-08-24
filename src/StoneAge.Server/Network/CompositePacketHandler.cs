using System.Net.Sockets;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class CompositePacketHandler(
    LoginPacketHandler loginHandler,
    CharacterPacketHandler characterHandler,
    ILogger<CompositePacketHandler> logger) : IClientPacketHandler
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
            _ => HandleUnknownAsync(session, packet)
        };
    }

    private Task HandleUnknownAsync(GameSession session, PacketFrame packet)
    {
        logger.LogWarning(
            "Unhandled opcode {Opcode} SessionId={SessionId}",
            packet.Opcode,
            session.SessionId);

        return Task.CompletedTask;
    }
}
