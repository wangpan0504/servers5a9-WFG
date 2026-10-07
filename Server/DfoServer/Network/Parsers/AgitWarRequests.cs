namespace DfoServer.Network.Parsers;

internal static class AgitWarRequests
{
    // 当前只确认入城查询的子型；其他子型保持未实现。
    public static bool IsTownInformation(ReadOnlySpan<byte> body) => body.SequenceEqual<byte>([2, 0, 0, 0]);
}
