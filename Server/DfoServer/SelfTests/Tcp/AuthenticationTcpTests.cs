using DfoServer.Game.Auth;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AuthenticationTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "authentication"));
        await fixture.StartAsync();
        foreach (ushort opcode in new ushort[] { 171, 1, 4 })
        {
            using var rejected = new TcpPeer(fixture.Options.GamePort);
            rejected.Expect(0, 1);
            rejected.Send(opcode, new byte[39]);
            rejected.Closed();
        }
        using (var rejected = new TcpPeer(fixture.Options.GamePort))
        {
            rejected.Expect(0, 1);
            rejected.Send(1562, NativeLoginWire.Precheck("10000"));
            rejected.Expect(1, 1562, [1]);
            rejected.Send(1, NativeLoginWire.Login("10000", NativeLoginWire.WrongDigest));
            rejected.Closed();
        }
        using (var developer = TcpPeer.Open(fixture))
        {
            developer.Create("开发账号");
            developer.Select();
            developer.Barrier();
            That(fixture.Snapshot("SELECT DISTINCT account FROM characters") == "[[\"10000\"]]", "development characters belong to authenticated account 10000");
            developer.Send(3, [1]);
            developer.Expect(1, 3, [1]);
            developer.Closed();
        }
        using (var resumed = TcpPeer.Open(fixture))
        {
            resumed.Select();
            resumed.Barrier();
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restarted");
        using (var restarted = TcpPeer.Open(fixture, "10000", LoginCredentials.DevelopmentDigest))
        {
            restarted.Select();
            restarted.Barrier();
        }
        Console.WriteLine("PASS native-only authentication, development credentials, reconnect and restart");
    }
}
