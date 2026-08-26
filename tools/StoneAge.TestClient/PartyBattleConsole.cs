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
                var characterId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
                var name = ReadString(payload, ref offset);
                var leader = payload[offset++] == 1;
                var hp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
                var maxHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
                Console.WriteLine($"  {(leader ? "*" : " ")} {name}#{characterId} HP={hp}/{maxHp}");
            }
            Console.WriteLine("Use attack or defend. Server waits for every living party member.");
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
            var turn = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
            var monsterHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
            var victory = payload[offset++] == 1;
            var defeat = payload[offset++] == 1;
            var hitCount = payload[offset++];
            Console.WriteLine($"PARTY TURN {turn}: MonsterHP={monsterHp} Victory={victory} Defeat={defeat}");
            for (var i = 0; i < hitCount; i++)
            {
                if (offset + 24 > payload.Length) return;
                var actorId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
                var targetId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset, 8)); offset += 8;
                var damage = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
                var targetHp = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
                var actor = actorId == 0 ? "Monster" : $"Player#{actorId}";
                var target = targetId == 0 ? "Monster" : $"Player#{targetId}";
                Console.WriteLine($"  {actor} -> {target} DMG={damage} TargetHP={targetHp}");
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
            if (payload.Length < 11) return;
            var offset = 0;
            var result = payload[offset++];
            var expEach = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
            var monsterId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4)); offset += 4;
            var message = ReadString(payload, ref offset);
            Console.WriteLine($"PARTY BATTLE END result={result} monster={monsterId} EXP/participant={expEach} {message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PartyBattleEnd parse error: {ex.Message}");
        }
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
