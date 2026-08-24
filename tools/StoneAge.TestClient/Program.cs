using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using StoneAge.Network.Protocol;

const string host = "127.0.0.1";
const int port = 7021;

Console.WriteLine("StoneAge Online TestClient v0.1-03");
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

await stream.WriteAsync(PacketCodec.Encode(Opcode.LoginRequest, BuildLoginPayload(username, password)));
var login = await ReadPacketAsync(stream);
var loginSuccess = login.Payload[0] == 1;
var accountId = BinaryPrimitives.ReadInt64LittleEndian(login.Payload.AsSpan(1, 8));
Console.WriteLine(loginSuccess ? $"Login successful. AccountId={accountId}" : "Login failed.");
if (!loginSuccess) return;

await ShowCharactersAsync(stream);

Console.WriteLine();
Console.WriteLine("Commands: list | create <name> | select <id> | quit");
while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine()?.Trim() ?? string.Empty;
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    if (input.Equals("list", StringComparison.OrdinalIgnoreCase))
    {
        await ShowCharactersAsync(stream);
        continue;
    }

    if (input.StartsWith("create ", StringComparison.OrdinalIgnoreCase))
    {
        var name = input[7..].Trim();
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var payload = new byte[2 + nameBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)nameBytes.Length));
        nameBytes.CopyTo(payload.AsSpan(2));
        await stream.WriteAsync(PacketCodec.Encode(Opcode.CharacterCreateRequest, payload));
        PrintResult(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("select ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[7..], out var characterId))
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, characterId);
        await stream.WriteAsync(PacketCodec.Encode(Opcode.CharacterSelectRequest, payload));
        PrintResult(await ReadPacketAsync(stream));
        continue;
    }

    Console.WriteLine("Unknown command.");
}

static async Task ShowCharactersAsync(NetworkStream stream)
{
    await stream.WriteAsync(PacketCodec.Encode(Opcode.CharacterListRequest, ReadOnlySpan<byte>.Empty));
    var response = await ReadPacketAsync(stream);
    var payload = response.Payload;
    var offset = 0;
    var count = payload[offset++];
    Console.WriteLine($"Characters ({count}):");
    for (var i = 0; i < count; i++)
    {
        var id = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2)); offset += 2;
        var name = Encoding.UTF8.GetString(payload, offset, nameLength); offset += nameLength;
        var level = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var mapId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var x = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset, 2)); offset += 2;
        var y = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset, 2)); offset += 2;
        Console.WriteLine($"  [{id}] {name} Lv.{level} Map={mapId} ({x},{y})");
    }
}

static void PrintResult(PacketFrame response)
{
    var success = response.Payload[0] == 1;
    var id = BinaryPrimitives.ReadInt64LittleEndian(response.Payload.AsSpan(1, 8));
    var messageLength = BinaryPrimitives.ReadUInt16LittleEndian(response.Payload.AsSpan(9, 2));
    var message = Encoding.UTF8.GetString(response.Payload, 11, messageLength);
    Console.WriteLine($"{(success ? "OK" : "FAIL")} Id={id} Message={message}");
}

static byte[] BuildLoginPayload(string username, string password)
{
    var usernameBytes = Encoding.UTF8.GetBytes(username);
    var passwordBytes = Encoding.UTF8.GetBytes(password);
    var payload = new byte[2 + usernameBytes.Length + 2 + passwordBytes.Length];
    var offset = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2), checked((ushort)usernameBytes.Length)); offset += 2;
    usernameBytes.CopyTo(payload.AsSpan(offset)); offset += usernameBytes.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2), checked((ushort)passwordBytes.Length)); offset += 2;
    passwordBytes.CopyTo(payload.AsSpan(offset));
    return payload;
}

static async Task<PacketFrame> ReadPacketAsync(NetworkStream stream)
{
    var header = new byte[PacketCodec.HeaderSize];
    await ReadExactlyAsync(stream, header);
    var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
    var opcode = (Opcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
    var payload = new byte[length - PacketCodec.HeaderSize];
    await ReadExactlyAsync(stream, payload);
    return new PacketFrame(opcode, payload);
}

static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer[offset..]);
        if (read == 0) throw new EndOfStreamException("Server disconnected.");
        offset += read;
    }
}
