using System.Net.Sockets;
using System.Runtime.CompilerServices;
using StoneAge.Network.Protocol;

namespace StoneAge.Network.Server;

public static class ConnectionSendGate
{
    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
    }

    private static readonly ConditionalWeakTable<NetworkStream, Gate> Gates = new();

    public static Task SendPacketAsync(
        NetworkStream stream,
        Opcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
        => SendAsync(stream, PacketCodec.Encode(opcode, payload.Span), cancellationToken);

    public static async Task SendAsync(NetworkStream stream, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        var gate = Gates.GetValue(stream, static _ => new Gate());
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(packet, cancellationToken);
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }
}
