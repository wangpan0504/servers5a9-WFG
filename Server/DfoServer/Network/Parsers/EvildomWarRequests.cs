namespace DfoServer.Network.Parsers;

internal static class EvildomWarRequests
{
    // 0080B560..0080B576只有begin/send，无正文写入；模式沿CMD12的KindArgument。
    public static bool IsStart(ReadOnlySpan<byte> body) => body.IsEmpty;
}
