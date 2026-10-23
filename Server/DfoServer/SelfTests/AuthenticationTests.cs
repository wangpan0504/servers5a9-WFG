using DfoServer.Game.Accounts;
using System.Net;
using DfoServer.Game.Auth;

namespace DfoServer;

internal static class AuthenticationTests
{
    static int checks;
    static void Check(bool condition, string message)
    {
        checks++;
        if (!condition)
            throw new Exception("FAIL auth: " + message);
    }

    static void Reject(Action action, string message)
    {
        try
        {
            action();
            throw new Exception("FAIL auth: expected rejection " + message);
        }
        catch (ArgumentException)
        {
            checks++;
        }
    }

    public static void Run()
    {
        checks = 0;
        AccountOwnerTests.Run();
        ExpertJobTradeRepositoryTests.Run();
        var settings = new AuthenticationSettings(DuplicateLoginPolicy.Reject, 300, "matched-client");
        settings.Validate();
        Reject(() => new AuthenticationSettings(DuplicateLoginPolicy.Reject, 0, "matched-client").Validate(), "zero TTL");
        Reject(() => new AuthenticationSettings(DuplicateLoginPolicy.Reject, 300, "").Validate(), "empty protocol version");
        Reject(() => new AuthenticationSettings(DuplicateLoginPolicy.Reject, 300, "unsupported-client").Validate(), "wrong protocol version");
        var issued = DateTimeOffset.UtcNow;
        var identity = new AuthenticatedIdentity("acct-1", "auth-session-1", issued, issued.AddMinutes(5), IPAddress.Loopback, "matched-client", 7);
        Check(identity.AccountId == "acct-1" && identity.ProtocolVersion == "matched-client", "identity preserves account and protocol");
        Console.WriteLine($"PASS {checks} authentication contract assertions");
    }
}
