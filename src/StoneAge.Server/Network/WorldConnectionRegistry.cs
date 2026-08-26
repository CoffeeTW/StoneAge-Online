using System.Collections.Concurrent;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class WorldConnectionRegistry
{
    private readonly ConcurrentDictionary<long, ClientConnection> _peers = new();

    public bool Register(long characterId, ClientConnection connection)
        => _peers.TryAdd(characterId, connection);

    public void Unregister(long characterId)
        => _peers.TryRemove(characterId, out _);

    public async Task SendAsync(long characterId, byte[] packet, CancellationToken cancellationToken)
    {
        if (!_peers.TryGetValue(characterId, out var connection))
            return;

        await connection.SendEncodedAsync(packet, cancellationToken);
    }
}
