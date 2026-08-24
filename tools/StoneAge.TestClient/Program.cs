using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using StoneAge.Network.Protocol;

const string host = "127.0.0.1";
const int port = 7021;

Console.WriteLine("StoneAge Online TestClient v0.1-02");
Console.WriteLine($"Connecting to {host}:{port} ...");

using var client = new TcpClient();
await client.ConnectAsync(host, port);
await using var stream = client.GetStream();

var hello = await ReadPacketAsync(stream);
Console.WriteLine($"Connected. Opcode={hello.Opcode} Payload=\"{Encoding.UTF8.GetString(hello.Payload)}\"");

Console.Write("Username [test]: ");
var username = Console.ReadLine();
if (string.IsNullOrWhiteSpace(username)) username = "test";

Console.Write("Password [test1234]: ");
var password = Console.ReadLine();
if (string.IsNullOrWhiteSpace(password)) password = "test1234";

var loginPayload = BuildLoginPayload(username, password);
await stream.WriteAsync(PacketCodec.Encode(Opcode.LoginRequest, loginPayload));

var response = await ReadPacketAsync(stream);
if (response.Opcode != Opcode.LoginResponse)
    throw new InvalidDataException($"Expected LoginResponse, got {response.Opcode}");

var success = response.Payload[0] == 1;
var accountId = BinaryPrimitives.ReadInt64LittleEndian(response.Payload.AsSpan(1, 8));
var messageLength = BinaryPrimitives.ReadUInt16LittleEndian(response.Payload.AsSpan(9, 2));
var message = Encoding.UTF8.GetString(response.Payload, 11, messageLength);

Console.WriteLine(success ? "Login successful." : "Login failed.");
Console.WriteLine($"AccountId : {accountId}");
Console.WriteLine($"Message   : {message}");
Console.WriteLine("Press Enter to disconnect.");
Console.ReadLine();

static byte[] BuildLoginPayload(string username, string password)
{
    var usernameBytes = Encoding.UTF8.GetBytes(username);
    var passwordBytes = Encoding.UTF8.GetBytes(password);
    var payload = new byte[2 + usernameBytes.Length + 2 + passwordBytes.Length];
    var offset = 0;

    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2), checked((ushort)usernameBytes.Length));
    offset += 2;
    usernameBytes.CopyTo(payload.AsSpan(offset));
    offset += usernameBytes.Length;

    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2), checked((ushort)passwordBytes.Length));
    offset += 2;
    passwordBytes.CopyTo(payload.AsSpan(offset));

    return payload;
}

static async Task<PacketFrame> ReadPacketAsync(NetworkStream stream)
{
    var header = new byte[PacketCodec.HeaderSize];
    await ReadExactlyAsync(stream, header);

    var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
    var opcode = (Opcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
    var payloadLength = length - PacketCodec.HeaderSize;
    var payload = new byte[payloadLength];
    await ReadExactlyAsync(stream, payload);
    return new PacketFrame(opcode, payload);
}

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
