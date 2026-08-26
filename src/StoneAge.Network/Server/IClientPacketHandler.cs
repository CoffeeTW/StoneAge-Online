using System.Net.Sockets;
using StoneAge.Network.Protocol;

namespace StoneAge.Network.Server;

public interface IClientPacketHandler
{
    Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken cancellationToken);

    Task HandleAsync(ClientConnection connection, PacketFrame packet, CancellationToken cancellationToken)
        => HandleAsync(connection.Session, packet, connection.Stream, cancellationToken);
}
