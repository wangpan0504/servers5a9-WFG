using DfoServer.Game.Auth;
using DfoServer.Testing;
using System.Net;
using System.Security.Cryptography;

namespace DfoServer;

internal static class AuthenticationServiceTests
{
    public static void Run()
    {
        int checks = TestCheck.Count;
        var store = new MemoryStore();
        var service = new AuthenticationService(store, 100000);
        AuthenticationResult Login(string id, string digest = NativeLoginWire.Digest, TimeSpan? ttl = null) =>
            service.VerifyOrCreateLogin(id, digest, IPAddress.Loopback, "fixture", ttl ?? TimeSpan.FromMinutes(5));
        foreach (string id in new[] { "", "with space", "中文", new string('a', 31), "line\nbreak" })
            TestCheck.That(Login(id).Failure == AuthenticationFailure.InvalidCredentials, "invalid native account rejected before storage");
        foreach (string digest in new[] { "", "plain-password", new string('g', 32), new string('a', 33) })
            TestCheck.That(Login("alice", digest).Failure == AuthenticationFailure.InvalidCredentials, "invalid native digest rejected before storage");
        TestCheck.That(!Login("alice", ttl: TimeSpan.Zero).Succeeded && !Login("alice", ttl: TimeSpan.FromSeconds(-1)).Succeeded, "invalid TTL cannot create an account");
        TestCheck.That(store.Reads == 0 && store.Writes == 0, "invalid native input has no storage side effects");

        DateTimeOffset before = DateTimeOffset.UtcNow;
        var first = Login("ALICE", NativeLoginWire.Digest.ToUpperInvariant());
        var identity = first.Identity!;
        TestCheck.That(first.Succeeded && identity.AccountId == "alice" && identity.AuthGeneration == 1, "new login returns normalized persistent identity");
        TestCheck.That(identity.ClientAddress.Equals(IPAddress.Loopback) && identity.ProtocolVersion == "fixture" &&
            identity.IssuedUtc >= before && identity.IssuedUtc <= DateTimeOffset.UtcNow && identity.ExpiresUtc - identity.IssuedUtc == TimeSpan.FromMinutes(5), "identity retains address, protocol, revision and TTL metadata");
        var credential = store.Entries["alice"].Password;
        TestCheck.That(credential.Salt.Length == 16 && credential.Hash.Length == 32 && credential.Iterations == 100000, "stored credential retains the salt, hash and iteration format");
        byte[] expected = Rfc2898DeriveBytes.Pbkdf2(NativeLoginWire.Digest, credential.Salt.AsSpan(), 100000, HashAlgorithmName.SHA256, 32);
        TestCheck.Bytes(credential.Hash.AsSpan(), expected, "stored derivation agrees with independent PBKDF2 call");
        int writes = store.Writes, reads = store.Reads;
        var again = Login("Alice");
        TestCheck.That(again.Succeeded && store.Writes == writes && store.Reads == reads + 1, "existing login only reads its credential snapshot");
        TestCheck.That(again.Identity!.AuthSessionId.Length == 32 && again.Identity.AuthSessionId != identity.AuthSessionId, "each successful authentication issues a fresh session id");
        TestCheck.That(!Login("alice", NativeLoginWire.WrongDigest).Succeeded && store.Writes == writes, "wrong credential never overwrites existing account");

        service.Create(" Mixed ", "raw-password");
        TestCheck.That(service.Verify("MIXED", "raw-password", IPAddress.IPv6Loopback, "fixture", TimeSpan.FromSeconds(40)).Succeeded, "administrative credential API preserves normalization and non-digest passwords");
        Reject<InvalidOperationException>(() => service.Create("mixed", "replacement"));
        foreach (string id in new[] { " ", new string('a', 65) }) Reject<ArgumentException>(() => service.Create(id, "password"));
        foreach (string password in new[] { "", new string('a', 257) }) Reject<ArgumentException>(() => service.ChangePassword("mixed", password));
        TestCheck.That(!service.Verify("mixed", "", IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(1)).Succeeded, "empty password rejected");
        TestCheck.That(!service.Verify("mixed", "raw-password", IPAddress.Loopback, "fixture", TimeSpan.Zero).Succeeded, "direct verification preserves TTL rejection");
        service.Disable("mixed");
        TestCheck.That(service.Verify("mixed", "wrong", IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(1)).Failure == AuthenticationFailure.AccountDisabled, "disabled state takes precedence over wrong password");
        service.Enable("mixed");
        var updatedService = new AuthenticationService(store);
        updatedService.ChangePassword("mixed", "next-password");
        var changed = updatedService.Verify("mixed", "next-password", IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(1));
        TestCheck.That(changed.Succeeded && changed.Identity!.AuthGeneration == 4 && store.Entries["mixed"].Password.Iterations == 120000, "admin updates advance revision and use current derivation policy");
        TestCheck.That(updatedService.Verify("alice", NativeLoginWire.Digest, IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(1)).Succeeded, "verification uses stored iterations rather than new-account policy");
        foreach (Action action in new Action[] { () => service.Disable("missing"), () => service.Enable("missing"), () => service.ChangePassword("missing", "password") })
            Reject<KeyNotFoundException>(action);

        service.EnsureDevelopmentAccount();
        service.ChangePassword(LoginCredentials.DevelopmentAccount, NativeLoginWire.WrongDigest);
        service.Disable(LoginCredentials.DevelopmentAccount);
        var seeded = store.Entries[LoginCredentials.DevelopmentAccount];
        writes = store.Writes;
        service.EnsureDevelopmentAccount();
        TestCheck.That(store.Writes == writes && store.Entries[LoginCredentials.DevelopmentAccount] == seeded, "initialization preserves modified and disabled developer credentials");
        TestCheck.That(Login(LoginCredentials.DevelopmentAccount, NativeLoginWire.WrongDigest).Failure == AuthenticationFailure.AccountDisabled, "development account has no disabled-account bypass");

        // 模拟另一进程在读取缺失行后抢先落库；不得认证刚计算但未写入的候选凭据。
        foreach (bool disabled in new[] { false, true })
        {
            foreach (string digest in new[] { NativeLoginWire.Digest, NativeLoginWire.WrongDigest })
            {
                var rival = new MemoryStore();
                rival.OnCreate = (id, _) => { rival.Entries[id] = new(credential, disabled, 7); return false; };
                var result = new AuthenticationService(rival, 100000).VerifyOrCreateLogin("race", digest, IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(1));
                bool accepted = !disabled && digest == NativeLoginWire.Digest;
                TestCheck.That(result.Succeeded == accepted && (!accepted || result.Identity!.AuthGeneration == 7), "first-login conflict verifies winner and returns its revision");
                TestCheck.That(rival.Reads == 2 && rival.Writes == 1 && (!disabled || result.Failure == AuthenticationFailure.AccountDisabled), "conflict is bounded and honors winner's disabled state");
            }
        }
        var vanished = new MemoryStore { OnCreate = (_, _) => true };
        TestCheck.That(!new AuthenticationService(vanished, 100000).VerifyOrCreateLogin("gone", NativeLoginWire.Digest, IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(1)).Succeeded, "creation alone is insufficient without persisted credentials");
        var broken = new MemoryStore { OnCreate = (_, _) => throw new IOException("synthetic storage failure") };
        var brokenService = new AuthenticationService(broken, 100000);
        Reject<IOException>(() => brokenService.VerifyOrCreateLogin("broken", NativeLoginWire.Digest, IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(1)));
        Reject<IOException>(brokenService.EnsureDevelopmentAccount);
        TestCheck.That(broken.Entries.Count == 0 && broken.Writes == 2, "storage failure is propagated without retries or success");
        Reject<ArgumentOutOfRangeException>(() => new AuthenticationService(store, 99999));
        Reject<ArgumentNullException>(() => new AuthenticationService(null!));
        Console.WriteLine($"PASS {TestCheck.Count - checks} authentication use-case assertions");
    }

    static void Reject<T>(Action action) where T : Exception
    {
        bool rejected = false;
        try { action(); }
        catch (T) { rejected = true; }
        TestCheck.That(rejected, "expected " + typeof(T).Name);
    }

    sealed class MemoryStore : IAccountCredentialStore
    {
        public Dictionary<string, AccountCredential> Entries { get; } = new(StringComparer.Ordinal);
        public Func<string, PasswordCredential, bool>? OnCreate { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public AccountCredential? Find(string accountId) { Reads++; return Entries.GetValueOrDefault(accountId); }
        public bool TryCreate(string accountId, PasswordCredential password)
        {
            Writes++;
            return OnCreate?.Invoke(accountId, password) ?? Entries.TryAdd(accountId, new(password, false, 1));
        }
        public bool ChangePassword(string accountId, PasswordCredential password)
        {
            Writes++;
            if (!Entries.TryGetValue(accountId, out var row)) return false;
            Entries[accountId] = row with { Password = password, Revision = row.Revision + 1 };
            return true;
        }
        public bool SetDisabled(string accountId, bool disabled)
        {
            Writes++;
            if (!Entries.TryGetValue(accountId, out var row)) return false;
            Entries[accountId] = row with { Disabled = disabled, Revision = row.Revision + 1 };
            return true;
        }
    }
}
