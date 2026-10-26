namespace DfoServer.Network.Parsers;

internal static class TagEvildomRunRequests
{
    // 0176C640/0176C5A0分别发送1468/1469；正文均为一个队内ordinal，不是稳定ID或名册index。
    public static byte Ordinal(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1 || body[0] > 1)
            throw new InvalidDataException("再战魔界切角/死亡正文必须是0或1的单字节ordinal");
        return body[0];
    }
}
