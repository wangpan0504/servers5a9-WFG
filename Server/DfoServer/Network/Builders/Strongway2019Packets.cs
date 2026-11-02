using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class Strongway2019Packets
{
    // ACK520成功后01FFEFF0依序读u8/u16/u8；末字段业务语义未完全闭合，保留原值。
    public static byte[] MatchingAccepted(byte rawFlag) => [1, 1, 14, 0, rawFlag];

    // ACK1525：006B6B40在成功分支读取三个u32；side沿NOTI1492安装的0/1阵营。
    public static byte[] Symbol(uint side, uint kind, uint value)
    {
        if (side > 1 || kind > 8) throw new InvalidDataException("首期同步阵营或类型无效");
        byte[] body = new byte[13];
        body[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), side);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), kind);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), value);
        return body;
    }

    // NOTI1494：首u8与本地sideKey相等才播放胜利；255表示两侧均未获胜，末字段保留原值。
    public static byte[] Result(byte winningSide, byte rawCondition) => winningSide is 0 or 1 or 255 ? [winningSide, rawCondition] :
        throw new InvalidDataException("首期结果胜方阵营无效");

    // NOTI1492：firstRaw为Unix入场截止秒；第二个原值尚未见消费，按协议保留。
    public static byte[] Waiting(uint firstRaw, uint secondRaw, byte side, IReadOnlyList<ushort> actors)
    {
        if (side > 1 || actors.Count is < 1 or > 8 || actors.Any(id => id is 0 or ushort.MaxValue) ||
            actors.Distinct().Count() != actors.Count)
            throw new InvalidDataException("首期等待阵营或角色名单无效");
        byte[] body = new byte[10 + actors.Count * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(body, firstRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), secondRaw);
        body[8] = side;
        body[9] = (byte)actors.Count;
        for (int i = 0; i < actors.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10 + 2 * i), actors[i]);
        return body;
    }
}
