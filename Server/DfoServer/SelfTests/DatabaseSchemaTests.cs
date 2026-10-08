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
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "新建完整结构直接标记当前版本");
                Check(Query(db, "SELECT count(*) FROM character_dungeon_growth_receipts") is 0L,
                    "当前完整结构包含空成长账本，不补发历史奖励");
                Check(Query(db, "SELECT count(*) FROM character_dungeon_settlement_receipts") is 0L,
                    "当前完整结构包含空通关账本，不补发历史经验");
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
                "PRAGMA user_version=0", $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}", "ALTER TABLE accounts DROP COLUMN disabled",
                "ALTER TABLE inventory_creature_details DROP COLUMN detail", "DROP TABLE account_client_settings", "DROP TABLE character_client_settings",
                "DROP TABLE character_game_settings",
                "DROP TABLE character_dungeon_growth_receipts",
                "ALTER TABLE character_dungeon_growth_receipts DROP COLUMN resource_source",
                "DROP TABLE character_dungeon_state", "DROP TABLE character_dungeon_loot_receipts",
                "DROP TABLE character_dungeon_pickup_receipts", "DROP TABLE character_dungeon_settlement_receipts",
                "ALTER TABLE character_dungeon_settlement_receipts DROP COLUMN report_body"
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

            VerifyReleasedMigrations(root);
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

    private static void VerifyReleasedMigrations(string root)
    {
        foreach (int oldVersion in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })
        {
            string path = Path.Combine(root, $"released-v{oldVersion}.db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,experience,gold) VALUES('migrated',0,'Migrated',7,38,1,450,234,0,2,60,123)");
                Execute(db, "INSERT INTO character_skill_points VALUES(1,0,12,0),(1,1,34,0)");
                Execute(db, "INSERT INTO character_active_main_quests VALUES(1,0,3145,1)");
                Execute(db, "INSERT INTO account_wallets(account,revision,rental_points) VALUES('migrated',5,17)");
                if (oldVersion >= 3)
                    Execute(db, "INSERT INTO character_dungeon_growth_receipts VALUES(1,'migrated','legacy','legacy-instance',3145,1,1,76121,123,99,1,20,'legacy-rule','legacy-hash',1,1,2,2,40,60,7)");
                // 旧版必须确实缺少新结构；只改版本号会让真实 CREATE/ALTER 迁移撞表或撞列。
                Execute(db, "DROP TABLE character_dungeon_settlement_receipts");
                if (oldVersion < 9)
                    Execute(db, "DROP TABLE character_dungeon_pickup_receipts; DROP TABLE character_dungeon_loot_receipts");
                if (oldVersion < 8) Execute(db, "DROP TABLE character_dungeon_state");
                if (oldVersion < 7) Execute(db, "DROP TABLE character_game_settings");
                if (oldVersion < 6) Execute(db, "DROP TABLE account_client_settings; DROP TABLE character_client_settings");
                if (oldVersion < 4) Execute(db, "DROP TABLE mailbox_audit_log; DROP TABLE mailbox_attachments; DROP TABLE mailbox_sent_gold; DROP TABLE mailbox_messages");
                if (oldVersion is 3 or 4) Execute(db, "ALTER TABLE character_dungeon_growth_receipts RENAME COLUMN resource_source TO resource_sha256");
                if (oldVersion < 3)
                    Execute(db, "DROP TABLE character_dungeon_growth_receipts");
                if (oldVersion == 1)
                    Execute(db, "ALTER TABLE account_wallets DROP COLUMN rental_points");
                Execute(db, $"PRAGMA user_version={oldVersion}");
                Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_dungeon_state'") is long ownerTables && ownerTables == (oldVersion >= 8 ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_dungeon_growth_receipts'") is long growthTables &&
                    growthTables == (oldVersion >= 3 ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='rental_points'") is long columns &&
                    columns == (oldVersion == 1 ? 0 : 1), "真实旧版模拟按v1/v2/v3保留已发布结构，移除后续副本owner表");
            }
            _ = new AuthenticationRepository(path);
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_dungeon_growth_receipts") is long receipts &&
                    receipts == (oldVersion >= 3 ? 1 : 0), "真实旧版连续迁移保留v3已有账本，v1/v2只建立空账本");
                Check(Query(db, "SELECT count(*) FROM character_dungeon_state") is 0L,
                    "旧角色迁移不补造key、attempt或附属成长owner");
                Check(Query(db, "SELECT count(*) FROM character_dungeon_settlement_receipts") is 0L,
                    "v1至v9升级只增加空通关账本，不合成历史奖励");
                Check(Query(db, "SELECT rental_points FROM account_wallets WHERE account='migrated'") is long rental &&
                    rental == (oldVersion == 1 ? 0 : 17), "v1租赁默认值与v2既有租赁余额分别保留");
                Check(Query(db, "SELECT revision||':'||level||':'||experience||':'||gold FROM characters WHERE id=1") is "7:2:60:123" &&
                    Query(db, "SELECT group_concat(context||':'||sp,',') FROM (SELECT context,sp FROM character_skill_points ORDER BY context)") is "0:12,1:34" &&
                    Query(db, "SELECT quest_id||':'||condition_state FROM character_active_main_quests WHERE character_id=1") is "3145:1" &&
                    Query(db, "SELECT revision FROM account_wallets WHERE account='migrated'") is 5L,
                    "迁移不补经验、两树SP或任务，不修改角色与钱包revision");
                string receipt = "INSERT INTO character_dungeon_growth_receipts VALUES(1,'migrated','event','instance',3145,1,1,76121,256,1,1,72,'rule','hash',1,3,2,2,60,132,8)";
                Execute(db, receipt);
                Reject<SqliteException>(() => Execute(db, receipt), "迁移账本保留事件唯一约束");
                Reject<SqliteException>(() => Execute(db, receipt.Replace("'event'", "'same-key'").Replace("256,1,1,72", "256,2,1,72")),
                    "迁移账本拒绝同实例怪物key重复发奖");
                Reject<SqliteException>(() => Execute(db, receipt.Replace("'event'", "'same-sequence'").Replace("256,1,1,72", "257,1,1,72")),
                    "迁移账本拒绝同实例战斗序号重复发奖");
                Reject<SqliteException>(() => Execute(db, receipt.Replace("'event'", "'other'").Replace("256,1,1,72", "257,2,1,0")),
                    "迁移账本拒绝零奖励等非法事实");
                Reject<SqliteException>(() => Execute(db, receipt.Replace("'migrated'", "'wrong-owner'").Replace("'event'", "'wrong'").Replace("256,1,1,72", "258,3,1,72")),
                    "迁移账本保留角色账号归属外键");
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "真实连续迁移后数据库与外键完整");
            }
            string migrated = Snapshot(path);
            _ = new CharacterRepository(path);
            _ = new AuthenticationRepository(path);
            Check(Snapshot(path) == migrated, "真实迁移重开不重建账本或重复迁移");
        }

        string rollback = Path.Combine(root, "released-rollback.db");
        _ = new CharacterRepository(rollback);
        using (var db = Open(rollback))
        {
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,experience,gold) VALUES('keep',0,'Rollback',9,38,1,450,234,0,2,60,321)");
            Execute(db, "INSERT INTO account_wallets(account,revision,rental_points) VALUES('keep',6,0)");
            Execute(db, "INSERT INTO character_skill_points VALUES(1,0,12,0),(1,1,34,0)");
            Execute(db, "DROP TABLE character_dungeon_settlement_receipts");
            Execute(db, "DROP TABLE character_dungeon_pickup_receipts");
            Execute(db, "DROP TABLE character_dungeon_loot_receipts");
            Execute(db, "DROP TABLE character_dungeon_state");
            Execute(db, "DROP TABLE character_game_settings; DROP TABLE account_client_settings; DROP TABLE character_client_settings");
            Execute(db, "DROP TABLE mailbox_audit_log; DROP TABLE mailbox_attachments; DROP TABLE mailbox_sent_gold; DROP TABLE mailbox_messages");
            Execute(db, "DROP TABLE character_dungeon_growth_receipts");
            Execute(db, "ALTER TABLE account_wallets DROP COLUMN rental_points");
            Execute(db, "PRAGMA user_version=1");
            Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
        }
        string before = Snapshot(rollback);
        Reject<SqliteException>(() => new CharacterRepository(rollback), "真实v2与v3执行后必需列检查失败回滚整链");
        Check(Snapshot(rollback) == before, "真实迁移失败不遗留租赁列、成长账本或版本升级");
        string clearRollback = Path.Combine(root, "clear-upgrade-rollback.db");
        _ = new CharacterRepository(clearRollback);
        using (var db = Open(clearRollback))
        {
            Execute(db, "DROP TABLE character_dungeon_settlement_receipts; PRAGMA user_version=9; CREATE TABLE character_dungeon_settlement_receipts(collision INTEGER)");
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,experience,gold) VALUES('keep',0,'ClearKeep',11,38,1,450,234,0,321,456)");
        }
        string clearBefore = Snapshot(clearRollback);
        Reject<SqliteException>(() => new CharacterRepository(clearRollback), "v9通关账本迁移撞表时拒绝启动");
        Check(Snapshot(clearRollback) == clearBefore, "v10迁移失败保留v9版本、结构与原角色资产");
        using (var db = Open(rollback))
        {
            Check(Query(db, "PRAGMA user_version") is 1L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_dungeon_growth_receipts'") is 0L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_dungeon_state'") is 0L &&
                Query(db, "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='rental_points'") is 0L,
                "回滚恢复真实v1结构");
        }
    }

    private static void VerifyFutureMigrations(string path)
    {
        string before = Snapshot(path);
        // 未来步骤在当前完整结构上追加；已发布前缀只作为连续编号，不伪造旧版结构。
        var released = Enumerable.Range(2, SqliteMigrations.CurrentVersion - 1)
            .Select(version => new SqliteMigration(version, "released_prefix", static (_, _) =>
                throw new InvalidOperationException("当前版本不应重跑已发布迁移"))).ToArray();
        SqliteMigration second = new(SqliteMigrations.CurrentVersion + 1, "test_add_column", (db, tx) =>
        {
            SqliteMigrations.Execute(db, tx, "ALTER TABLE accounts ADD COLUMN future_value INTEGER NOT NULL DEFAULT 0");
            SqliteMigrations.Execute(db, tx, "UPDATE characters SET revision=revision+1");
        });
        SqliteMigration failing = new(SqliteMigrations.CurrentVersion + 2, "test_failure", (db, tx) =>
        {
            SqliteMigrations.Execute(db, tx, "CREATE TABLE partial_migration(value INTEGER)");
            SqliteMigrations.Execute(db, tx, "CREATE TABLE accounts(collision INTEGER)");
        });
        using (var db = Open(path))
        {
            Reject<SqliteException>(() => SqliteMigrations.Apply(db, [.. released, second, failing]), "后续步骤失败回滚整个升级事务");
            Check(Snapshot(path) == before, "失败时账号、角色、DDL 和版本全部保持");
            Reject<InvalidOperationException>(() => SqliteMigrations.Apply(db, [failing]), "拒绝不连续的迁移编号");
            SqliteMigrations.Apply(db, [.. released, second]);
            Check(Query(db, "PRAGMA user_version") is long version && version == second.Version && Query(db, "SELECT revision FROM characters") is 8L,
                "追加迁移按版本执行并同步提交数据和版本");
            string after = Snapshot(path);
            SqliteMigrations.Apply(db, [.. released, second]);
            Check(Snapshot(path) == after, "已执行迁移不重复运行");
        }
        string futureSnapshot = Snapshot(path);
        Reject<InvalidDataException>(() => new CharacterRepository(path), "当前程序不回退打开后续版本");
        Check(Snapshot(path) == futureSnapshot, "拒绝未来版本时保留其完整结构与数据");
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
            Has("characters") ? Query(db, "SELECT group_concat(account||':'||revision||':'||level||':'||experience||':'||gold,'|') FROM characters") : null,
            Has("accounts") ? Query(db, "SELECT group_concat(account_id||':'||revision||':'||hex(salt)||':'||hex(password_hash)||':'||iterations,'|') FROM accounts") : null,
            HasColumn("accounts", "disabled") ? Query(db, "SELECT group_concat(account_id||':'||disabled,'|') FROM accounts") : null,
            Has("account_wallets") ? Query(db, "SELECT group_concat(account||':'||revision,'|') FROM account_wallets") : null,
            HasColumn("account_wallets", "rental_points") ? Query(db, "SELECT group_concat(account||':'||rental_points,'|') FROM account_wallets") : null,
            Has("character_skill_points") ? Query(db, "SELECT group_concat(character_id||':'||context||':'||sp||':'||tp,'|') FROM character_skill_points") : null,
            Has("character_active_main_quests") ? Query(db, "SELECT group_concat(character_id||':'||slot||':'||quest_id||':'||condition_state,'|') FROM character_active_main_quests") : null,
            Has("character_dungeon_growth_receipts") ? Query(db, "SELECT group_concat(character_id||':'||event_id||':'||instance_id||':'||monster_key||':'||combat_sequence||':'||amount||':'||revision,'|') FROM character_dungeon_growth_receipts") : null);
        bool Has(string table) => Query(db, $"SELECT count(*) FROM sqlite_schema WHERE name='{table}'") is 1L;
        bool HasColumn(string table, string column) => Query(db,
            $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name='{column}'") is 1L;
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
