using System.Buffers.Binary;
using System.Text;
using StoneAge.Network.Protocol;

const string host = "127.0.0.1";
const int port = 7021;

Console.WriteLine("StoneAge Online TestClient v0.1-16");
Console.WriteLine($"Connecting to {host}:{port} ...");

await using var client = new AsyncPacketClient();
client.UnsolicitedPacket += packet =>
{
    Console.WriteLine();
    PrintPacket(packet);
    Console.Write("> ");
};

var helloTask = client.WaitForAsync(Opcode.Hello);
await client.ConnectAsync(host, port);
PrintPacket(await helloTask);

Console.Write("Username [test]: ");
var username = Console.ReadLine();
if (string.IsNullOrWhiteSpace(username)) username = "test";
Console.Write("Password [test1234]: ");
var password = Console.ReadLine();
if (string.IsNullOrWhiteSpace(password)) password = "test1234";

var login = await client.RequestAsync(
    Opcode.LoginRequest,
    BuildLoginPayload(username, password),
    Opcode.LoginResponse);
PrintPacket(login);
if (login.Payload.Length < 1 || login.Payload[0] != 1)
    return;

Console.WriteLine("Commands: chars | create <name> | select <id> | enter | move <x> <y> <dir>");
Console.WriteLine("Social: say <text> | pinvite <characterId> | paccept <inviterId> | preject <inviterId> | pleave");
Console.WriteLine("Battle: attack | defend | escape | capture | petskill <slot>");
Console.WriteLine("Pets: pets | petactive <id> | petname <id> <name> | petrelease <id> | petheal <id> | petrevive <id>");
Console.WriteLine("Pet skills: petskills <petId> | petlearn <petId> <skillId> <slot> | petforget <petId> <slot> | quit");
Console.WriteLine("Broadcasts, chat, party events, and battle events are received automatically.");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine()?.Trim() ?? string.Empty;
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.Equals("chars", StringComparison.OrdinalIgnoreCase))
    {
        PrintPacket(await client.RequestAsync(Opcode.CharacterListRequest, ReadOnlyMemory<byte>.Empty, Opcode.CharacterListResponse));
        continue;
    }

    if (input.StartsWith("create ", StringComparison.OrdinalIgnoreCase))
    {
        var bytes = Encoding.UTF8.GetBytes(input[7..].Trim());
        var payload = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, checked((ushort)bytes.Length));
        bytes.CopyTo(payload.AsSpan(2));
        PrintPacket(await client.RequestAsync(Opcode.CharacterCreateRequest, payload, Opcode.CharacterCreateResponse));
        continue;
    }

    if (input.StartsWith("select ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[7..], out var characterId))
    {
        PrintPacket(await client.RequestAsync(Opcode.CharacterSelectRequest, BuildInt64Payload(characterId), Opcode.CharacterSelectResponse));
        continue;
    }

    if (input.Equals("enter", StringComparison.OrdinalIgnoreCase))
    {
        PrintPacket(await client.RequestAsync(Opcode.EnterWorld, ReadOnlyMemory<byte>.Empty, Opcode.EnterWorld));
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
            PrintPacket(await client.RequestAsync(Opcode.MoveRequest, payload, Opcode.MoveResponse));
        }
        else Console.WriteLine("Usage: move <x> <y> <dir>");
        continue;
    }

    if (input.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
    {
        var textBytes = Encoding.UTF8.GetBytes(input[4..].Trim());
        if (textBytes.Length is 0 or > 200)
        {
            Console.WriteLine("Chat text must be 1-200 UTF-8 bytes.");
            continue;
        }
        var payload = new byte[2 + textBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)textBytes.Length));
        textBytes.CopyTo(payload.AsSpan(2));
        await client.SendAsync(Opcode.ChatSayRequest, payload);
        continue;
    }

    if (input.StartsWith("pinvite ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[8..], out var inviteTargetId))
    {
        PrintPacket(await client.RequestAsync(Opcode.PartyInviteRequest, BuildInt64Payload(inviteTargetId), Opcode.PartyInviteResponse));
        continue;
    }

    if ((input.StartsWith("paccept ", StringComparison.OrdinalIgnoreCase) || input.StartsWith("preject ", StringComparison.OrdinalIgnoreCase)))
    {
        var accepting = input.StartsWith("paccept ", StringComparison.OrdinalIgnoreCase);
        var rawId = input[(accepting ? 8 : 8)..];
        if (long.TryParse(rawId, out var inviterId))
        {
            var payload = new byte[9];
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), inviterId);
            payload[8] = accepting ? (byte)1 : (byte)0;
            PrintPacket(await client.RequestAsync(Opcode.PartyAnswerRequest, payload, Opcode.PartyAnswerResponse));
        }
        continue;
    }

    if (input.Equals("pleave", StringComparison.OrdinalIgnoreCase))
    {
        PrintPacket(await client.RequestAsync(Opcode.PartyLeaveRequest, ReadOnlyMemory<byte>.Empty, Opcode.PartyLeaveResponse));
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
        await client.SendAsync(Opcode.BattleActionRequest, new[] { battleAction });
        continue;
    }

    if (input.StartsWith("petskill ", StringComparison.OrdinalIgnoreCase) && byte.TryParse(input[9..], out var battleSkillSlot))
    {
        PrintPacket(await client.RequestAsync(Opcode.BattlePetSkillSelectRequest, new[] { battleSkillSlot }, Opcode.BattlePetSkillSelectResponse));
        continue;
    }

    if (input.Equals("pets", StringComparison.OrdinalIgnoreCase))
    {
        PrintPacket(await client.RequestAsync(Opcode.PetListRequest, ReadOnlyMemory<byte>.Empty, Opcode.PetListResponse));
        continue;
    }

    if (input.StartsWith("petactive ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[10..], out var activePetId))
    {
        PrintPacket(await client.RequestAsync(Opcode.PetActivateRequest, BuildInt64Payload(activePetId), Opcode.PetActivateResponse));
        continue;
    }

    if (input.StartsWith("petheal ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[8..], out var healPetId))
    {
        PrintPacket(await client.RequestAsync(Opcode.PetHealRequest, BuildInt64Payload(healPetId), Opcode.PetHealResponse));
        continue;
    }

    if (input.StartsWith("petrevive ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[10..], out var revivePetId))
    {
        PrintPacket(await client.RequestAsync(Opcode.PetReviveRequest, BuildInt64Payload(revivePetId), Opcode.PetReviveResponse));
        continue;
    }

    if (input.StartsWith("petrelease ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[11..], out var releasePetId))
    {
        PrintPacket(await client.RequestAsync(Opcode.PetReleaseRequest, BuildInt64Payload(releasePetId), Opcode.PetReleaseResponse));
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
            PrintPacket(await client.RequestAsync(Opcode.PetRenameRequest, payload, Opcode.PetRenameResponse));
        }
        continue;
    }

    if (input.StartsWith("petskills ", StringComparison.OrdinalIgnoreCase) && long.TryParse(input[10..], out var skillPetId))
    {
        PrintPacket(await client.RequestAsync(Opcode.PetSkillListRequest, BuildInt64Payload(skillPetId), Opcode.PetSkillListResponse));
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
            PrintPacket(await client.RequestAsync(Opcode.PetSkillLearnRequest, payload, Opcode.PetSkillLearnResponse));
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
            PrintPacket(await client.RequestAsync(Opcode.PetSkillForgetRequest, payload, Opcode.PetSkillForgetResponse));
        }
        continue;
    }

    Console.WriteLine("Unknown command.");
}

static byte[] BuildInt64Payload(long value)
{
    var payload = new byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(payload, value);
    return payload;
}

static void PrintPacket(PacketFrame packet)
{
    Console.WriteLine($"RX {packet.Opcode} ({packet.Payload.Length} bytes)");
    switch (packet.Opcode)
    {
        case Opcode.Hello:
            Console.WriteLine(Encoding.UTF8.GetString(packet.Payload));
            break;
        case Opcode.LoginResponse:
        case Opcode.CharacterCreateResponse:
        case Opcode.CharacterSelectResponse:
            PrintLegacyResult(packet.Payload);
            break;
        case Opcode.MoveResponse:
            PrintMoveResponse(packet.Payload);
            break;
        case Opcode.MoveBroadcast:
            PrintMoveBroadcast(packet.Payload);
            break;
        case Opcode.PlayerEnterBroadcast:
            PrintPlayerEnter(packet.Payload);
            break;
        case Opcode.PlayerLeaveBroadcast:
            if (packet.Payload.Length >= 8) Console.WriteLine($"Player left: {BinaryPrimitives.ReadInt64LittleEndian(packet.Payload)}");
            break;
        case Opcode.ChatSayBroadcast:
            PrintChat(packet.Payload);
            break;
        case Opcode.PartyInviteResponse:
            PrintPartyInviteResult(packet.Payload);
            break;
        case Opcode.PartyInviteNotification:
            PrintPartyInviteNotification(packet.Payload);
            break;
        case Opcode.PartyAnswerResponse:
            PrintPartyAnswerResult(packet.Payload);
            break;
        case Opcode.PartyStateBroadcast:
            PrintPartyState(packet.Payload);
            break;
        case Opcode.PartyLeaveResponse:
        case Opcode.PetActivateResponse:
        case Opcode.PetRenameResponse:
        case Opcode.PetReleaseResponse:
        case Opcode.PetHealResponse:
        case Opcode.PetReviveResponse:
        case Opcode.PetSkillLearnResponse:
        case Opcode.PetSkillForgetResponse:
        case Opcode.BattlePetSkillSelectResponse:
            PrintSimpleResult(packet.Payload);
            break;
        case Opcode.PetListResponse:
            PrintPets(packet.Payload);
            break;
        case Opcode.PetSkillListResponse:
            PrintPetSkills(packet.Payload);
            break;
        case Opcode.BattleStart:
            PrintBattleStart(packet.Payload);
            break;
        case Opcode.BattleTurnResult:
            PrintBattleTurn(packet.Payload);
            break;
        case Opcode.BattleEnd:
            PrintBattleEnd(packet.Payload);
            break;
    }
}

static void PrintChat(byte[] payload)
{
    if (payload.Length < 12) return;
    var offset = 0;
    var characterId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
    var name = ReadString(payload, ref offset);
    var text = ReadString(payload, ref offset);
    Console.WriteLine($"[SAY] {name}#{characterId}: {text}");
}

static void PrintPartyInviteNotification(byte[] payload)
{
    if (payload.Length < 10) return;
    var offset = 0;
    var inviterId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
    var name = ReadString(payload, ref offset);
    Console.WriteLine($"PARTY INVITE from {name}#{inviterId}. Use paccept {inviterId} or preject {inviterId}.");
}

static void PrintPartyInviteResult(byte[] payload)
{
    if (payload.Length < 11) return;
    var offset = 0;
    var result = payload[offset++];
    var targetId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
    var message = ReadString(payload, ref offset);
    Console.WriteLine($"PartyInvite result={result} target={targetId} {message}");
}

static void PrintPartyAnswerResult(byte[] payload)
{
    if (payload.Length < 4) return;
    var offset = 0;
    var result = payload[offset++];
    var accepted = payload[offset++] == 1;
    var message = ReadString(payload, ref offset);
    Console.WriteLine($"PartyAnswer result={result} accepted={accepted} {message}");
}

static void PrintPartyState(byte[] payload)
{
    if (payload.Length < 25) return;
    var offset = 0;
    var partyId = new Guid(payload.AsSpan(offset, 16)); offset += 16;
    var leaderId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
    var count = payload[offset++];
    if (count == 0)
    {
        Console.WriteLine("PARTY cleared.");
        return;
    }

    Console.WriteLine($"PARTY {partyId} leader={leaderId} members={count}");
    for (var i = 0; i < count; i++)
    {
        if (offset + 10 > payload.Length) return;
        var memberId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
        var name = ReadString(payload, ref offset);
        Console.WriteLine($"  {(memberId == leaderId ? "*" : " ")} {name}#{memberId}");
    }
}

static void PrintMoveResponse(byte[] payload)
{
    if (payload.Length < 10) return;
    var result = payload[0];
    var mapId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1, 4));
    var x = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(5, 2));
    var y = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(7, 2));
    var direction = payload[9];
    Console.WriteLine($"MoveResult={result} authoritative=Map:{mapId} ({x},{y}) Dir={direction}");
}

static void PrintMoveBroadcast(byte[] payload)
{
    if (payload.Length < 17) return;
    var id = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, 8));
    var map = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
    var x = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(12, 2));
    var y = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(14, 2));
    Console.WriteLine($"MOVE Player={id} Map={map} ({x},{y}) Dir={payload[16]}");
}

static void PrintPlayerEnter(byte[] payload)
{
    if (payload.Length < 10) return;
    var offset = 0;
    var id = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
    var name = ReadString(payload, ref offset);
    if (offset + 9 > payload.Length) return;
    var map = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
    var x = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset, 2)); offset += 2;
    var y = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(offset, 2)); offset += 2;
    var direction = payload[offset];
    Console.WriteLine($"ENTER {name}#{id} Map={map} ({x},{y}) Dir={direction}");
}

static void PrintPets(byte[] payload)
{
    if (payload.Length == 0) return;
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
    if (payload.Length < 3) return;
    if (payload[0] != 1) { PrintSimpleResult(payload); return; }
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
        var selectedSkillId = offset + 4 <= payload.Length ? BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)) : 0;
        Console.WriteLine($"PET: {petName}#{petId} Lv.{petLevel} HP={petHp}/{petMaxHp} Loyalty={loyalty} Skill={selectedSkillId}");
    }
}

static void PrintBattleTurn(byte[] payload)
{
    if (payload.Length < 28) return;
    Console.WriteLine($"Action={payload[0]} PlayerDMG={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1,4))} PetDMG={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5,4))} MonsterDMG={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(9,4))} PlayerHP={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(13,4))} PetHP={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(17,4))} MonsterHP={BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(21,4))} Target={payload[25]}");
}

static void PrintBattleEnd(byte[] payload)
{
    if (payload.Length < 31) return;
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
