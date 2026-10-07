using DfoServer.Persistence;
using DfoServer.Game.Auth;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class DatabaseSchemaTests
{
    public static void Run()
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-database-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var characters = new CharacterRepository(path);
            var accounts = new AuthenticationService(new AuthenticationRepository(path));
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is 2L, "首次发布完整结构直接标记当前 v2");
                Check(Query(db, "SELECT value FROM schema_metadata WHERE key='baseline_id'") is SqliteMigrations.BaselineId,
                    "统一数据库保存独立基线标识");
                Check(Query(db, "SELECT count(*) FROM accounts") is 0L && Query(db, "SELECT count(*) FROM characters") is 0L,
                    "初始化不携带账号或角色种子数据");
                Check(Query(db, "PRAGMA journal_mode") is "wal", "账号与游戏数据共用 WAL");
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('shared',0,'Baseline',7,38,1,450,234,0)");
                Reject<SqliteException>(() => Execute(db, "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'shared',0,9,123)"),
                    "完整基线保留库存归属外键");
            }
            accounts.Create("shared", "fixture-password");
            accounts.Disable("shared");
            string snapshot = Snapshot(path);
            _ = new AuthenticationRepository(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == snapshot, "反复初始化不重建、不改账号、角色或版本");
            Check(Directory.GetFiles(root, "*.db").Length == 1, "两个仓储共用一份数据库文件");
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT count(*) FROM accounts WHERE account_id='shared' AND disabled=1") is 1L &&
                    Query(db, "SELECT revision FROM characters WHERE account='shared'") is 7L, "账号和角色状态位于同一数据库并持久化");
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "统一基线数据库与外键完整");
            }

            foreach (string mutation in new[]
            {
                "DROP TABLE schema_metadata", "UPDATE schema_metadata SET value='another-database'",
                "PRAGMA user_version=0", "PRAGMA user_version=3", "ALTER TABLE accounts DROP COLUMN disabled",
                "ALTER TABLE inventory_creature_details DROP COLUMN detail"
            })
            {
                string rejected = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
                _ = new CharacterRepository(rejected);
                using (var db = Open(rejected))
                    Execute(db, mutation);
                string original = Snapshot(rejected);
                RejectDatabase(() => new CharacterRepository(rejected));
                RejectDatabase(() => new AuthenticationRepository(rejected));
                Check(Snapshot(rejected) == original, "拒绝错误基线、较新版本或缺列时保留原库");
            }

            string legacy = Path.Combine(root, "unpublished.db");
            using (var db = Open(legacy))
            {
                Execute(db, "CREATE TABLE old_data(value TEXT)");
                Execute(db, "INSERT INTO old_data VALUES('keep')");
                Execute(db, "PRAGMA user_version=14");
            }
            string legacySnapshot = Snapshot(legacy);
            Reject<InvalidDataException>(() => new CharacterRepository(legacy), "未发布旧库明确拒绝");
            Check(Snapshot(legacy) == legacySnapshot, "旧库不被重置版本、覆盖或迁移");

            string broken = Path.Combine(root, "broken.sql");
            File.WriteAllText(broken, "CREATE TABLE partial(value INTEGER); CREATE TABLE partial(value INTEGER);");
            string failed = Path.Combine(root, "failed.db");
            Reject<SqliteException>(() => SqliteDatabaseBootstrap.Initialize(failed, broken), "中途 DDL 失败立即抛出");
            Check(!File.Exists(failed) && !File.Exists(failed + "-wal") && !File.Exists(failed + "-shm"), "首次建库失败不遗留半成品");
            Reject<FileNotFoundException>(() => SqliteDatabaseBootstrap.Initialize(failed, Path.Combine(root, "missing.sql")), "缺少基线 SQL 明确失败");
            Check(!File.Exists(failed), "缺少 SQL 不留下空数据库");
            _ = new CharacterRepository(failed);

            VerifyFutureMigrations(path);
            string concurrent = Path.Combine(root, "concurrent.db");
            Task.WaitAll(Task.Run(() => new CharacterRepository(concurrent)), Task.Run(() => new AuthenticationRepository(concurrent)));
            using (var db = Open(concurrent))
                Check(Query(db, "SELECT count(*) FROM schema_metadata") is 1L, "并发初始化只创建一份完整基线");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} 统一数据库基线、拒绝、重开及增量迁移原子性断言");
    }

    private static void VerifyFutureMigrations(string path)
    {
        using (var reset = Open(path))
            Execute(reset, "PRAGMA user_version=1");
        string before = Snapshot(path);
        SqliteMigration second = new(2, "test_add_column", (db, tx) =>
        {
            SqliteMigrations.Execute(db, tx, "ALTER TABLE accounts ADD COLUMN future_value INTEGER NOT NULL DEFAULT 0");
            SqliteMigrations.Execute(db, tx, "UPDATE characters SET revision=revision+1");
        });
        SqliteMigration failing = new(3, "test_failure", (db, tx) =>
        {
            SqliteMigrations.Execute(db, tx, "CREATE TABLE partial_migration(value INTEGER)");
            SqliteMigrations.Execute(db, tx, "CREATE TABLE accounts(collision INTEGER)");
        });
        using (var db = Open(path))
        {
            Reject<SqliteException>(() => SqliteMigrations.Apply(db, [second, failing]), "后续步骤失败回滚整个升级事务");
            Check(Snapshot(path) == before, "失败时账号、角色、DDL 和版本全部保持");
            Reject<InvalidOperationException>(() => SqliteMigrations.Apply(db, [failing]), "拒绝不连续的迁移编号");
            SqliteMigrations.Apply(db, [second]);
            Check(Query(db, "PRAGMA user_version") is 2L && Query(db, "SELECT revision FROM characters") is 8L,
                "追加迁移按版本执行并同步提交数据和版本");
            string after = Snapshot(path);
            SqliteMigrations.Apply(db, [second]);
            Check(Snapshot(path) == after, "已执行迁移不重复运行");
        }
        using (var future = Open(path))
            Execute(future, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
        Reject<InvalidDataException>(() => new CharacterRepository(path), "当前程序不回退打开后续版本");
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        return db;
    }

    private static object? Query(SqliteConnection db, string sql)
    {
        using var query = db.CreateCommand();
        query.CommandText = sql;
        return query.ExecuteScalar();
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Snapshot(string path)
    {
        using var db = Open(path);
        return string.Join('|', Query(db, "PRAGMA user_version"),
            Query(db, "SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name)"),
            Query(db, "SELECT group_concat(name||':'||rootpage,'|') FROM sqlite_schema"),
            Has("schema_metadata") ? Query(db, "SELECT group_concat(key||':'||value,'|') FROM schema_metadata") : null,
            Has("old_data") ? Query(db, "SELECT group_concat(value,'|') FROM old_data") : null,
            Has("characters") ? Query(db, "SELECT group_concat(account||':'||revision,'|') FROM characters") : null,
            Has("accounts") ? Query(db, "SELECT group_concat(account_id||':'||revision||':'||hex(password_hash),'|') FROM accounts") : null);
        bool Has(string table) => Query(db, $"SELECT count(*) FROM sqlite_schema WHERE name='{table}'") is 1L;
    }

    private static void RejectDatabase(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or SqliteException)
        {
            Check(true, "仓储拒绝错误数据库");
            return;
        }
        throw new InvalidOperationException("FAIL: 仓储接受了错误数据库");
    }

    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException("FAIL: " + message);
    }

    private static void Check(bool condition, string message) => TestCheck.That(condition, message);
}
