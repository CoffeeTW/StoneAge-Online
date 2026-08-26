using System.Net;
using System.Net.Sockets;
using System.Text;
using StoneAge.Network.Protocol;

namespace StoneAge.Network.Server;

public sealed class TcpGameServer(IClientPacketHandler packetHandler)
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);
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
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, Action<string> log, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var session = new GameSession();
        log($"Client connected: {endpoint} Session={session.SessionId}");

        try
        {
            await using var stream = client.GetStream();
            var helloPayload = Encoding.UTF8.GetBytes("StoneAge Online v0.1-11");
            await stream.WriteAsync(PacketCodec.Encode(Opcode.Hello, helloPayload), cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                PacketFrame? packet;
                try
                {
                    packet = await PacketReader.ReadAsync(stream, cancellationToken)
                        .WaitAsync(IdleTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    log($"Idle timeout: {endpoint} Session={session.SessionId}");
                    break;
                }

                if (packet is null)
                    break;

                session.Touch();
                log($"RX Opcode={packet.Opcode} Payload={packet.Payload.Length} Session={session.SessionId}");
                await packetHandler.HandleAsync(session, packet, stream, cancellationToken);
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
            if (packetHandler is IClientConnectionLifecycle lifecycle)
            {
                try
                {
                    await lifecycle.OnDisconnectedAsync(session, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log($"Disconnect cleanup error {endpoint}: {ex.Message}");
                }
            }

            client.Dispose();
            log($"Client disconnected: {endpoint} Session={session.SessionId}");
        }
    }
}
