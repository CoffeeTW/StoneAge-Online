namespace StoneAge.Network.Protocol;

public enum Opcode : ushort
{
    Hello = 0x0001,
    LoginRequest = 0x0101,
    LoginResponse = 0x0102,
    Logout = 0x0103,
    CharacterListRequest = 0x0201,
    CharacterListResponse = 0x0202,
    CharacterCreateRequest = 0x0203,
    CharacterCreateResponse = 0x0204,
    CharacterSelectRequest = 0x0205,
    CharacterSelectResponse = 0x0206,
    EnterWorld = 0x0301,
    LeaveWorld = 0x0302,
    PlayerEnterBroadcast = 0x0303,
    PlayerLeaveBroadcast = 0x0304,
    MoveRequest = 0x0401,
    MoveBroadcast = 0x0402,
    Ping = 0x0501,
    Pong = 0x0502
}
