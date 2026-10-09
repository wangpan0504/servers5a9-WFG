using static DfoServer.Testing.TestCheck;
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
                Check(Query(db,"SELECT count(*) FROM character_quest_skip_receipts") is 0L,
                    "当前完整结构包含空跳过回执，不补造旧角色完成记录");
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
                "DROP TABLE character_quest_lifecycle_receipts",
                "DROP TABLE character_quest_skip_receipts", "ALTER TABLE character_quest_skip_receipts DROP COLUMN plan_snapshot",
                "DROP TABLE character_tp_sources", "DROP TABLE character_skill_commands", "DROP TABLE character_skill_combos",
                "DROP TABLE character_dungeon_growth_receipts",
                "ALTER TABLE character_dungeon_growth_receipts DROP COLUMN resource_source",
                "DROP TABLE character_dungeon_state", "DROP TABLE character_dungeon_loot_receipts",
                "DROP TABLE character_dungeon_pickup_receipts", "DROP TABLE character_quest_lifecycle_receipts; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_dungeon_settlement_receipts",
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
            VerifyMasterQuest14Compatibility(root);
            VerifyQuestSystemMigration(root);
            VerifyExistingQuestSystemV14(root);
            VerifyExistingQuestType49(root);
            VerifyMasterQuest17Migration(root);
            VerifyExistingQuestFatigueV14(root);
            VerifyQuestSkipConstraints(root);
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

    private static void VerifyQuestSystemMigration(string root)
    {
        string path = Path.Combine(root, "quest-system-v13.db");
        _ = new CharacterRepository(path);
        string[] tables; string[][] columns; string[] previous;
        using (var db = Open(path))
        {
            RewindQuestSystemToV13(db);
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,experience,gold) VALUES('history',0,'History',7,38,1,450,234,0,60,123)");
            Execute(db, "INSERT INTO character_skill_points VALUES(1,0,45,6),(1,1,78,9)");
            Execute(db, "INSERT INTO character_active_main_quests VALUES(1,2,6217,255,'1234567890abcdef1234567890abcdef')");
            Execute(db, "INSERT INTO character_completed_quests VALUES(1,3145)");
            Execute(db, "INSERT INTO character_dungeon_growth_receipts(rowid,character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES(41,1,'history','1234567890abcdef1234567890abcdef','old-run',3145,1,1,76126,1,1,1,20,'legacy-rule','legacy-source',1,1,1,1,40,60,7)");
            Execute(db, "INSERT INTO character_dungeon_settlement_receipts VALUES(1,'history','old-settlement','old-run',3145,1,1,3,76126,zeroblob(16),x'01',1,20,0,20,-1,'legacy-rule','legacy-source',1,1,60,80,8)");
            Execute(db, "INSERT INTO character_dungeon_loot_receipts VALUES(1,'history','1234567890abcdef1234567890abcdef','old-run',1,1,76126,1,'legacy-rule','legacy-source',9,'{}',100,200,0)");
            Execute(db, "INSERT INTO character_dungeon_pickup_receipts VALUES(1,'history','1234567890abcdef1234567890abcdef',9,'old-run',1,1,0,177,1,'{}',8)");
            Execute(db, "INSERT INTO character_quest_clear_receipts(rowid,character_id,account,acceptance_id,quest_id,quest_slot,event_id,instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision) VALUES(43,1,'history','234567890abcdef1234567890abcdef1',3145,0,'1234567890abcdef1234567890abcdef','old-run',1,1,3,76126,1,7)");
            Execute(db, """
                INSERT INTO character_quest_completion_receipts(rowid,character_id,account,acceptance_id,quest_id,event_id,instance_id,task_attempt,epoch,generation,
                  rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,experience_amount,gold_amount,
                  before_level,after_level,before_experience,after_experience,before_revision,revision,rewards)
                VALUES(47,1,'history','234567890abcdef1234567890abcdef1',3145,'1234567890abcdef1234567890abcdef','old-run',1,1,1,
                  'legacy-rule-v0','legacy-source','legacy-quest',2,20,-15,'old difficulty',9,20,0,1,1,60,80,7,8,'[{"legacy":1},{},{},{},{},{},{}]');
                """);
            Execute(db, "CREATE INDEX ordinary_history_index ON character_dungeon_growth_receipts(rule_id)");
            Execute(db, "CREATE TABLE ordinary_history_trigger_calls(value INTEGER)");
            Execute(db, "CREATE TRIGGER ordinary_history_trigger AFTER INSERT ON character_dungeon_growth_receipts BEGIN INSERT INTO ordinary_history_trigger_calls VALUES(1); END");
            tables = TableNames(db); columns = tables.Select(table => ColumnNames(db, table)).ToArray();
            previous = tables.Select((table, index) => Rows(db, table, columns[index])).ToArray();
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            That(Query(db, "PRAGMA user_version") is long currentVersion && currentVersion == SqliteMigrations.CurrentVersion && tables.Select((table, index) => Rows(db, table, columns[index])).SequenceEqual(previous),
                "连续已发布迁移保存v13所有旧表原列、rowid、序列、接取身份和领奖规则原值");
            That(Query(db, "SELECT clear_acceptance_id=acceptance_id AND condition_type=3 AND selector=65535 AND multiplier=1 AND consumed='[]' AND changed_items='[]' AND plan_snapshot IS NULL FROM character_quest_completion_receipts WHERE rowid=47") is 1L,
                "旧领奖回执仅补来源关联和默认元数据，不重写旧JSON或规则版本");
            That(Query(db, "SELECT quest_cycle_day||':'||equipment_slot_flags FROM characters") is "0:0" &&
                Query(db, "SELECT daily_period FROM character_completed_quests") is 0L && Query(db, "SELECT answer IS NULL FROM character_completed_quests") is 1L &&
                new[] { "character_quest_lifecycle_receipts", "account_completed_quests", "character_quest_drop_receipts", "character_quest_skip_receipts" }
                    .All(table => Query(db, "SELECT count(*) FROM " + table) is 0L),
                "任务系统建立四张空账本及四个默认列，不补造任务、回答、周期或解锁");
            That(Query(db, "SELECT count(*) FROM ordinary_history_trigger_calls") is 0L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN('ordinary_history_index','ordinary_history_trigger')") is 2L,
                "迁移保留附加索引与触发器，恢复历史行时不重新执行触发器");
            That(Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L && Query(db, "PRAGMA integrity_check") is "ok",
                "有历史父子账本的v13直接迁移仍保持完整外键");
            Execute(db, "INSERT INTO character_dungeon_growth_receipts SELECT character_id,account,'ordinary','new-run',0,task_attempt,epoch,map_id,2,2,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_growth_receipts WHERE rowid=41");
            Execute(db, "INSERT INTO character_dungeon_settlement_receipts SELECT character_id,account,'ordinary-settlement','new-run',0,task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_settlement_receipts WHERE event_id='old-settlement'");
            That(Query(db, "SELECT count(*) FROM character_dungeon_growth_receipts WHERE quest_id=0") is 1L &&
                Query(db, "SELECT count(*) FROM character_dungeon_settlement_receipts WHERE quest_id=0") is 1L,
                "普通无任务副本可保存成长与结算，不生成任务完成记录");
        }
        string migrated = Snapshot(path);
        _ = new CharacterRepository(path); _ = new AuthenticationRepository(path);
        That(Snapshot(path) == migrated, "合并迁移重开不重复修改历史任务或账本");

        string rollback = Path.Combine(root, "quest-system-v14-rollback.db");
        _ = new CharacterRepository(rollback);
        using (var db = Open(rollback))
        {
            RewindQuestSystemToV13(db);
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,experience,gold) VALUES('keep',0,'V14Rollback',9,38,1,450,234,0,321,456)");
            Execute(db, "CREATE TABLE character_quest_skip_receipts(collision INTEGER)");
        }
        string before = Snapshot(rollback);
        Reject<SqliteException>(() => new CharacterRepository(rollback), "任务结构末段跳过账本撞表时回滚此前全部DDL及数据恢复");
        That(Snapshot(rollback) == before, "升级失败保留v13版本、全部原表定义、rowid和角色资产");
    }

    private static void VerifyMasterQuest14Compatibility(string root)
    {
        string path = Path.Combine(root, "master-quest-v14.db");
        _ = new CharacterRepository(path);
        var rows = new Dictionary<string, (string[] Columns, string Rows)>();
        using (var db = Open(path))
        {
            // 已发布任务v14有任务结构、旧回执，但尚未拥有普通分支疲劳表。
            RewindQuestReceiptsToV17(db);
            Execute(db, "DROP TABLE character_dungeon_fatigue_receipts; DROP TABLE character_fatigue; PRAGMA user_version=14");
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,quest_cycle_day,equipment_slot_flags) VALUES('master-quest',0,'MasterQuest',9,38,1,450,234,0,20261009,7)");
            Execute(db, "INSERT INTO character_completed_quests VALUES(1,6217,20261009,2)");
            Execute(db, "INSERT INTO accounts(account_id,salt,password_hash,iterations) VALUES('master-quest',x'01',x'02',1)");
            Execute(db, "INSERT INTO account_completed_quests VALUES('master-quest',6217,1,'1234567890abcdef1234567890abcdef',20261009,2)");
            Execute(db, "INSERT INTO character_dungeon_growth_receipts(rowid,character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES(41,1,'master-quest','1234567890abcdef1234567890abcdef','old-run',3145,1,1,76126,1,1,1,20,'legacy-rule','legacy-source',1,1,1,1,40,60,9)");
            Execute(db, "INSERT INTO character_quest_drop_receipts(rowid,character_id,account,event_id,before_revision,revision,plan_snapshot,changed_items) VALUES(73,1,'master-quest','1234567890abcdef1234567890abcdef',8,9,'{}','[]')");
            Execute(db, "CREATE INDEX quest_drop_history_index ON character_quest_drop_receipts(revision); CREATE TABLE quest_drop_trigger_calls(value INTEGER)");
            Execute(db, "CREATE TRIGGER quest_drop_history_trigger AFTER INSERT ON character_quest_drop_receipts BEGIN INSERT INTO quest_drop_trigger_calls VALUES(1); END");
            foreach (string table in TableNames(db))
            {
                string[] columns = ColumnNames(db, table);
                rows.Add(table, (columns, Rows(db, table, columns)));
            }
        }
        string rollback = Path.Combine(root, "master-quest-v14-rollback.db");
        File.Copy(path, rollback);
        using (var db = Open(rollback)) Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
        string before = Snapshot(rollback);
        RejectDatabase(() => new CharacterRepository(rollback));
        Check(Snapshot(rollback) == before, "任务v14末段校验失败回滚版本、父子账本、索引及触发器");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long currentVersion && currentVersion == SqliteMigrations.CurrentVersion && Query(db, "SELECT revision||':'||quest_cycle_day||':'||equipment_slot_flags FROM characters") is "9:20261009:7" &&
                Query(db, "SELECT daily_period||':'||answer FROM character_completed_quests") is "20261009:2" &&
                Query(db, "SELECT daily_period||':'||answer FROM account_completed_quests") is "20261009:2" &&
                Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "master已发布任务v14升级18不重复建任务结构、不改周期答案、资产或revision");
            foreach (var row in rows)
                Check(Rows(db, row.Key, row.Value.Columns) == row.Value.Rows, "任务v14保留原表全部数据及rowid：" + row.Key);
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN ('quest_drop_history_index','quest_drop_history_trigger')") is 2L &&
                Query(db, "SELECT count(*) FROM quest_drop_trigger_calls") is 0L,
                "任务掉落索引和触发器保留，迁移不重新触发历史插入");
        }
        string upgraded = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == upgraded, "含任务掉落的v14升级后重开不改账本");
    }

    private static void VerifyQuestSkipConstraints(string root)
    {
        string path = Path.Combine(root, "quest-skip-constraints.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('skip-history',0,'SkipHistory',19,38,1,450,234,0)");
            const string receipt = "INSERT INTO character_quest_skip_receipts VALUES(1,'skip-history','234567890abcdef1234567890abcdef1',19,6217,19,20,'{}','234567890abcdef1234567890abcdef1','QuickClear')";
            Execute(db, receipt);
            Reject<SqliteException>(() => Execute(db, receipt), "跳过回执按角色和接取身份唯一");
            Reject<SqliteException>(() => Execute(db, receipt.Replace("234567890abcdef1234567890abcdef1", "34567890abcdef1234567890abcdef12").Replace("19,20,'{}'", "19,21,'{}'")), "跳过回执revision只增加一次");
            Reject<SqliteException>(() => Execute(db, receipt.Replace("'skip-history'", "'wrong-owner'").Replace("234567890abcdef1234567890abcdef1", "4567890abcdef1234567890abcdef123")), "跳过回执保留角色账号归属外键");
        }
        string committed = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == committed, "v14再次初始化保留已有跳过回执，不重复迁移");
    }

    private static void VerifyExistingQuestSystemV14(string root)
    {
        string path = Path.Combine(root,"quest-existing-v14.db");
        using (var source = Open(Path.Combine(root,"quest-system-v13.db")))
        using (var target = Open(path)) source.BackupDatabase(target);
        const string completion = "character_quest_completion_receipts", skip = "character_quest_skip_receipts";
        // 冻结实际已运行v14的8列跳过结构；完成表撤回49/70约束，不只改版本号。
        const string previousSkip = """
            CREATE TABLE character_quest_skip_receipts(
              character_id INTEGER NOT NULL,account TEXT NOT NULL,
              acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
              parent_key INTEGER NOT NULL CHECK(parent_key BETWEEN 0 AND 2147483647),question_quest INTEGER NOT NULL CHECK(question_quest BETWEEN 1 AND 29999),
              before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
              plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot) AND json_type(plan_snapshot)='object'),
              PRIMARY KEY(character_id,acceptance_id),
              FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
            );
            """;
        string[] tables; string[][] columns; string[] before;
        using (var db = Open(path))
        {
            Execute(db,"DROP TABLE character_dungeon_fatigue_receipts; DROP TABLE character_fatigue; PRAGMA user_version=14");
            ChangeCompletionDefinition(db,sql => sql.Replace("44,49,64","44,64").Replace("65,67,70)","65,67)"));
            Execute(db,"DROP TABLE " + skip); Execute(db,previousSkip);
            Execute(db,"INSERT INTO character_quest_skip_receipts(rowid,character_id,account,acceptance_id,parent_key,question_quest,before_revision,revision,plan_snapshot) VALUES(59,1,'history','34567890abcdef1234567890abcdef12',19,6217,6,7,'{\"Old\":true}')");
            Execute(db,"CREATE TABLE v14_skip_trigger_calls(value INTEGER)");
            Execute(db,"CREATE TRIGGER v14_skip_trigger AFTER INSERT ON character_quest_skip_receipts BEGIN INSERT INTO v14_skip_trigger_calls VALUES(1); END");
            tables = TableNames(db); columns = tables.Select(table => ColumnNames(db,table)).ToArray();
            before = tables.Select((table,index) => Rows(db,table,columns[index])).ToArray();
        }
        string rollback = Path.Combine(root,"quest-existing-v14-rollback.db"), unknown = Path.Combine(root,"quest-existing-v14-unknown.db");
        using (var source = Open(path))
        {
            using (var target = Open(rollback)) source.BackupDatabase(target);
            using (var target = Open(unknown)) source.BackupDatabase(target);
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            for (int index = 0; index < tables.Length; index++)
                Check(Rows(db,tables[index],columns[index]) == before[index],"已有v14保留" + tables[index] + "全部旧列、rowid和数据");
            Check(Query(db,"PRAGMA user_version") is long currentVersion && currentVersion == SqliteMigrations.CurrentVersion,"旧任务v14按已发布版本升级，master只追加唯一v18");
            Check(Query(db,"SELECT operation_id||':'||operation_kind FROM character_quest_skip_receipts WHERE rowid=59") is "34567890abcdef1234567890abcdef12:QuickClear",
                "旧跳过回执使用原接取身份及QuickClear类型，不改冻结快照");
            Check(Query(db,"SELECT count(*) FROM v14_skip_trigger_calls") is 0L,"保全历史跳过不重新触发业务触发器");
            var names = ColumnNames(db,completion);
            string selected = string.Join(',',names.Select(name => name switch
            {
                "\"acceptance_id\"" => "'4567890abcdef1234567890abcdef123'",
                "\"condition_type\"" => "70", "\"plan_snapshot\"" => "'{}'",
                "\"event_id\"" or "\"instance_id\"" or "\"task_attempt\"" or "\"epoch\"" or "\"clear_acceptance_id\"" => "NULL",
                _ => name
            }));
            Execute(db,"INSERT INTO " + completion + "(" + string.Join(',',names) + ") SELECT " + selected + " FROM " + completion + " WHERE rowid=47");
            Check(Query(db,"SELECT count(*) FROM character_quest_completion_receipts WHERE condition_type=70") is 1L,"对齐后的已有v14实际接受type70回执");
            Execute(db,"INSERT INTO character_quest_skip_receipts VALUES(1,'history',NULL,NULL,NULL,7,8,'{}','567890abcdef1234567890abcdef1234','Manual')");
            Check(Query(db,"SELECT count(*) FROM character_quest_skip_receipts WHERE operation_kind='Manual' AND question_quest IS NULL") is 1L,"手动跳过无问题时不伪造question或接取身份");
            Execute(db,"INSERT INTO character_quest_skip_receipts VALUES(1,'history',NULL,NULL,NULL,8,9,'{}','67890abcdef1234567890abcdef12345','Ticket')");
            Check(Query(db,"SELECT count(*) FROM character_quest_skip_receipts WHERE operation_kind='Ticket' AND acceptance_id IS NULL AND question_quest IS NULL") is 1L,"任务券以独立操作身份保存，不伪造普通接取或回答入口");
            Reject<SqliteException>(() => Execute(db,"INSERT INTO character_quest_skip_receipts VALUES(1,'history',NULL,NULL,NULL,7,8,'{}','67890abcdef1234567890abcdef12345','QuickClear')"),"普通旅程跳过仍要求真实接取及问题");
            Check(Query(db,"PRAGMA integrity_check") is "ok" && Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L,"保全对齐后数据及历史外键完整");
        }
        string after = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == after,"最终v18再次初始化不重建、改写或重放回执");
        string original = Snapshot(rollback);
        using (var db = Open(rollback))
        {
            Execute(db,"CREATE TEMP TABLE character_quest_skip_receipts_quest_migration(collision INTEGER)");
            Reject<SqliteException>(() => SqliteMigrations.Apply(db),"v18第二张表保全失败时回滚第一张表及数据");
        }
        Check(Snapshot(rollback) == original,"v18对齐失败保留两张原定义、全部历史字段和版本");
        using (var db = Open(unknown)) Execute(db,"ALTER TABLE character_quest_skip_receipts ADD COLUMN unknown_value INTEGER");
        original = Snapshot(unknown);
        Reject<InvalidDataException>(() => new CharacterRepository(unknown),"不把未知v14结构当作可自动修复开发库");
        Check(Snapshot(unknown) == original,"拒绝未知v14定义时保留原库");
    }

    private static void VerifyExistingQuestType49(string root)
    {
        string path = Path.Combine(root,"quest-existing-v14-before49.db");
        using (var source = Open(Path.Combine(root,"quest-system-v13.db")))
        using (var target = Open(path)) source.BackupDatabase(target);
        string[] tables; string[][] columns; string[] before;
        using (var db = Open(path))
        {
            Execute(db,"DROP TABLE character_dungeon_fatigue_receipts; DROP TABLE character_fatigue; PRAGMA user_version=14");
            ChangeCompletionDefinition(db,sql => sql.Replace("44,49,64","44,64"));
            Execute(db,"INSERT INTO character_quest_skip_receipts VALUES(1,'history',NULL,NULL,NULL,7,8,'{\"Manual\":true}','4567890abcdef1234567890abcdef123','Manual')");
            Execute(db,"INSERT INTO character_quest_skip_receipts VALUES(1,'history',NULL,NULL,NULL,8,9,'{\"Ticket\":true}','67890abcdef1234567890abcdef12345','Ticket')");
            tables = TableNames(db); columns = tables.Select(table => ColumnNames(db,table)).ToArray();
            before = tables.Select((table,index) => Rows(db,table,columns[index])).ToArray();
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(tables.Select((table,index) => Rows(db,table,columns[index])).SequenceEqual(before),
                "已含70的旧v14补49仍保留全部表旧列、rowid、JSON、余额及序列");
            Check(Query(db,"PRAGMA user_version") is long currentVersion && currentVersion == SqliteMigrations.CurrentVersion,"旧任务v14的接取型扩展统一在v18迁移");
            var names = ColumnNames(db,"character_quest_completion_receipts");
            string selected = string.Join(',',names.Select(name => name switch
            {
                "\"acceptance_id\"" => "'567890abcdef1234567890abcdef1234'",
                "\"condition_type\"" => "49", "\"plan_snapshot\"" => "'{}'",
                "\"event_id\"" or "\"instance_id\"" or "\"task_attempt\"" or "\"epoch\"" or "\"clear_acceptance_id\"" => "NULL",
                _ => name
            }));
            Execute(db,"INSERT INTO character_quest_completion_receipts(" + string.Join(',',names) + ") SELECT " +
                selected + " FROM character_quest_completion_receipts WHERE rowid=47");
            Check(Query(db,"SELECT count(*) FROM character_quest_completion_receipts WHERE condition_type=49") is 1L,
                "接取型49使用普通非清图回执，不伪造副本字段");
            Check(Query(db,"SELECT count(*) FROM ordinary_history_trigger_calls") is 1L,
                "历史触发器不在保全中重放，新业务插入仍正常触发");
            Check(Query(db,"PRAGMA integrity_check") is "ok" && Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "49约束保全后完整性与历史外键正常");
        }
        string committed = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == committed,"包含49的最终v18重开不重建或重发历史奖励");
    }

    private static void VerifyMasterQuest17Migration(string root)
    {
        string path = Path.Combine(root, "master-quest-v17.db");
        using (var from = Open(Path.Combine(root, "quest-system-v13.db")))
        using (var to = Open(path)) from.BackupDatabase(to);
        string[] tables; string[][] columns; string[] before;
        using (var db = Open(path))
        {
            RewindQuestReceiptsToV17(db);
            Execute(db, "PRAGMA user_version=17");
            Execute(db, "INSERT INTO character_quest_skip_receipts(rowid,character_id,account,acceptance_id,parent_key,question_quest,before_revision,revision,plan_snapshot) VALUES(59,1,'history','34567890abcdef1234567890abcdef12',19,6217,6,7,'{\"Old\":true}')");
            Execute(db, "INSERT INTO character_dungeon_state VALUES(1,'history',700,42,123,4,56)");
            Execute(db, "INSERT INTO character_fatigue VALUES(1,'history',7,156,9,149,3,4)");
            Execute(db, "INSERT INTO character_dungeon_fatigue_receipts(rowid,character_id,account,instance_id,task_attempt,map_id,epoch,generation,rule_id,before_revision,revision,before_state,after_state) VALUES(81,1,'history','abcdef1234567890abcdef1234567890',42,76126,9,7,'fatigue-old',6,7,'{\"Used\":6}','{\"Used\":7}')");
            Execute(db, "INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(12345,1,'history','{\"Uid\":12345,\"Legacy\":true}')");
            Execute(db, "INSERT INTO inventory_avatar_details VALUES(12345,'{\"Detail\":true}')");
            Execute(db, "INSERT INTO character_quest_drop_receipts(rowid,character_id,account,event_id,before_revision,revision,plan_snapshot,changed_items) VALUES(73,1,'history','1234567890abcdef1234567890abcdef',6,7,'{\"QuestDrop\":true}','[]')");
            Execute(db, "INSERT INTO character_dungeon_growth_receipts SELECT character_id,account,'legacy','drop-old-run',quest_id,task_attempt,epoch,map_id,3,3,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_growth_receipts WHERE rowid=41");
            AddDropDependencyFixture(db);
            Execute(db, "CREATE TABLE v18_trigger_calls(value INTEGER)");
            Execute(db, "CREATE INDEX v18_completion_index ON character_quest_completion_receipts(rule_id); CREATE INDEX v18_skip_index ON character_quest_skip_receipts(revision)");
            Execute(db, "CREATE TRIGGER v18_completion_trigger AFTER INSERT ON character_quest_completion_receipts BEGIN INSERT INTO v18_trigger_calls VALUES(1); END");
            Execute(db, "CREATE TRIGGER v18_skip_trigger AFTER INSERT ON character_quest_skip_receipts BEGIN INSERT INTO v18_trigger_calls VALUES(2); END");
            tables = TableNames(db); columns = tables.Select(table => ColumnNames(db, table)).ToArray();
            before = tables.Select((table,index) => Rows(db,table,columns[index])).ToArray();
        }
        string rollback = Path.Combine(root, "master-quest-v17-rollback.db");
        using (var from = Open(path))
        using (var to = Open(rollback)) from.BackupDatabase(to);
        string rollbackBefore = Snapshot(rollback);
        using (var db = Open(rollback))
        {
            Execute(db,"CREATE TEMP TABLE character_quest_skip_receipts_quest_migration(collision INTEGER)");
            Reject<SqliteException>(() => SqliteMigrations.Apply(db),"v18第二张回执表重建失败时回滚v17第一张表和历史数据");
        }
        Check(Snapshot(rollback) == rollbackBefore,"v17到v18失败保留版本、全部DDL、rowid、UID、序列、疲劳及掉落账本");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db,"PRAGMA user_version") is 18L,"最新master17只执行唯一新增v18");
            for (int index=0;index<tables.Length;index++)
                Check(Rows(db,tables[index],columns[index]) == before[index],"v18保留master17原表全部历史列、rowid和数据：" + tables[index]);
            Check(Query(db,"SELECT operation_id||':'||operation_kind FROM character_quest_skip_receipts WHERE rowid=59") is "34567890abcdef1234567890abcdef12:QuickClear",
                "master旧跳过身份原样转为QuickClear操作，不改冻结规划");
            Check(Query(db,"SELECT count(*) FROM v18_trigger_calls") is 0L &&
                Query(db,"SELECT count(*) FROM sqlite_schema WHERE name IN('v18_completion_index','v18_skip_index','v18_completion_trigger','v18_skip_trigger')") is 4L,
                "v18保留两张回执附属索引和触发器，不重新执行历史插入");
            Check(Query(db,"SELECT used||':'||maximum||':'||used_maximum||':'||remaining||':'||context_auxiliary||':'||selection_auxiliary FROM character_fatigue") is "7:156:9:149:3:4" &&
                Query(db,"SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'") is 12345L,
                "v18不修改master疲劳余额、物品UID或序列");
            Check(Query(db,"PRAGMA integrity_check") is "ok" && Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "v18保留成长、普通掉落三层集合和任务掉落历史外键");
        }
        string after = Snapshot(path);
        _ = new CharacterRepository(path); _ = new AuthenticationRepository(path);
        Check(Snapshot(path) == after,"v18重开不重建表、不重放领取或消耗");
    }

    private static void VerifyExistingQuestFatigueV14(string root)
    {
        string path = Path.Combine(root, "quest-v14-existing-fatigue.db");
        using (var from = Open(Path.Combine(root,"master-quest-v17.db")))
        using (var to = Open(path)) from.BackupDatabase(to);
        string[] tables; string[][] columns; string[] before;
        using (var db = Open(path))
        {
            RewindQuestReceiptsToV17(db);
            ChangeReceiptDefinition(db,"character_dungeon_fatigue_receipts",sql => sql.Replace("CHECK(map_id>0)","CHECK(map_id BETWEEN 76121 AND 76126)"));
            Execute(db,"PRAGMA user_version=14");
            tables = TableNames(db); columns = tables.Select(table => ColumnNames(db,table)).ToArray();
            before = tables.Select((table,index) => Rows(db,table,columns[index])).ToArray();
        }
        foreach (bool missing in new[] { false, true })
        {
            string rejected = Path.Combine(root,missing ? "v14-fatigue-partial.db" : "v14-fatigue-unknown.db");
            using (var from = Open(path))
            using (var to = Open(rejected))
            {
                from.BackupDatabase(to);
                Execute(to,missing ? "DROP TABLE character_dungeon_fatigue_receipts" : "ALTER TABLE character_fatigue ADD COLUMN unknown_value INTEGER");
            }
            string snapshot = Snapshot(rejected);
            Reject<InvalidDataException>(() => new CharacterRepository(rejected),"旧v14只兼容完整且精确已知的疲劳结构");
            Check(Snapshot(rejected) == snapshot,"未知或部分疲劳表拒绝时保存全部原历史和v14版本");
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db,"PRAGMA user_version") is 18L,"已存在疲劳的任务v14保留master迁移顺序并升级至18");
            for (int index=0;index<tables.Length;index++)
                Check(Rows(db,tables[index],columns[index]) == before[index],"已知v14疲劳兼容保留全部原列、rowid和历史：" + tables[index]);
            Check(Query(db,"SELECT count(*) FROM v18_trigger_calls") is 0L && Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "任务掉落、普通集合和疲劳一起参与原v16重建，不重放历史事件");
            Execute(db,"INSERT INTO character_dungeon_fatigue_receipts SELECT character_id,account,'1234567890abcdef1234567890abcdef',task_attempt+1,81111,epoch,generation,rule_id,before_revision,revision,before_state,after_state FROM character_dungeon_fatigue_receipts WHERE rowid=81");
            Check(Query(db,"SELECT count(*) FROM character_dungeon_fatigue_receipts WHERE map_id=81111") is 1L,
                "既有疲劳回执保留原值同时由已发布v16支持普通地图索引");
        }
        string after = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == after,"旧v14疲劳兼容后重开不重建或重置");
    }

    static void ChangeCompletionDefinition(SqliteConnection db,Func<string,string> change) =>
        ChangeReceiptDefinition(db, "character_quest_completion_receipts", change);

    static void ChangeReceiptDefinition(SqliteConnection db, string table, Func<string,string> change, string[]? columns = null)
    {
        string ddl = (string)Query(db,"SELECT sql FROM sqlite_schema WHERE name='" + table + "'")!;
        var names = columns ?? ColumnNames(db,table);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name='" + table + "' AND type IN('index','trigger') AND sql IS NOT NULL";
        var dependencies = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) dependencies.Add(reader.GetString(0));
        Execute(db,"CREATE TEMP TABLE saved_receipt AS SELECT rowid AS old_rowid,* FROM " + table);
        Execute(db,"DROP TABLE " + table); Execute(db,change(ddl));
        Execute(db,"INSERT INTO " + table + "(rowid," + string.Join(',',names) + ") SELECT old_rowid," + string.Join(',',names) + " FROM temp.saved_receipt");
        Execute(db,"DROP TABLE temp.saved_receipt");
        foreach (string dependency in dependencies) Execute(db,dependency);
    }

    // 按实际v17回执约束及八列定义回退，保留疲劳和普通副本历史，不只改版本号。
    static void RewindQuestReceiptsToV17(SqliteConnection db)
    {
        ChangeCompletionDefinition(db, sql => sql.Replace("44,49,64", "44,64").Replace("65,67,70)", "65,67)"));
        const string skip = "character_quest_skip_receipts";
        string[] names = ColumnNames(db, skip).Where(name => name is not "\"operation_id\"" and not "\"operation_kind\"").ToArray();
        ChangeReceiptDefinition(db, skip, _ => QuestSkipV17Schema, names);
    }

    const string QuestSkipV17Schema = """
        CREATE TABLE character_quest_skip_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          parent_key INTEGER NOT NULL CHECK(parent_key BETWEEN 0 AND 2147483647),question_quest INTEGER NOT NULL CHECK(question_quest BETWEEN 1 AND 29999),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot) AND json_type(plan_snapshot)='object'),
          PRIMARY KEY(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private static void VerifyReleasedMigrations(string root)
    {
        foreach (int oldVersion in Enumerable.Range(1, SqliteMigrations.CurrentVersion - 1))
        {
            string path = Path.Combine(root, $"released-v{oldVersion}.db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,experience,gold) VALUES('migrated',0,'Migrated',7,38,1,450,234,0,2,60,123)");
                Execute(db, "INSERT INTO character_skill_points VALUES(1,0,12,0),(1,1,34,0)");
                Execute(db, "INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES(1,0,3145,1,'1234567890abcdef1234567890abcdef')");
                Execute(db, "INSERT INTO account_wallets(account,revision,rental_points) VALUES('migrated',5,17)");
                if (oldVersion >= 3)
                    Execute(db, "INSERT INTO character_dungeon_growth_receipts VALUES(1,'migrated','legacy','legacy-instance',3145,1,1,76121,123,99,1,20,'legacy-rule','legacy-hash',1,1,2,2,40,60,7)");
                // 旧版必须确实缺少新结构；只改版本号会让真实 CREATE/ALTER 迁移撞表或撞列。
                if (oldVersion < 15)
                    Execute(db, "DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue");
                if (oldVersion >= 17) RewindQuestReceiptsToV17(db);
                else RewindQuestSystemToV13(db, keepFatigue: oldVersion >= 15);
                if (oldVersion < 13) Execute(db, "DROP TABLE character_quest_completion_receipts");
                if (oldVersion < 13) Execute(db, "DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id");
                if (oldVersion < 12) Execute(db, "DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos");
                if (oldVersion < 11) Execute(db, "DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts");
                if (oldVersion < 10) Execute(db, "DROP TABLE character_dungeon_settlement_receipts");
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
                if (oldVersion == 13)
                    AddDropDependencyFixture(db);
                Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_dungeon_state'") is long ownerTables && ownerTables == (oldVersion >= 8 ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_dungeon_growth_receipts'") is long growthTables &&
                    growthTables == (oldVersion >= 3 ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='rental_points'") is long columns &&
                    columns == (oldVersion == 1 ? 0 : 1), "真实旧版模拟按v1/v2/v3保留已发布结构，移除后续副本owner表");
            }
            if (oldVersion == 13)
            {
                foreach (bool incomplete in new[] { false, true })
                {
                    string failedDependency = Path.Combine(root, incomplete ? "partial-drop-dependency.db" : "drop-dependency-rollback.db");
                    using (var source = Open(path))
                    using (var target = Open(failedDependency))
                    {
                        source.BackupDatabase(target);
                        Execute(target, incomplete ? "DROP TABLE character_dungeon_drop_pickups" : "ALTER TABLE accounts DROP COLUMN disabled");
                    }
                    string oldSnapshot = Snapshot(failedDependency);
                    if (incomplete)
                        Reject<InvalidDataException>(() => new CharacterRepository(failedDependency), "集合外键依赖不完整拒绝普通升级");
                    else
                        Reject<SqliteException>(() => new CharacterRepository(failedDependency), "复制完整集合依赖后必需结构失败回滚整链");
                    Check(Snapshot(failedDependency) == oldSnapshot, "拒绝或晚期失败保留旧版本、成长、集合行、索引和角色资产");
                }
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
                Check(Query(db, "SELECT length(acceptance_id)=32 AND acceptance_id<>'00000000000000000000000000000000' FROM character_active_main_quests WHERE character_id=1") is 1L &&
                    Query(db, "SELECT count(*) FROM character_quest_clear_receipts") is 0L &&
                    Query(db, "SELECT count(*) FROM character_quest_completion_receipts") is 0L,
                    "v13建立稳定接取身份，未凭历史scalar补造通关账本");

                Check(Query(db, "SELECT count(*) FROM character_skill_point_sources") is 0L &&
                    Query(db, "SELECT count(*) FROM account_expert_contracts") is 0L,
                    "v11只增加空来源表，不重算旧SP或授予契约");
                Check(Query(db, "SELECT count(*) FROM character_tp_sources") is 0L &&
                    Query(db, "SELECT count(*) FROM character_skill_commands") is 0L && Query(db, "SELECT count(*) FROM character_skill_combos") is 0L,
                    "v12只增加空TP来源、指令及连招表，保留旧角色原有布局");
                Check(Query(db, "SELECT count(*) FROM character_dungeon_settlement_receipts") is 0L,
                    "v1至v9升级只增加空通关账本，不合成历史奖励");
                Check(Query(db,"SELECT equipment_slot_flags FROM characters WHERE id=1") is 0L,
                    "v14保留旧角色资产，不因等级、GM跳过或旧任务完成位补造装备槽解锁");
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
                if (oldVersion == 13)
                {
                    Check(Query(db, "SELECT payload FROM character_dungeon_drop_batches") is "原批次" &&
                        Query(db, "SELECT payload FROM character_dungeon_drop_objects") is "完整Core" &&
                        Query(db, "SELECT payload FROM character_dungeon_drop_pickups") is "已到账凭据" &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN ('drop_dependency_index','drop_dependency_guard')") is 2L,
                        "普通账本重建保留外部完整集合三层引用、全部行及附属索引触发器");
                    Reject<SqliteException>(() => Execute(db, "UPDATE character_dungeon_drop_objects SET event_id='missing'"),
                        "集合外键在重建及重命名后仍拒绝不存在的原成长来源");
                    Reject<SqliteException>(() => Execute(db, "INSERT INTO character_dungeon_drop_objects VALUES(1,'legacy',513,'')"),
                        "集合附属触发器在重建后仍生效");
                }
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
            RewindQuestSystemToV13(db);
            Execute(db, "DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_dungeon_settlement_receipts");
            Execute(db, "DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts");
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
        string questRollback = Path.Combine(root, "quest-upgrade-rollback.db");
        _ = new CharacterRepository(questRollback);
        using (var db = Open(questRollback))
        {
            Execute(db, "DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; PRAGMA user_version=12");
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('keep',0,'QuestKeep',11,38,1,450,234,0)");
            Execute(db, "INSERT INTO character_active_main_quests VALUES(1,0,3145,1)");
        }
        string questBefore = Snapshot(questRollback);
        Reject<SqliteException>(() => new CharacterRepository(questRollback), "合并v13领奖表撞表回滚接取身份和清图表");
        Check(Snapshot(questRollback) == questBefore, "合并迁移失败保留v12版本、完整原结构与任务条件");
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

    internal static void RewindQuestSystemToV13(string path)
    {
        using var db = Open(path);
        RewindQuestSystemToV13(db);
    }

    // 成功迁移夹具恢复完整已发布v13结构，不以改版本号冒充旧库。
    internal static void RewindQuestSystemToV13(SqliteConnection db, bool keepFatigue = false)
    {
        if (!keepFatigue)
            Execute(db, "DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue");
        Execute(db, "PRAGMA foreign_keys=OFF");
        Execute(db, "DROP TABLE character_quest_skip_receipts; DROP TABLE character_quest_drop_receipts; DROP TABLE account_completed_quests; DROP TABLE character_quest_lifecycle_receipts");
        Execute(db, "ALTER TABLE characters DROP COLUMN equipment_slot_flags; ALTER TABLE characters DROP COLUMN quest_cycle_day; ALTER TABLE character_completed_quests DROP COLUMN answer; ALTER TABLE character_completed_quests DROP COLUMN daily_period");
        RewindQuestCompletionToV13(db);
        foreach (string table in new[] { "character_dungeon_growth_receipts", "character_dungeon_settlement_receipts", "character_quest_clear_receipts" })
        {
            string source = (string)Query(db, "SELECT sql FROM sqlite_schema WHERE name='" + table + "'")!;
            string old = table == "character_quest_clear_receipts"
                ? source.Replace("PRIMARY KEY(character_id,acceptance_id),", "PRIMARY KEY(character_id,acceptance_id),UNIQUE(character_id,event_id),UNIQUE(character_id,instance_id),", StringComparison.Ordinal)
                : source.Replace("CHECK(quest_id>=0)", "CHECK(quest_id>0)", StringComparison.Ordinal);
            Execute(db, "CREATE TEMP TABLE old_receipts AS SELECT rowid AS old_rowid,* FROM " + table);
            Execute(db, "DROP TABLE " + table); Execute(db, old);
            string columns = string.Join(',', ColumnNames(db, table));
            Execute(db, "INSERT INTO " + table + "(rowid," + columns + ") SELECT old_rowid," + columns + " FROM temp.old_receipts");
            Execute(db, "DROP TABLE temp.old_receipts");
        }
        Execute(db, "PRAGMA user_version=13; PRAGMA foreign_keys=ON");
    }

    private static string[] TableNames(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
        using var reader = command.ExecuteReader(); var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static string[] ColumnNames(SqliteConnection db, string table)
    {
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA table_info(" + table + ")";
        using var reader = command.ExecuteReader(); var names = new List<string>();
        while (reader.Read()) names.Add('"' + reader.GetString(1).Replace("\"", "\"\"") + '"');
        return names.ToArray();
    }

    private static string Rows(SqliteConnection db, string table, string[] columns)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT rowid," + string.Join(',', columns) + " FROM " + table + " ORDER BY rowid";
        using var reader = command.ExecuteReader(); var rows = new List<object[]>();
        while (reader.Read()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values); }
        return System.Text.Json.JsonSerializer.Serialize(rows);
    }

    // 冻结已发布v13的26列和约束，不能以v14表仅改user_version冒充旧库。
    internal static void RewindQuestCompletionToV13(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE character_quest_completion_receipts_v13(
              character_id INTEGER NOT NULL,account TEXT NOT NULL,acceptance_id TEXT NOT NULL,
              quest_id INTEGER NOT NULL CHECK(quest_id=3145),event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
              task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
              generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
              resource_source TEXT NOT NULL,quest_source TEXT NOT NULL,
              native_index INTEGER NOT NULL CHECK(native_index>=0),base_experience INTEGER NOT NULL CHECK(base_experience>0),
              raw_additional_percent INTEGER NOT NULL,difficulty_key TEXT NOT NULL,difficulty_weight INTEGER NOT NULL CHECK(difficulty_weight>0),
              experience_amount INTEGER NOT NULL CHECK(experience_amount BETWEEN 1 AND 2147483647),gold_amount INTEGER NOT NULL CHECK(gold_amount=0),
              before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
              before_experience INTEGER NOT NULL CHECK(before_experience>=0),after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+experience_amount),
              before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
              rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array' AND json_array_length(rewards)=7),
              PRIMARY KEY(character_id,acceptance_id),UNIQUE(character_id,quest_id),
              FOREIGN KEY(character_id,acceptance_id) REFERENCES character_quest_clear_receipts(character_id,acceptance_id),
              FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
            INSERT INTO character_quest_completion_receipts_v13(rowid,character_id,account,acceptance_id,quest_id,event_id,instance_id,
              task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,
              difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,
              before_revision,revision,rewards)
            SELECT rowid,character_id,account,acceptance_id,quest_id,event_id,instance_id,task_attempt,epoch,generation,rule_id,
              resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,
              experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,rewards
              FROM character_quest_completion_receipts;
            DROP TABLE character_quest_completion_receipts;
            ALTER TABLE character_quest_completion_receipts_v13 RENAME TO character_quest_completion_receipts;
            """);
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        return db;
    }

    // 只模拟三层成长外键，避免在普通图测试中重复实现掉落模块的业务DDL；完整DDL另由依赖组合验证。
    private static void AddDropDependencyFixture(SqliteConnection db) => Execute(db, """
        CREATE TABLE character_dungeon_drop_batches(character_id INTEGER,event_id TEXT,payload TEXT NOT NULL,
          PRIMARY KEY(character_id,event_id),FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id));
        CREATE TABLE character_dungeon_drop_objects(character_id INTEGER,event_id TEXT,ground_key INTEGER,payload TEXT NOT NULL,
          PRIMARY KEY(character_id,ground_key),FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_drop_batches(character_id,event_id));
        CREATE TABLE character_dungeon_drop_pickups(character_id INTEGER,ground_key INTEGER,payload TEXT NOT NULL,
          PRIMARY KEY(character_id,ground_key),FOREIGN KEY(character_id,ground_key) REFERENCES character_dungeon_drop_objects(character_id,ground_key));
        CREATE INDEX drop_dependency_index ON character_dungeon_drop_objects(payload);
        CREATE TRIGGER drop_dependency_guard BEFORE INSERT ON character_dungeon_drop_objects
          WHEN NEW.payload='' BEGIN SELECT RAISE(ABORT,'空集合测试载荷'); END;
        INSERT INTO character_dungeon_drop_batches VALUES(1,'legacy','原批次');
        INSERT INTO character_dungeon_drop_objects VALUES(1,'legacy',512,'完整Core');
        INSERT INTO character_dungeon_drop_pickups VALUES(1,512,'已到账凭据');
        """);

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
            string.Join('|', TableNames(db).Select(table => table + ':' + Rows(db, table, ColumnNames(db, table)))));
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


    private static void Check(bool condition, string message) => TestCheck.That(condition, message);
}
