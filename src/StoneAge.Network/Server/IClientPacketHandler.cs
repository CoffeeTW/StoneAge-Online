using StoneAge.Network.Protocol;

namespace StoneAge.Network.Server;

public interface IClientPacketHandler
{
    Task HandleAsync(ClientConnection connection, PacketFrame packet, CancellationToken cancellationToken);
}
