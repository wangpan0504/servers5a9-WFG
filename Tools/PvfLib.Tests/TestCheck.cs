namespace PvfLib.Tests;

internal static class TestCheck
{
    private static int count;
    public static int Count => count;

    public static void That(bool condition, string message)
    {
        Interlocked.Increment(ref count);
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
    }

    public static void Reject<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            That(true, typeof(T).Name);
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
