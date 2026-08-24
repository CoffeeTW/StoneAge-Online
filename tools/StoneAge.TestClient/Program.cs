using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using StoneAge.Network.Protocol;

const string host = "127.0.0.1";
const int port = 7021;

Console.WriteLine("StoneAge Online TestClient v0.1-01");
Console.WriteLine($"Connecting to {host}:{port} ...");

using var client = new TcpClient();
await client.ConnectAsync(host, port);
await using var stream = client.GetStream();

var header = new byte[PacketCodec.HeaderSize];
await ReadExactlyAsync(stream, header);

var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
var opcode = (Opcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
var payloadLength = length - PacketCodec.HeaderSize;
var payload = new byte[payloadLength];
await ReadExactlyAsync(stream, payload);

Console.WriteLine($"Connected. Opcode={opcode} Payload=\"{Encoding.UTF8.GetString(payload)}\"");
Console.WriteLine("Press Enter to disconnect.");
Console.ReadLine();

static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer[offset..]);
        if (read == 0)
            throw new EndOfStreamException("Server disconnected.");
        offset += read;
    }
}
