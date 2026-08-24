namespace StoneAge.Network.Server;

public interface IClientConnectionLifecycle
{
    Task OnDisconnectedAsync(GameSession session, CancellationToken cancellationToken);
}
