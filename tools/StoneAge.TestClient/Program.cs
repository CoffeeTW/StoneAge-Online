using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using StoneAge.Network.Protocol;

const string host = "127.0.0.1";
const int port = 7021;

Console.WriteLine("StoneAge Online TestClient v0.1-13");
Console.WriteLine($"Connecting to {host}:{port} ...");

using var client = new TcpClient();
await client.ConnectAsync(host, port);
await using var stream = client.GetStream();
PrintPacket(await ReadPacketAsync(stream));

Console.Write("Username [test]: ");
var username = Console.ReadLine();
if (string.IsNullOrWhiteSpace(username)) username = "test";
Console.Write("Password [test1234]: ");
var password = Console.ReadLine();
if (string.IsNullOrWhiteSpace(password)) password = "test1234";

await stream.WriteAsync(PacketCodec.Encode(Opcode.LoginRequest, BuildLoginPayload(username, password)));
var login = await ReadPacketAsync(stream);
PrintPacket(login);
if (login.Opcode != Opcode.LoginResponse || login.Payload.Length < 1 || login.Payload[0] != 1) return;

Console.WriteLine("Commands: chars | create <name> | select <id> | enter | move <x> <y> <dir> | recv");
Console.WriteLine("Battle: attack | defend | escape | capture | petskill <slot>");
Console.WriteLine("Pets: pets | petactive <id> | petname <id> <name> | petrelease <id>");
Console.WriteLine("Pet skills: petskills <petId> | petlearn <petId> <skillId> <slot> | petforget <petId> <slot> | quit");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine()?.Trim() ?? string.Empty;
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    if (input.Equals("recv", StringComparison.OrdinalIgnoreCase))
    {
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.Equals("chars", StringComparison.OrdinalIgnoreCase))
    {
        await stream.WriteAsync(PacketCodec.Encode(Opcode.CharacterListRequest, ReadOnlySpan<byte>.Empty));
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("create ", StringComparison.OrdinalIgnoreCase))
    {
        var bytes = Encoding.UTF8.GetBytes(input[7..].Trim());
        var payload = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, checked((ushort)bytes.Length));
        bytes.CopyTo(payload.AsSpan(2));
        await stream.WriteAsync(PacketCodec.Encode(Opcode.CharacterCreateRequest, payload));
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("select ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[7..], out var characterId))
    {
        await SendInt64Async(stream, Opcode.CharacterSelectRequest, characterId);
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.Equals("enter", StringComparison.OrdinalIgnoreCase))
    {
        await stream.WriteAsync(PacketCodec.Encode(Opcode.EnterWorld, ReadOnlySpan<byte>.Empty));
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("move ", StringComparison.OrdinalIgnoreCase))
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4 && short.TryParse(parts[1], out var x) && short.TryParse(parts[2], out var y) && byte.TryParse(parts[3], out var direction))
        {
            var payload = new byte[5];
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(0, 2), x);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2, 2), y);
            payload[4] = direction;
            await stream.WriteAsync(PacketCodec.Encode(Opcode.MoveRequest, payload));
            PrintPacket(await ReadPacketAsync(stream));
            Console.WriteLine("If an encounter also started, use 'recv' to read BattleStart.");
        }
        continue;
    }

    var battleAction = input.ToLowerInvariant() switch
    {
        "attack" => (byte)1,
        "defend" => (byte)2,
        "escape" => (byte)3,
        "capture" => (byte)4,
        _ => (byte)0
    };
    if (battleAction != 0)
    {
        await stream.WriteAsync(PacketCodec.Encode(Opcode.BattleActionRequest, new[] { battleAction }));
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("petskill ", StringComparison.OrdinalIgnoreCase) && byte.TryParse(input[9..], out var battlePetSkillSlot))
    {
        await stream.WriteAsync(PacketCodec.Encode(Opcode.BattlePetSkillSelectRequest, new[] { battlePetSkillSlot }));
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.Equals("pets", StringComparison.OrdinalIgnoreCase))
    {
        await stream.WriteAsync(PacketCodec.Encode(Opcode.PetListRequest, ReadOnlySpan<byte>.Empty));
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("petactive ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[10..], out var activePetId))
    {
        await SendInt64Async(stream, Opcode.PetActivateRequest, activePetId);
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("petrelease ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[11..], out var releasePetId))
    {
        await SendInt64Async(stream, Opcode.PetReleaseRequest, releasePetId);
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("petname ", StringComparison.OrdinalIgnoreCase))
    {
        var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && long.TryParse(parts[1], out var petId))
        {
            var nameBytes = Encoding.UTF8.GetBytes(parts[2]);
            var payload = new byte[10 + nameBytes.Length];
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), petId);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), checked((ushort)nameBytes.Length));
            nameBytes.CopyTo(payload.AsSpan(10));
            await stream.WriteAsync(PacketCodec.Encode(Opcode.PetRenameRequest, payload));
            PrintPacket(await ReadPacketAsync(stream));
        }
        continue;
    }

    if (input.StartsWith("petskills ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[10..], out var skillPetId))
    {
        await SendInt64Async(stream, Opcode.PetSkillListRequest, skillPetId);
        PrintPacket(await ReadPacketAsync(stream));
        continue;
    }

    if (input.StartsWith("petlearn ", StringComparison.OrdinalIgnoreCase))
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4 && long.TryParse(parts[1], out var learnPetId) && int.TryParse(parts[2], out var skillId) && byte.TryParse(parts[3], out var slot))
        {
            var payload = new byte[13];
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), learnPetId);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), skillId);
            payload[12] = slot;
            await stream.WriteAsync(PacketCodec.Encode(Opcode.PetSkillLearnRequest, payload));
            PrintPacket(await ReadPacketAsync(stream));
        }
        continue;
    }

    if (input.StartsWith("petforget ", StringComparison.OrdinalIgnoreCase))
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && long.TryParse(parts[1], out var forgetPetId) && byte.TryParse(parts[2], out var slot))
        {
            var payload = new byte[9];
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), forgetPetId);
            payload[8] = slot;
            await stream.WriteAsync(PacketCodec.Encode(Opcode.PetSkillForgetRequest, payload));
            PrintPacket(await ReadPacketAsync(stream));
        }
        continue;
    }

    Console.WriteLine("Unknown command.");
}

static async Task SendInt64Async(NetworkStream stream, Opcode opcode, long value)
{
    var payload = new byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(payload, value);
    await stream.WriteAsync(PacketCodec.Encode(opcode, payload));
}

static void PrintPacket(PacketFrame packet)
{
    Console.WriteLine($"RX {packet.Opcode} ({packet.Payload.Length} bytes)");
    if (packet.Opcode == Opcode.Hello)
        Console.WriteLine(Encoding.UTF8.GetString(packet.Payload));
    else if (packet.Opcode is Opcode.LoginResponse or Opcode.CharacterCreateResponse or Opcode.CharacterSelectResponse)
        PrintLegacyResult(packet.Payload);
    else if (packet.Opcode is Opcode.PetActivateResponse or Opcode.PetRenameResponse or Opcode.PetReleaseResponse or Opcode.PetSkillLearnResponse or Opcode.PetSkillForgetResponse)
        PrintSimpleResult(packet.Payload);
    else if (packet.Opcode == Opcode.BattlePetSkillSelectResponse)
        PrintBattlePetSkillResult(packet.Payload);
    else if (packet.Opcode == Opcode.PetListResponse)
        PrintPets(packet.Payload);
    else if (packet.Opcode == Opcode.PetSkillListResponse)
        PrintPetSkills(packet.Payload);
    else if (packet.Opcode == Opcode.BattleStart)
        PrintBattleStart(packet.Payload);
    else if (packet.Opcode == Opcode.BattleTurnResult)
        PrintBattleTurn(packet.Payload);
    else if (packet.Opcode == Opcode.BattleEnd)
        PrintBattleEnd(packet.Payload);
}

static void PrintPets(byte[] payload)
{
    var offset = 0;
    var count = payload[offset++];
    Console.WriteLine($"Pets ({count}):");
    for (var i = 0; i < count; i++)
    {
        var id = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
        var monsterId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var name = ReadString(payload, ref offset);
        var level = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var exp = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
        var hp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var maxHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var atk = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var def = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var agi = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var loyalty = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var earth = payload[offset++]; var water = payload[offset++]; var fire = payload[offset++]; var wind = payload[offset++];
        var active = payload[offset++] == 1;
        Console.WriteLine($"  [{id}] {name} Monster={monsterId} Lv.{level} EXP={exp} HP={hp}/{maxHp} ATK={atk} DEF={def} AGI={agi} Loyalty={loyalty} E/W/F/W={earth}/{water}/{fire}/{wind} {(active ? "[ACTIVE]" : "")}");
    }
}

static void PrintPetSkills(byte[] payload)
{
    if (payload.Length < 3)
        return;

    if (payload[0] != 1)
    {
        PrintSimpleResult(payload);
        return;
    }

    var offset = 1;
    var petId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
    var count = payload[offset++];
    Console.WriteLine($"Pet #{petId} skills ({count}):");
    for (var i = 0; i < count; i++)
    {
        var slot = payload[offset++];
        var skillId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var name = ReadString(payload, ref offset);
        var power = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var element = ReadString(payload, ref offset);
        Console.WriteLine($"  Slot {slot}: {name} (SkillId={skillId}, Power={power}%, Element={element})");
    }
}

static void PrintBattlePetSkillResult(byte[] payload)
{
    if (payload.Length < 8) return;
    var success = payload[0] == 1;
    var slot = payload[1];
    var skillId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(2, 4));
    var len = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
    var message = payload.Length >= 8 + len ? Encoding.UTF8.GetString(payload, 8, len) : string.Empty;
    Console.WriteLine($"{(success ? "OK" : "FAIL")} PetSkill Slot={slot} SkillId={skillId} {message}");
}

static void PrintBattleStart(byte[] payload)
{
    var offset = 0;
    var monsterId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
    var name = ReadString(payload, ref offset);
    var level = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
    var playerHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
    var monsterHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
    var monsterMaxHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
    offset += 5;
    var hasPet = payload[offset++] == 1;
    Console.WriteLine($"BATTLE: {name}#{monsterId} Lv.{level} PlayerHP={playerHp} MonsterHP={monsterHp}/{monsterMaxHp}");
    if (hasPet)
    {
        var petId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
        var petName = ReadString(payload, ref offset);
        var petLevel = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var petHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var petMaxHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var loyalty = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
        var selectedSkillId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
        Console.WriteLine($"PET: {petName}#{petId} Lv.{petLevel} HP={petHp}/{petMaxHp} Loyalty={loyalty} SelectedSkill={selectedSkillId}");
    }
}

static void PrintBattleTurn(byte[] payload)
{
    Console.WriteLine($"Action={payload[0]} PlayerDMG={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1,4))} PetDMG={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5,4))} MonsterDMG={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(9,4))} PlayerHP={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(13,4))} PetHP={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(17,4))} MonsterHP={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(21,4))}");
}

static void PrintBattleEnd(byte[] payload)
{
    var result = payload[0];
    var exp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1,4));
    var levels = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5,4));
    var petLevels = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(25,4));
    Console.WriteLine($"BATTLE END result={result} EXP={exp} PlayerLevels={levels} PetLevels={petLevels}");
}

static void PrintLegacyResult(byte[] payload)
{
    if (payload.Length < 11) return;
    var success = payload[0] == 1;
    var id = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(1, 8));
    var len = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(9, 2));
    Console.WriteLine($"{(success ? "OK" : "FAIL")} Id={id} {Encoding.UTF8.GetString(payload, 11, len)}");
}

static void PrintSimpleResult(byte[] payload)
{
    if (payload.Length < 3) return;
    var len = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1, 2));
    Console.WriteLine($"{(payload[0] == 1 ? "OK" : "FAIL")} {Encoding.UTF8.GetString(payload, 3, len)}");
}

static string ReadString(byte[] payload, ref int offset)
{
    var len = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2)); offset += 2;
    var value = Encoding.UTF8.GetString(payload, offset, len); offset += len;
    return value;
}

static byte[] BuildLoginPayload(string username, string password)
{
    var user = Encoding.UTF8.GetBytes(username);
    var pass = Encoding.UTF8.GetBytes(password);
    var payload = new byte[2 + user.Length + 2 + pass.Length];
    var offset = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2), checked((ushort)user.Length)); offset += 2;
    user.CopyTo(payload.AsSpan(offset)); offset += user.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset, 2), checked((ushort)pass.Length)); offset += 2;
    pass.CopyTo(payload.AsSpan(offset));
    return payload;
}

static async Task<PacketFrame> ReadPacketAsync(NetworkStream stream)
{
    var header = new byte[PacketCodec.HeaderSize];
    await ReadExactlyAsync(stream, header);
    var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
    if (length < PacketCodec.HeaderSize) throw new InvalidDataException("Invalid packet length.");
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
