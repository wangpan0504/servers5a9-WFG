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

    // 显式记录未执行子项；保留原控制台与退出行为，不能计作完整覆盖。
    public static void Skip(string code, string reason) => TcpTestReceipts.RecordSkip(code, reason);

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
