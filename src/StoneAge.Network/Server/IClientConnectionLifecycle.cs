namespace StoneAge.Network.Server;

public interface IClientConnectionLifecycle
{
    Task OnDisconnectedAsync(ClientConnection connection, CancellationToken cancellationToken);
}
