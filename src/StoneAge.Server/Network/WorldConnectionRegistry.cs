using System.Collections.Concurrent;
using System.Net.Sockets;
using StoneAge.Network.Server;

namespace StoneAge.Server.Network;

public sealed class WorldConnectionRegistry
{
    private readonly ConcurrentDictionary<long, NetworkStream> _peers = new();

    public bool Register(long characterId, NetworkStream stream)
        => _peers.TryAdd(characterId, stream);

    public void Unregister(long characterId)
        => _peers.TryRemove(characterId, out _);

    public async Task SendAsync(long characterId, byte[] packet, CancellationToken cancellationToken)
    {
        if (!_peers.TryGetValue(characterId, out var stream))
            return;

        await ConnectionSendGate.SendAsync(stream, packet, cancellationToken);
    }
}
