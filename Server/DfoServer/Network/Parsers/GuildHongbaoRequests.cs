using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class GuildHongbaoRequests
{
    // 旧发送器把栈上的13B前缀一并追加为正文；公共帧头另占13B，不能再剥一次。
    public static bool TryRecord(ReadOnlySpan<byte> body, out uint id)
    {
        id = body.Length == 17 ? BinaryPrimitives.ReadUInt32LittleEndian(body[13..]) : 0;
        return body.Length == 17 && id != 0;
    }

    public static bool TryIssue(ReadOnlySpan<byte> body, out uint item, out byte count)
    {
        item = 0;
        count = 0;
        if (body.Length != 18) return false;
        item = BinaryPrimitives.ReadUInt32LittleEndian(body[13..]);
        count = body[17];
        return item != 0 && count is >= 5 and <= 30;
    }
}
