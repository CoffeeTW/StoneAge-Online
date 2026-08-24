using System.Net;
using System.Net.Sockets;
using System.Text;
using StoneAge.Network.Protocol;

namespace StoneAge.Network.Server;

public sealed class TcpGameServer
{
    private TcpListener? _listener;

    public async Task RunAsync(int port, Action<string> log, CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        log($"TCP game server listening on 0.0.0.0:{port}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, log, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _listener.Stop();
        }
    }

    private static async Task HandleClientAsync(
        TcpClient client,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        log($"Client connected: {endpoint}");

        try
        {
            await using var stream = client.GetStream();
            var payload = Encoding.UTF8.GetBytes("StoneAge Online v0.1-01");
            var packet = PacketCodec.Encode(Opcode.Hello, payload);
            await stream.WriteAsync(packet, cancellationToken);

            var buffer = new byte[1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                log($"RX {read} bytes from {endpoint}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log($"Client error {endpoint}: {ex.Message}");
        }
        finally
        {
            client.Dispose();
            log($"Client disconnected: {endpoint}");
        }
    }
}
