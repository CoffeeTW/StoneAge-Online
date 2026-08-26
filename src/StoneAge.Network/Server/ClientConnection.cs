using System.Net.Sockets;
using StoneAge.Network.Protocol;

namespace StoneAge.Network.Server;

public sealed class ClientConnection : IAsyncDisposable
{
    private readonly bool _ownsTransport;

    public ClientConnection(TcpClient client)
    {
        Client = client;
        Session = new GameSession();
        Stream = client.GetStream();
        _ownsTransport = true;
    }

    public ClientConnection(NetworkStream stream, GameSession session)
    {
        Stream = stream;
        Session = session;
        _ownsTransport = false;
    }

    public TcpClient? Client { get; }
    public GameSession Session { get; }
    public NetworkStream Stream { get; }
    public string RemoteEndpoint => Client?.Client.RemoteEndPoint?.ToString() ?? "legacy";

    public Task SendAsync(Opcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => ConnectionSendGate.SendPacketAsync(Stream, opcode, payload, cancellationToken);

    public Task SendEncodedAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        => ConnectionSendGate.SendAsync(Stream, packet, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (!_ownsTransport)
            return;

        await Stream.DisposeAsync();
        Client?.Dispose();
    }
}
