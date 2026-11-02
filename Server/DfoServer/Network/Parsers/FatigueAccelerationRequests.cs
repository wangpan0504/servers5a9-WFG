namespace DfoServer.Network.Parsers;

internal static class FatigueAccelerationRequests
{
    public static bool Change(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1 || body[0] > 1) throw new InvalidDataException("疲劳燃烧开关正文必须为一个0/1字节");
        return body[0] != 0;
    }
}
