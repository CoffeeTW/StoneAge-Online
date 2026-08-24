using System.Buffers.Binary;

namespace StoneAge.Network.Protocol;

public static class PacketCodec
{
    public const int HeaderSize = 4;

    public static byte[] Encode(Opcode opcode, ReadOnlySpan<byte> payload)
    {
        var length = checked((ushort)(HeaderSize + payload.Length));
        var buffer = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2, 2), (ushort)opcode);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }
}
