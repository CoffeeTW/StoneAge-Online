using System.Net.Sockets;
using StoneAge.Network.Protocol;

namespace StoneAge.Network.Server;

public interface IClientPacketHandler
{
    Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken cancellationToken);
}
