using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class AdventureRequests
{
    public static (byte Job, byte Grow) Profession(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2 || (body[1] & 0x70) != 0x20 || body[0] > 15 || (body[1] & 15) == 0)
            throw new InvalidDataException("代表角色请求必须为职业及二觉打包成长类型");
        return (body[0], (byte)(body[1] & 15));
    }
    public static (byte Region, ushort Character, byte Job, byte Grow) Representative(ReadOnlySpan<byte> body)
    {
        if (body.Length != 7) throw new InvalidDataException("代表角色设置必须为区域、u32角色、职业及成长类型");
        var profession = Profession(body[5..]);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
        return (body[0], id is > 0 and <= ushort.MaxValue ? (ushort)id : (ushort)0, profession.Job, profession.Grow);
    }

    public static ushort Target(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2) throw new InvalidDataException("冒险团查询必须为角色u16");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }
}
