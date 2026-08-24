namespace StoneAge.Network.Protocol;

public sealed record PacketFrame(Opcode Opcode, byte[] Payload);
