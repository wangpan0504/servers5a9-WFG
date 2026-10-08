namespace DfoServer.Testing;

internal static class TestCheck
{
    static int count;
    public static int Count => Volatile.Read(ref count);

    public static void That(bool condition, string message)
    {
        Interlocked.Increment(ref count);
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
    }

    public static void Bytes(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, string message) => That(actual.SequenceEqual(expected), message);

    public static void Reject<T>(Action action, string message = "") where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            That(true, message);
            return;
        }
        throw new InvalidOperationException("FAIL: expected " + typeof(T).Name + ": " + message);
    }
}
