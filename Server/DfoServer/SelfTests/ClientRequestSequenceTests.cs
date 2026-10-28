using DfoServer.Network;
using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ClientRequestSequenceTests
{
    public static void RunCore()
    {
        int before = Count;
        var sequence = new ClientRequestSequence();
        ulong previous = sequence.Observe(65534);
        That(sequence.Observe(65534) == previous, "同序号重发保持业务身份");
        That(sequence.Observe(65535) == previous + 1 && sequence.Observe(0) == previous + 2,
            "客户端u16回卷后仍分配递增完整身份");
        That(sequence.Observe(65535) == previous + 1 && sequence.Observe(1) == previous + 3,
            "回卷前迟到重发不能倒退连接高水位");
        var owners = new AccountOwners();
        using var owner = owners.Acquire("request-identity", () => { });
        var first = new GameSession(null!, null!, owner);
        var second = new GameSession(null!, null!, owner);
        string key = first.GuildRequestIdentity(previous);
        That(key.Length == 64 && key.All(value => char.IsAsciiHexDigitLower(value)), "业务回执使用固定长度十六进制身份");
        That(first.GuildRequestIdentity(previous) == key && first.GuildRequestIdentity(previous + 1) != key &&
            second.GuildRequestIdentity(previous) != key, "重发幂等且不同请求和连接隔离");
        Console.WriteLine($"PASS {Count - before} 客户端序号回卷与公会回执身份断言");
    }
}
