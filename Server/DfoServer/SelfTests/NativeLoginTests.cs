using System.Net;
using System.Text;
using DfoServer.Game.Auth;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Testing;

namespace DfoServer;

// 独立按原生 writer 的顺序构造请求，网络测试复用样例，不调用产品 Builder。
internal static class NativeLoginWire
{
    public const string Digest = "0123456789abcdef0123456789abcdef";
    public const string WrongDigest = "fedcba9876543210fedcba9876543210";
    public static byte[] Precheck(string account) => TcpPeer.Build(w =>
    {
        Text(w, account);
        w.Write((byte)0);
    });
    public static byte[] Login(string account, string digest = Digest, byte count = 0) => TcpPeer.Build(w =>
    {
        Text(w, account);
        w.Write((byte)0);
        Text(w, digest);
        w.Write((byte)0);
        w.Write(6);
        w.Write("123456"u8);
        w.Write(512);
        w.Write(new byte[512]);
        w.Write(0u);
        w.Write(0u);
        w.Write((ushort)1);
        w.Write(count);
        for (int i = 0; i < count; i++)
            w.Write(i + 1);
        w.Write((byte)0);
    });
    static void Text(BinaryWriter writer, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}

internal static class NativeLoginTests
{
    public static void Run()
    {
        int before = TestCheck.Count;
        Configuration();
        TestCheck.That(NativeLoginRequest.Precheck(NativeLoginWire.Precheck("Alice")) == "Alice", "precheck account exact text");
        var value = NativeLoginRequest.Login(NativeLoginWire.Login("Alice"));
        TestCheck.That(value.Account == "Alice" && value.Digest == NativeLoginWire.Digest, "native login strings separated by state byte");
        TestCheck.That(NativeLoginRequest.Login(NativeLoginWire.Login("abc", count: 255)).Account == "abc", "variable native login tail");
        byte[] complete = NativeLoginWire.Login("Alice");
        for (int i = 0; i < complete.Length; i++)
            Reject(complete[..i]);
        Reject([.. complete, 0]);
        Reject(NativeLoginWire.Login("bad/account"));
        Reject(NativeLoginWire.Login(new string('x', 31)));
        Reject(NativeLoginWire.Login("Alice", "plain-password"));
        Reject(NativeLoginWire.Login("Alice", new string('g', 32)));
        byte[] overflow = (byte[])complete.Clone();
        Array.Fill(overflow, (byte)255, 0, 4);
        Reject(overflow);
        string root = Path.Combine(Path.GetTempPath(), "native-login-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "accounts.db");
        try
        {
            var accounts = new AuthenticationService(new AuthenticationRepository(path), 100000);
            AuthenticationResult Login(string id, string digest = NativeLoginWire.Digest) => accounts.VerifyOrCreateLogin(id, digest, IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(5));
            accounts.EnsureDevelopmentAccount();
            TestCheck.That(Login("10000", "e10adc3949ba59abbe56e057f20f883e").Succeeded, "development account accepts MD5 of 123456");
            TestCheck.That(!Login("10000").Succeeded, "development account requires its seeded credential");
            accounts.ChangePassword("10000", NativeLoginWire.WrongDigest);
            accounts.EnsureDevelopmentAccount();
            TestCheck.That(Login("10000", NativeLoginWire.WrongDigest).Succeeded, "startup does not overwrite changed developer credentials");
            TestCheck.That(Login("Alice").Succeeded, "first valid native credentials create account");
            TestCheck.That(Login("ALICE", NativeLoginWire.Digest.ToUpperInvariant()).Identity?.AccountId == "alice", "account and digest normalization");
            TestCheck.That(!Login("Alice", NativeLoginWire.WrongDigest).Succeeded, "existing account does not change credentials");
            accounts.Disable("alice");
            TestCheck.That(Login("alice").Failure == AuthenticationFailure.AccountDisabled, "disabled account not recreated");
            accounts.Enable("alice");
            TestCheck.That(!Login("bad", "plain-password").Succeeded, "invalid digest does not create account");
            accounts.Create("bad", NativeLoginWire.Digest);
            TestCheck.That(Login("bad").Succeeded, "invalid first login left account free");
            Task<AuthenticationResult>[] racers = [Task.Run(() => Login("race")), Task.Run(() => Login("race", NativeLoginWire.WrongDigest))];
            Task.WaitAll(racers);
            TestCheck.That(racers.Count(t => t.Result.Succeeded) == 1, "racing first logins choose exactly one credential");
            var reopened = new AuthenticationService(new AuthenticationRepository(path));
            TestCheck.That(reopened.VerifyOrCreateLogin("alice", NativeLoginWire.Digest, IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(5)).Succeeded, "native credentials persist after reopen");
            TestCheck.That(TestFiles.ReadAllBytes(path).AsSpan().IndexOf(Encoding.ASCII.GetBytes(NativeLoginWire.Digest)) < 0, "database stores salted derived hash, not login digest");
        }
        finally
        {
            TestFiles.DeleteDirectory(root, true);
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} native login assertions");
    }

    static void Configuration()
    {
        var options = ServerOptions.Load();
        TestCheck.That(options.DevelopmentAccount == "10000", "development account uses native identity");
        TestCheck.That(options.DuplicateLoginPolicy == Game.Accounts.DuplicateLoginPolicy.ReplaceExisting,
            "native login always replaces existing owner after credential validation");
    }

    static void Reject(byte[] body)
    {
        try
        {
            NativeLoginRequest.Login(body);
        }
        catch (InvalidDataException)
        {
            TestCheck.That(true, "invalid native login rejected");
            return;
        }
        throw new InvalidOperationException("Malformed native login accepted");
    }
}
