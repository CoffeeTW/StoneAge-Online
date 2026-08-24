using System.Buffers.Binary;
using System.Net.Sockets;

namespace StoneAge.Network.Protocol;

public static class PacketReader
{
    public const int MaxPacketSize = ushort.MaxValue;

    public static async Task<PacketFrame?> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[PacketCodec.HeaderSize];
        var headerRead = await ReadExactlyOrEofAsync(stream, header, cancellationToken);
        if (!headerRead)
            return null;

        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        if (length < PacketCodec.HeaderSize || length > MaxPacketSize)
            throw new InvalidDataException($"Invalid packet length: {length}");

        var opcode = (Opcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        var payloadLength = length - PacketCodec.HeaderSize;
        var payload = new byte[payloadLength];

        if (payloadLength > 0)
            await ReadExactlyAsync(stream, payload, cancellationToken);

        return new PacketFrame(opcode, payload);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                if (offset == 0)
                    return false;

                throw new EndOfStreamException("Connection closed in the middle of a packet header.");
            }

            offset += read;
        }

        return true;
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Connection closed in the middle of a packet payload.");

            offset += read;
        }
    }
}
