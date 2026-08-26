namespace StoneAge.Network.Server;

public interface IClientConnectionLifecycle
{
    Task OnDisconnectedAsync(GameSession session, CancellationToken cancellationToken);

    Task OnDisconnectedAsync(ClientConnection connection, CancellationToken cancellationToken)
        => OnDisconnectedAsync(connection.Session, cancellationToken);
}
