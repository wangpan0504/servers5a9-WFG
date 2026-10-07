using DfoServer.Game.Town;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class TownRequests
{
    // Client 02A8F294..02A8F3A9: u16 x/y,u8 movement,u16 speed,u8 action,u16 value,u8 0,u16 0.
    public static PositionRequest Position(ushort opcode, ReadOnlySpan<byte> b)
    {
        if (opcode == (ushort)CmdPacketType.USER_STATE_MOTION ? b.Length != 13 : opcode != (ushort)CmdPacketType.SET_USER_POSITION || b.Length != 7)
            throw new InvalidDataException("Position body width");
        if (b[4] is < 1 or > 9)
            throw new InvalidDataException("Movement code outside native 1..9");
        if (b.Length == 13 && (b[10] != 0 || b[11] != 0 || b[12] != 0))
            throw new InvalidDataException("Unknown movement tail");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(b), BinaryPrimitives.ReadUInt16LittleEndian(b[2..]), b[4], BinaryPrimitives.ReadUInt16LittleEndian(b[5..]), b.Length == 13 ? b[7] : (byte)0, b.Length == 13 ? BinaryPrimitives.ReadUInt16LittleEndian(b[8..]) : (ushort)0);
    }

    public static AreaRequest Area(ReadOnlySpan<byte> b)
    {
        if (b.Length != 16)
            throw new InvalidDataException("Area body width");
        ushort x = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]), y = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
        // Path kind is a full u8, not a packet-validity whitelist. Route admission belongs to the session.
        // Live Client includes ordinary0, room portal5 and return6; preserve every other value unchanged.
        if (b[6] != 5)
            throw new InvalidDataException("Unverified area request fields");
        return new(b[0], b[1], x, y, b[6], BinaryPrimitives.ReadUInt16LittleEndian(b[7..]), BinaryPrimitives.ReadUInt16LittleEndian(b[9..]), b[15], BinaryPrimitives.ReadUInt32LittleEndian(b[11..]));
    }
}
