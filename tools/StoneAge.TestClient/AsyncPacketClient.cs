using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using StoneAge.Network.Protocol;

internal sealed class AsyncPacketClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Opcode, ConcurrentQueue<TaskCompletionSource<PacketFrame>>> _waiters = new();
    private NetworkStream? _stream;
    private Task? _receiveLoop;

    public event Action<PacketFrame>? UnsolicitedPacket;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(host, port, cancellationToken);
        _client.NoDelay = true;
        _stream = _client.GetStream();
        _receiveLoop = ReceiveLoopAsync(_stop.Token);
    }

    public Task<PacketFrame> WaitForAsync(Opcode opcode, CancellationToken cancellationToken = default)
    {
        var waiter = new TaskCompletionSource<PacketFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = _waiters.GetOrAdd(opcode, static _ => new ConcurrentQueue<TaskCompletionSource<PacketFrame>>());
        queue.Enqueue(waiter);
        return waiter.Task.WaitAsync(cancellationToken);
    }

    public async Task<PacketFrame> RequestAsync(
        Opcode requestOpcode,
        ReadOnlyMemory<byte> payload,
        Opcode responseOpcode,
        CancellationToken cancellationToken = default)
    {
        var response = WaitForAsync(responseOpcode, cancellationToken);
        await SendAsync(requestOpcode, payload, cancellationToken);
        return await response;
    }

    public async Task SendAsync(Opcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
            throw new InvalidOperationException("Client is not connected.");

        await _stream.WriteAsync(PacketCodec.Encode(opcode, payload.Span), cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await ReadPacketAsync(cancellationToken);
                if (_waiters.TryGetValue(packet.Opcode, out var queue) && queue.TryDequeue(out var waiter))
                {
                    waiter.TrySetResult(packet);
                    continue;
                }

                PartyBattleConsole.TryPrint(packet);
                UnsolicitedPacket?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            foreach (var queue in _waiters.Values)
            {
                while (queue.TryDequeue(out var waiter))
                    waiter.TrySetException(ex);
            }

            Console.WriteLine($"\nReceiver stopped: {ex.Message}");
        }
    }

    private async Task<PacketFrame> ReadPacketAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
            throw new InvalidOperationException("Client is not connected.");

        var header = new byte[PacketCodec.HeaderSize];
        await ReadExactlyAsync(_stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        if (length < PacketCodec.HeaderSize)
            throw new InvalidDataException("Invalid packet length.");

        var opcode = (Opcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        var payload = new byte[length - PacketCodec.HeaderSize];
        await ReadExactlyAsync(_stream, payload, cancellationToken);
        return new PacketFrame(opcode, payload);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Server disconnected.");
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _stream?.Close();
        _client.Dispose();

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; }
            catch { }
        }

        _stop.Dispose();
    }
}
