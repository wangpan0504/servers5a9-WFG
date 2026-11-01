using DfoServer.Game.Auth;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using System.Net;

namespace DfoServer;

internal static class AccountCredentialStoreTests
{
    public static async Task RunAsync()
    {
        int checks = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "credential-store-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var store = new AuthenticationRepository(path);
            var service = new AuthenticationService(store, 100000);
            TestCheck.That(store.Find("missing") is null, "missing credential is a read-only absence");
            service.Create("stable", NativeLoginWire.Digest);
            var original = store.Find("stable")!;
            TestCheck.That(!store.TryCreate("stable", original.Password), "only duplicate account produces a create conflict");
            TestCheck.That(Same(original, store.Find("stable")!), "create conflict preserves complete credential and revision");
            TestCheck.That(!store.ChangePassword("missing", original.Password) && !store.SetDisabled("missing", true), "updates report missing row without inserting it");
            service.Disable("stable");
            service.Disable("stable");
            var disabled = store.Find("stable")!;
            TestCheck.That(disabled.Disabled && disabled.Revision == original.Revision + 2 && original.Revision == 1 && !original.Disabled, "updates advance revision while prior snapshots remain immutable");
            service.Enable("stable");
            var enabled = store.Find("stable")!;
            TestCheck.That(!enabled.Disabled && enabled.Revision == 4, "enable preserves revision semantics");

            using (var db = Open(path))
            {
                Execute(db, "CREATE TRIGGER reject_insert BEFORE INSERT ON accounts WHEN NEW.account_id IN ('blocked','10000') BEGIN SELECT RAISE(ABORT,'synthetic insert failure'); END");
                RejectSqlite(() => Login(service, "blocked", NativeLoginWire.Digest));
                RejectSqlite(service.EnsureDevelopmentAccount);
                TestCheck.That(store.Find("blocked") is null && store.Find("10000") is null, "non-duplicate constraint errors propagate without rows or authentication success");
                Execute(db, "DROP TRIGGER reject_insert");
                Execute(db, "CREATE TRIGGER reject_update AFTER UPDATE ON accounts WHEN NEW.account_id='stable' BEGIN SELECT RAISE(ABORT,'synthetic update failure'); END");
                RejectSqlite(() => service.ChangePassword("stable", NativeLoginWire.WrongDigest));
                RejectSqlite(() => service.Disable("stable"));
                TestCheck.That(Same(enabled, store.Find("stable")!), "late SQL failure rolls back credential, disabled state and revision");
                Execute(db, "DROP TRIGGER reject_update");
            }

            // 独立连接占有真实文件库 writer 时，既有账号登录只读 WAL 快照，不申请 writer。
            using (var writer = Open(path))
            using (var tx = writer.BeginTransaction())
            {
                using var cmd = writer.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE accounts SET disabled=1,revision=revision+1 WHERE account_id='stable'";
                cmd.ExecuteNonQuery();
                var read = await Task.Run(() => Login(service, "stable", NativeLoginWire.Digest)).WaitAsync(TimeSpan.FromSeconds(3));
                TestCheck.That(read.Succeeded && read.Identity!.AuthGeneration == enabled.Revision, "existing login completes against committed snapshot during external write");
                tx.Rollback();
            }
            TestCheck.That(Same(enabled, store.Find("stable")!), "read-only login leaves the rolled-back writer state untouched");

            await RaceAsync(path, "race-different", NativeLoginWire.WrongDigest, expectedSuccesses: 1);
            await RaceAsync(path, "race-same", NativeLoginWire.Digest, expectedSuccesses: 2);
            service.ChangePassword("stable", NativeLoginWire.WrongDigest);
            var changed = store.Find("stable")!;
            TestCheck.That(changed.Revision == enabled.Revision + 1 && !changed.Password.Salt.AsSpan().SequenceEqual(original.Password.Salt.AsSpan()), "password update uses fresh salt and one revision increment");
            var reopened = new AuthenticationService(new AuthenticationRepository(path));
            TestCheck.That(!Login(reopened, "stable", NativeLoginWire.Digest).Succeeded && Login(reopened, "stable", NativeLoginWire.WrongDigest).Succeeded, "reopen rejects old credential and accepts updated credential");
            using (var db = Open(path))
            {
                TestCheck.That(Scalar(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Scalar(db, "PRAGMA integrity_check") is "ok", "凭据边界保留当前结构版本与数据库完整性");
                TestCheck.That(Scalar(db, "SELECT count(*) FROM accounts") is 3L, "only successfully created accounts persist");
            }
        }
        finally { if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - checks} credential storage and concurrency assertions");
    }

    static async Task RaceAsync(string path, string account, string secondDigest, int expectedSuccesses)
    {
        using var barrier = new Barrier(2);
        // 两份仓储和用例，确保结果由 SQLite 唯一键而非共享进程内锁决定。
        var first = new AuthenticationService(new FirstReadBarrier(new AuthenticationRepository(path), barrier), 100000);
        var second = new AuthenticationService(new FirstReadBarrier(new AuthenticationRepository(path), barrier), 100000);
        Task<AuthenticationResult>[] tasks = [Task.Run(() => Login(first, account, NativeLoginWire.Digest)), Task.Run(() => Login(second, account, secondDigest))];
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        TestCheck.That(results.Count(r => r.Succeeded) == expectedSuccesses, "concurrent first login authenticates exactly the persisted credential");
        var stored = new AuthenticationRepository(path).Find(account)!;
        TestCheck.That(stored.Revision == 1 && results.Where(r => r.Succeeded).All(r => r.Identity!.AuthGeneration == 1), "first-login race inserts one credential without revision updates");
        if (expectedSuccesses == 1)
        {
            var reopened = new AuthenticationService(new AuthenticationRepository(path), 100000);
            TestCheck.That(Login(reopened, account, NativeLoginWire.Digest).Succeeded == results[0].Succeeded &&
                Login(reopened, account, secondDigest).Succeeded == results[1].Succeeded, "winning and losing credential results survive reopen");
        }
        else
            TestCheck.That(results[0].Identity!.AuthSessionId != results[1].Identity!.AuthSessionId, "same-credential concurrent logins receive distinct authentication identities");
    }

    static AuthenticationResult Login(AuthenticationService service, string id, string digest) =>
        service.VerifyOrCreateLogin(id, digest, IPAddress.Loopback, "fixture", TimeSpan.FromMinutes(1));

    static bool Same(AccountCredential first, AccountCredential second) => first.Disabled == second.Disabled && first.Revision == second.Revision &&
        first.Password.Iterations == second.Password.Iterations && first.Password.Salt.AsSpan().SequenceEqual(second.Password.Salt.AsSpan()) &&
        first.Password.Hash.AsSpan().SequenceEqual(second.Password.Hash.AsSpan());

    static void RejectSqlite(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { rejected = true; }
        TestCheck.That(rejected, "non-duplicate SQLite failure remains observable");
    }

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }
    static void Execute(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    static object? Scalar(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }

    sealed class FirstReadBarrier(IAccountCredentialStore store, Barrier barrier) : IAccountCredentialStore
    {
        int reads;
        public AccountCredential? Find(string accountId)
        {
            var found = store.Find(accountId);
            if (Interlocked.Increment(ref reads) == 1 && !barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("first credential read did not rendezvous");
            return found;
        }
        public bool TryCreate(string accountId, PasswordCredential password) => store.TryCreate(accountId, password);
        public bool ChangePassword(string accountId, PasswordCredential password) => store.ChangePassword(accountId, password);
        public bool SetDisabled(string accountId, bool disabled) => store.SetDisabled(accountId, disabled);
    }
}
