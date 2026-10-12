using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Parsers;

internal static class AdventureRequests
{
    public static byte Area(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1) throw new InvalidDataException("远征操作必须为地区u8");
        return body[0];
    }
    public static void Information(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0 || body[0] + 1 != body.Length || body[1..].ToArray().Distinct().Count() != body[0])
            throw new InvalidDataException("远征查询的地区数量与列表不一致");
    }
    public static (byte Area, uint Duration, ImmutableArray<(byte Selector, byte Job, byte Grow)> Members) Start(ReadOnlySpan<byte> body)
    {
        if (body.Length < 7) throw new InvalidDataException("远征出发包缺少固定字段");
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(body[5..]);
        if (count is < 1 or > 4 || body.Length != 7 + count * 3) throw new InvalidDataException("远征成员数量或行长度非法");
        var rows = ImmutableArray.CreateBuilder<(byte, byte, byte)>();
        for (int i = 0; i < count; i++) rows.Add((body[7 + i * 3], body[8 + i * 3], body[9 + i * 3]));
        return (body[0], BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), rows.ToImmutable());
    }

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
