# Protocol v0.1

Binary packet, little-endian.

| Offset | Size | Field |
|---:|---:|---|
| 0 | 2 | Length (包含 4-byte header) |
| 2 | 2 | Opcode |
| 4 | N | Payload |

## v0.1-01

- `0x0001 Hello`

## Reserved for v0.1

- `0x0101 LoginRequest`
- `0x0102 LoginResponse`
- `0x0103 Logout`
- `0x0201 CharacterListRequest`
- `0x0202 CharacterListResponse`
- `0x0203 CharacterCreateRequest`
- `0x0204 CharacterCreateResponse`
- `0x0205 CharacterSelectRequest`
- `0x0206 CharacterSelectResponse`
- `0x0301 EnterWorld`
- `0x0302 LeaveWorld`
- `0x0401 MoveRequest`
- `0x0402 MoveBroadcast`
- `0x0501 Ping`
- `0x0502 Pong`
