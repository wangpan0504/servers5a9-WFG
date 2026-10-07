namespace DfoServer.Network.Parsers;

internal static class SessionRequests
{
    public static byte Exit(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1 || body[0] > 1)
            throw new InvalidDataException("Unsupported Client exit mode");
        return body[0];
    }

    public static void Empty(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty)
            throw new InvalidDataException("Request must have an empty body");
    }
}
