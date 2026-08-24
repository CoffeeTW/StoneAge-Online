using System.Collections.Concurrent;
using System.Net.Sockets;

namespace StoneAge.Server.Network;

public sealed class WorldConnectionRegistry
{
    private sealed class Peer(NetworkStream stream)
    {
        public NetworkStream Stream { get; } = stream;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<long, Peer> _peers = new();

    public bool Register(long characterId, NetworkStream stream)
        => _peers.TryAdd(characterId, new Peer(stream));

    public void Unregister(long characterId)
        => _peers.TryRemove(characterId, out _);

    public async Task SendAsync(long characterId, byte[] packet, CancellationToken cancellationToken)
    {
        if (!_peers.TryGetValue(characterId, out var peer))
            return;

        await peer.SendLock.WaitAsync(cancellationToken);
        try
        {
            await peer.Stream.WriteAsync(packet, cancellationToken);
        }
        finally
        {
            peer.SendLock.Release();
        }
    }
}
