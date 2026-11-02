namespace DfoServer.Network.Parsers;

internal static class PcRoomRequests
{
    public static void Empty(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty && body.Length != 13) throw new InvalidDataException("网吧领奖请求正文必须为空");
    }

    public static byte[] Weapons(ReadOnlySpan<byte> body)
    {
        // 原生 MakePacket 的13B预留区与网络帧头分开；候选行本身固定两字节。
        if (body.Length >= 16 && body.Length == 14 + body[13] * 2) body = body[13..];
        if (body.Length < 3 || body[0] == 0 || body.Length != 1 + body[0] * 2)
            throw new InvalidDataException("网吧武器候选数量或长度无效");
        var indexes = new byte[body[0]];
        for (int index = 0; index < indexes.Length; index++)
        {
            if (body[1 + index * 2] != 0) throw new InvalidDataException("未知网吧武器候选分类");
            indexes[index] = body[2 + index * 2];
        }
        if (indexes.Distinct().Count() != indexes.Length) throw new InvalidDataException("重复网吧武器候选");
        return indexes;
    }
}
