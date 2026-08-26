using System.Buffers.Binary;
using System.Text;
using StoneAge.Network.Protocol;

internal static class PartyBattleConsole
{
    public static bool TryPrint(PacketFrame packet)
    {
        switch (packet.Opcode)
        {
            case Opcode.PartyBattleStart:
                PrintStart(packet.Payload);
                return true;
            case Opcode.PartyBattleActionResponse:
                PrintActionResponse(packet.Payload);
                return true;
            case Opcode.PartyBattleTurnResult:
                PrintTurn(packet.Payload);
                return true;
            case Opcode.PartyBattleEnd:
                PrintEnd(packet.Payload);
                return true;
            default:
                return false;
        }
    }

    private static void PrintStart(byte[] payload)
    {
        try
        {
            var offset = 0;
            if (payload.Length < 16 + 4 + 2 + 4 + 4 + 4 + 1) return;
            var battleId = new Guid(payload.AsSpan(offset, 16)); offset += 16;
            var monsterId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
            var monsterName = ReadString(payload, ref offset);
            var monsterLevel = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
            var monsterHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
            var monsterMaxHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
            var count = payload[offset++];
            Console.WriteLine($"PARTY BATTLE {battleId}: {monsterName}#{monsterId} Lv.{monsterLevel} HP={monsterHp}/{monsterMaxHp}");
            for (var i = 0; i < count; i++)
            {
                var characterId = ReadInt64(payload, ref offset);
                var name = ReadString(payload, ref offset);
                var leader = ReadByte(payload, ref offset) == 1;
                var hp = ReadInt32(payload, ref offset);
                var maxHp = ReadInt32(payload, ref offset);
                var hasPet = ReadByte(payload, ref offset) == 1;
                Console.WriteLine($"  {(leader ? "*" : " ")} {name}#{characterId} HP={hp}/{maxHp}");
                if (hasPet)
                {
                    var petId = ReadInt64(payload, ref offset);
                    var petName = ReadString(payload, ref offset);
                    var petHp = ReadInt32(payload, ref offset);
                    var petMaxHp = ReadInt32(payload, ref offset);
                    var skillId = ReadInt32(payload, ref offset);
                    Console.WriteLine($"      PET {petName}#{petId} HP={petHp}/{petMaxHp} Skill={skillId}");
                }
            }
            Console.WriteLine("Use attack or defend. Active pets act automatically when obedient.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PartyBattleStart parse error: {ex.Message}");
        }
    }

    private static void PrintActionResponse(byte[] payload)
    {
        if (payload.Length < 3) return;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1, 2));
        if (3 + length > payload.Length) return;
        Console.WriteLine($"PARTY ACTION {(payload[0] == 1 ? "OK" : "FAIL")}: {Encoding.UTF8.GetString(payload, 3, length)}");
    }

    private static void PrintTurn(byte[] payload)
    {
        try
        {
            if (payload.Length < 11) return;
            var offset = 0;
            var turn = ReadInt32(payload, ref offset);
            var monsterHp = ReadInt32(payload, ref offset);
            var victory = ReadByte(payload, ref offset) == 1;
            var defeat = ReadByte(payload, ref offset) == 1;
            var hitCount = ReadByte(payload, ref offset);
            Console.WriteLine($"PARTY TURN {turn}: MonsterHP={monsterHp} Victory={victory} Defeat={defeat}");
            for (var i = 0; i < hitCount; i++)
            {
                var actorType = ReadByte(payload, ref offset);
                var actorId = ReadInt64(payload, ref offset);
                var targetType = ReadByte(payload, ref offset);
                var targetId = ReadInt64(payload, ref offset);
                var amount = ReadInt32(payload, ref offset);
                var targetHp = ReadInt32(payload, ref offset);
                var isHeal = ReadByte(payload, ref offset) == 1;
                var actor = ActorLabel(actorType, actorId);
                var target = ActorLabel(targetType, targetId);
                Console.WriteLine(isHeal
                    ? $"  {actor} -> {target} HEAL={amount} TargetHP={targetHp}"
                    : $"  {actor} -> {target} DMG={amount} TargetHP={targetHp}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PartyBattleTurn parse error: {ex.Message}");
        }
    }

    private static void PrintEnd(byte[] payload)
    {
        try
        {
            if (payload.Length < 23) return;
            var offset = 0;
            var result = ReadByte(payload, ref offset);
            var expEach = ReadInt32(payload, ref offset);
            var monsterId = ReadInt32(payload, ref offset);
            var rewardItemId = ReadInt32(payload, ref offset);
            var rewardOwnerId = ReadInt64(payload, ref offset);
            var message = ReadString(payload, ref offset);
            Console.WriteLine($"PARTY BATTLE END result={result} monster={monsterId} EXP/participant={expEach} {message}");
            if (rewardItemId != 0)
                Console.WriteLine($"PARTY DROP Item={rewardItemId} OwnerCharacter={rewardOwnerId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PartyBattleEnd parse error: {ex.Message}");
        }
    }

    private static string ActorLabel(byte type, long id)
        => type switch
        {
            0 => "Monster",
            1 => $"Player#{id}",
            2 => $"Pet#{id}",
            _ => $"Actor({type})#{id}"
        };

    private static byte ReadByte(byte[] payload, ref int offset)
    {
        if (offset >= payload.Length) throw new InvalidDataException("Truncated byte.");
        return payload[offset++];
    }

    private static int ReadInt32(byte[] payload, ref int offset)
    {
        if (offset + 4 > payload.Length) throw new InvalidDataException("Truncated Int32.");
        var value = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static long ReadInt64(byte[] payload, ref int offset)
    {
        if (offset + 8 > payload.Length) throw new InvalidDataException("Truncated Int64.");
        var value = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8));
        offset += 8;
        return value;
    }

    private static string ReadString(byte[] payload, ref int offset)
    {
        if (offset + 2 > payload.Length) throw new InvalidDataException("Missing string length.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2)); offset += 2;
        if (offset + length > payload.Length) throw new InvalidDataException("Truncated string.");
        var value = Encoding.UTF8.GetString(payload, offset, length);
        offset += length;
        return value;
    }
}
