using static DfoServer.Testing.TestCheck;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    const string IncountTablesSql = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_dungeon_incount','account_dungeon_incount')";
    const string MainlineV62To66Sql = "SELECT (SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_growup_change','character_consumable_states'))" +
        "+(SELECT count(*) FROM pragma_table_info('characters') WHERE name='blacklist_count')" +
        "+(SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name='other_bonus_experience')" +
        "+(SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_quest_completion_receipts' AND sql LIKE '%60,64,65,67,70%')";
    const string SkinColumnSql = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='weapon_skin_state'";
    const string PermanentStatSql = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_permanent_stats'";
    const string ProficiencyGainSql = "SELECT (SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name='proficiency_gain')" +
        "+(SELECT count(*) FROM pragma_table_info('character_dungeon_growth_receipts') WHERE name='proficiency_gain')";
    // 主线 v69/v71/v72/v73 的冒险团结构（胶囊周期列、登录与推荐计数列、每周配置表、经验账本两列两表）；全有为 9。
    const string MainlineAdventureSql = "SELECT (SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name IN('capsule_week','login_streak_days','last_login_day','opening_experience','excluded_experience'))" +
        "+(SELECT count(*) FROM pragma_table_info('character_dungeon_state') WHERE name='recommended_dungeon_clears')" +
        "+(SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('account_adventure_weekly_config','character_adventure_credits','adventure_ledger_meta'))";

    // 副本次数两表现为 v88/v89（主线 v68 为道具永久属性来源，v69–v73 为冒险团，v74–v82 为熟练度到账至快捷聊天，v83–v87 为悲叹、塔与祭坛、装扮预设、对象key）。曾有六种非主线编号：合并前 fix/channel-town-entry 本地构建的 v62/v63（主线 v62–v66 另有含义），
    // 主线 v67 武器幻化之前合并分支的 v67/v68，主线 v68 之前本分支的 v68/v69，主线 v69–v73 之前本分支的 v69/v70，主线 v74–v82 之前本分支的 v74/v75，以及主线 v83–v87 之前本分支的 v83/v84。
    // 升级路径：新库、主线 v66/v67/v68/v73/v82/v87 旧库、本分支悲叹前旧编号 v83/v84、本地旧编号 v62/v63、合并分支旧编号 v67/v68、永久属性前旧编号 v68/v69、冒险团前旧编号 v69/v70、熟练度前旧编号 v74/v75；
    // 旧编号夹具都移除主线 v69/v71–v73 的追加结构（v70 结算分项按结构判定已是目标则跳过，夹具保留）；
    // 可选 DFO_MIGRATION_SAMPLE_DB 指向一份真实旧库，只读备份到临时目录后升级。
    static void IncountNumberingUpgrade(string root)
    {
        root = Path.Combine(root, "incount-numbering");
        Directory.CreateDirectory(root);

        string fresh = Path.Combine(root, "fresh.db");
        _ = new CharacterRepository(fresh);
        using (var db = Open(fresh))
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, IncountTablesSql) is 2L && Query(db, MainlineV62To66Sql) is 5L && Query(db, SkinColumnSql) is 1L && Query(db, PermanentStatSql) is 1L &&
                Query(db, MainlineAdventureSql) is 9L,
                "新库直接建出主线 v62–v87 与副本次数 v88/v89");

        foreach (int mainline in new[] { 66, 67, 68, 73, 82, 87 })
        {
            string master = Path.Combine(root, "master-v" + mainline + ".db");
            _ = new CharacterRepository(master);
            using (var db = Open(master))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('incount',0,'MasterOld',7,38,1,450,234,0,123)");
                if (mainline >= 67) Execute(db, "UPDATE characters SET weapon_skin_state='{\"Owned\":[5],\"Selected\":5}'");
                if (mainline < 68) Execute(db, "DROP TABLE character_permanent_stats");
                Execute(db, "DROP TABLE character_dungeon_incount; DROP TABLE account_dungeon_incount");
                if (mainline == 66) Execute(db, "ALTER TABLE characters DROP COLUMN weapon_skin_state");
                if (mainline < 69) RemoveMainlineAdventureAdditions(db);
                RewindRaidLifecycle(db);
                Execute(db, $"PRAGMA user_version={mainline}");
            }
            _ = new CharacterRepository(master);
            using (var db = Open(master))
                Check(Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) && Query(db, IncountTablesSql) is 2L && Query(db, MainlineV62To66Sql) is 5L &&
                    Query(db, SkinColumnSql) is 1L && Query(db, PermanentStatSql) is 1L && Query(db, MainlineAdventureSql) is 9L &&
                    Query(db, "SELECT count(*) FROM character_dungeon_incount") is 0L &&
                    Query(db, "SELECT name||':'||revision||':'||gold FROM characters") is "MasterOld:7:123" &&
                    Query(db, "SELECT weapon_skin_state FROM characters") is string skin &&
                    skin == (mainline >= 67 ? "{\"Owned\":[5],\"Selected\":5}" : "{\"Owned\":[],\"Selected\":0}"),
                    $"主线 v{mainline} 旧库只增量补到当前版本，不改角色与已有幻化状态");
            string masterCurrent = Snapshot(master);
            _ = new CharacterRepository(master);
            Check(Snapshot(master) == masterCurrent, $"主线 v{mainline} 旧库升级后重开不重复迁移");
        }

        foreach (int branch in new[] { 67, 68 })
        {
            string path = Path.Combine(root, "merge-branch-v" + branch + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('incount',0,'BranchOld',7,38,1,450,234,0,123)");
                Execute(db, "INSERT INTO character_dungeon_incount VALUES(1,'incount',7323,20261021,1,20261015,2)");
                Execute(db, "INSERT INTO account_dungeon_incount VALUES('incount',7184,20261015,3)");
                Execute(db, "ALTER TABLE characters DROP COLUMN weapon_skin_state; DROP TABLE character_permanent_stats");
                RemoveMainlineAdventureAdditions(db);
                if (branch == 67) Execute(db, "DROP TABLE account_dungeon_incount");
                RewindRaidLifecycle(db);
                Execute(db, $"PRAGMA user_version={branch}");
                Check(Query(db, SkinColumnSql) is 0L && Query(db, MainlineV62To66Sql) is 5L &&
                    Query(db, IncountTablesSql) is long tables && tables == (branch == 68 ? 2 : 1),
                    "合并分支旧编号夹具有主线 v62–v66 与次数表，缺主线 v67 幻化列");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) && Query(db, SkinColumnSql) is 1L && Query(db, MainlineV62To66Sql) is 5L && Query(db, IncountTablesSql) is 2L &&
                    Query(db, PermanentStatSql) is 1L && Query(db, MainlineAdventureSql) is 9L && Query(db, "SELECT weapon_skin_state FROM characters") is "{\"Owned\":[],\"Selected\":0}",
                    $"合并分支旧编号 v{branch} 库按列判定后补跑主线 v67–v73，并到当前版本");
                Check(Query(db, "SELECT character_id||':'||dungeon||':'||day||':'||daily_used||':'||week||':'||weekly_used FROM character_dungeon_incount") is "1:7323:20261021:1:20261015:2" &&
                    Query(db, "SELECT name||':'||revision||':'||gold FROM characters") is "BranchOld:7:123" &&
                    (branch == 68
                        ? Query(db, "SELECT account||':'||dungeon||':'||period||':'||used FROM account_dungeon_incount") is "incount:7184:20261015:3"
                        : Query(db, "SELECT count(*) FROM account_dungeon_incount") is 0L),
                    $"合并分支旧编号 v{branch} 库升级保留原有次数行和角色");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, $"合并分支旧编号 v{branch} 升级后重开不重复迁移");
        }

        foreach (int local in new[] { 62, 63 })
        {
            string path = Path.Combine(root, "local-v" + local + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('incount',0,'LocalOld',7,38,1,450,234,0,123)");
                Execute(db, "INSERT INTO character_dungeon_incount VALUES(1,'incount',7323,20261021,1,20261015,2)");
                Execute(db, "INSERT INTO account_dungeon_incount VALUES('incount',7184,20261015,3)");
                RewindToLocalIncountNumbering(db, local);
                Check(Query(db, "PRAGMA user_version") is long version && version == local && Query(db, MainlineV62To66Sql) is 0L && Query(db, SkinColumnSql) is 0L &&
                    Query(db, IncountTablesSql) is long tables && tables == (local == 63 ? 2 : 1),
                    "本地旧编号夹具确实缺少主线 v62–v67 结构，只保留旧次数表");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) && Query(db, MainlineV62To66Sql) is 5L && Query(db, IncountTablesSql) is 2L && Query(db, SkinColumnSql) is 1L &&
                    Query(db, PermanentStatSql) is 1L && Query(db, MainlineAdventureSql) is 9L,
                    $"本地旧编号 v{local} 库按表判定后补跑主线 v62–v73，并到当前版本");
                Check(Query(db, "SELECT character_id||':'||dungeon||':'||day||':'||daily_used||':'||week||':'||weekly_used FROM character_dungeon_incount") is "1:7323:20261021:1:20261015:2" &&
                    Query(db, "SELECT name||':'||revision||':'||gold FROM characters") is "LocalOld:7:123",
                    $"本地旧编号 v{local} 库升级保留原有副本次数行和角色");
                Check(local == 63
                    ? Query(db, "SELECT account||':'||dungeon||':'||period||':'||used FROM account_dungeon_incount") is "incount:7184:20261015:3"
                    : Query(db, "SELECT count(*) FROM account_dungeon_incount") is 0L,
                    $"本地旧编号 v{local} 库的账号次数表保留原行或空表新建");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, $"本地旧编号 v{local} 升级后重开不重复迁移");
        }

        foreach (int branch in new[] { 68, 69 })
        {
            string path = Path.Combine(root, "pre-permanent-v" + branch + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('incount',0,'PermOld',7,38,1,450,234,0,123)");
                Execute(db, "UPDATE characters SET weapon_skin_state='{\"Owned\":[5],\"Selected\":5}'");
                Execute(db, "INSERT INTO character_dungeon_incount VALUES(1,'incount',7323,20261021,1,20261015,2)");
                Execute(db, "INSERT INTO account_dungeon_incount VALUES('incount',7184,20261015,3)");
                Execute(db, "DROP TABLE character_permanent_stats");
                RemoveMainlineAdventureAdditions(db);
                if (branch == 68) Execute(db, "DROP TABLE account_dungeon_incount");
                RewindRaidLifecycle(db);
                Execute(db, $"PRAGMA user_version={branch}");
                Check(Query(db, PermanentStatSql) is 0L && Query(db, SkinColumnSql) is 1L && Query(db, MainlineV62To66Sql) is 5L &&
                    Query(db, IncountTablesSql) is long tables && tables == (branch == 69 ? 2 : 1),
                    "永久属性前旧编号夹具有主线 v62–v67 与次数表，缺主线 v68 永久属性表");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) && Query(db, PermanentStatSql) is 1L && Query(db, SkinColumnSql) is 1L &&
                    Query(db, MainlineV62To66Sql) is 5L && Query(db, IncountTablesSql) is 2L && Query(db, MainlineAdventureSql) is 9L &&
                    Query(db, "SELECT count(*) FROM character_permanent_stats") is 0L,
                    $"永久属性前旧编号 v{branch} 库按表判定后补跑主线 v68–v73，并到当前版本");
                Check(Query(db, "SELECT character_id||':'||dungeon||':'||day||':'||daily_used||':'||week||':'||weekly_used FROM character_dungeon_incount") is "1:7323:20261021:1:20261015:2" &&
                    Query(db, "SELECT name||':'||revision||':'||gold||':'||weapon_skin_state FROM characters") is "PermOld:7:123:{\"Owned\":[5],\"Selected\":5}" &&
                    (branch == 69
                        ? Query(db, "SELECT account||':'||dungeon||':'||period||':'||used FROM account_dungeon_incount") is "incount:7184:20261015:3"
                        : Query(db, "SELECT count(*) FROM account_dungeon_incount") is 0L),
                    $"永久属性前旧编号 v{branch} 库升级保留原有次数行、角色与幻化");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, $"永久属性前旧编号 v{branch} 升级后重开不重复迁移");
        }

        foreach (int branch in new[] { 69, 70 })
        {
            string path = Path.Combine(root, "pre-adventure-v" + branch + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('incount',0,'AdvOld',7,38,1,450,234,0,123)");
                Execute(db, "UPDATE characters SET weapon_skin_state='{\"Owned\":[5],\"Selected\":5}'");
                Execute(db, "INSERT INTO character_dungeon_incount VALUES(1,'incount',7323,20261021,1,20261015,2)");
                Execute(db, "INSERT INTO account_dungeon_incount VALUES('incount',7184,20261015,3)");
                RemoveMainlineAdventureAdditions(db);
                if (branch == 69) Execute(db, "DROP TABLE account_dungeon_incount");
                RewindRaidLifecycle(db);
                Execute(db, $"PRAGMA user_version={branch}");
                Check(Query(db, PermanentStatSql) is 1L && Query(db, SkinColumnSql) is 1L && Query(db, MainlineV62To66Sql) is 5L &&
                    Query(db, MainlineAdventureSql) is 0L && Query(db, IncountTablesSql) is long tables && tables == (branch == 70 ? 2 : 1),
                    "冒险团前旧编号夹具有主线 v62–v68 与次数表，缺主线 v69/v71–v73 冒险团结构");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) && Query(db, PermanentStatSql) is 1L && Query(db, SkinColumnSql) is 1L &&
                    Query(db, MainlineV62To66Sql) is 5L && Query(db, IncountTablesSql) is 2L && Query(db, MainlineAdventureSql) is 9L,
                    $"冒险团前旧编号 v{branch} 库按列判定后补跑主线 v69–v73，并到当前版本");
                Check(Query(db, "SELECT character_id||':'||dungeon||':'||day||':'||daily_used||':'||week||':'||weekly_used FROM character_dungeon_incount") is "1:7323:20261021:1:20261015:2" &&
                    Query(db, "SELECT name||':'||revision||':'||gold||':'||weapon_skin_state FROM characters") is "AdvOld:7:123:{\"Owned\":[5],\"Selected\":5}" &&
                    (branch == 70
                        ? Query(db, "SELECT account||':'||dungeon||':'||period||':'||used FROM account_dungeon_incount") is "incount:7184:20261015:3"
                        : Query(db, "SELECT count(*) FROM account_dungeon_incount") is 0L),
                    $"冒险团前旧编号 v{branch} 库升级保留原有次数行、角色与幻化");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, $"冒险团前旧编号 v{branch} 升级后重开不重复迁移");
        }

        foreach (int branch in new[] { 74, 75 })
        {
            string path = Path.Combine(root, "pre-proficiency-v" + branch + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('incount',0,'ProfOld',7,38,1,450,234,0,123)");
                Execute(db, "UPDATE characters SET weapon_skin_state='{\"Owned\":[5],\"Selected\":5}'");
                Execute(db, "INSERT INTO character_dungeon_incount VALUES(1,'incount',7323,20261021,1,20261015,2)");
                Execute(db, "INSERT INTO account_dungeon_incount VALUES('incount',7184,20261015,3)");
                Execute(db, "ALTER TABLE character_dungeon_settlement_receipts DROP COLUMN proficiency_gain; ALTER TABLE character_dungeon_growth_receipts DROP COLUMN proficiency_gain");
                if (branch == 74) Execute(db, "DROP TABLE account_dungeon_incount");
                RewindRaidLifecycle(db);
                Execute(db, $"PRAGMA user_version={branch}");
                Check(Query(db, ProficiencyGainSql) is 0L && Query(db, MainlineAdventureSql) is 9L &&
                    Query(db, IncountTablesSql) is long tables && tables == (branch == 75 ? 2 : 1),
                    "熟练度前旧编号夹具有主线 v62–v73 与次数表，缺主线 v74 熟练度到账列");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) && Query(db, ProficiencyGainSql) is 2L && Query(db, PermanentStatSql) is 1L && Query(db, SkinColumnSql) is 1L &&
                    Query(db, MainlineV62To66Sql) is 5L && Query(db, IncountTablesSql) is 2L && Query(db, MainlineAdventureSql) is 9L,
                    $"熟练度前旧编号 v{branch} 库按列判定后补跑主线 v74–v82，并到当前版本");
                Check(Query(db, "SELECT character_id||':'||dungeon||':'||day||':'||daily_used||':'||week||':'||weekly_used FROM character_dungeon_incount") is "1:7323:20261021:1:20261015:2" &&
                    Query(db, "SELECT name||':'||revision||':'||gold||':'||weapon_skin_state FROM characters") is "ProfOld:7:123:{\"Owned\":[5],\"Selected\":5}" &&
                    (branch == 75
                        ? Query(db, "SELECT account||':'||dungeon||':'||period||':'||used FROM account_dungeon_incount") is "incount:7184:20261015:3"
                        : Query(db, "SELECT count(*) FROM account_dungeon_incount") is 0L),
                    $"熟练度前旧编号 v{branch} 库升级保留原有次数行、角色与幻化");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, $"熟练度前旧编号 v{branch} 升级后重开不重复迁移");
        }

        string freshColumns = ColumnSignature(fresh);
        foreach (int branch in new[] { 83, 84 })
        {
            string path = Path.Combine(root, "pre-grief-v" + branch + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('incount',0,'GriefOld',7,38,1,450,234,0,123)");
                Execute(db, "UPDATE characters SET weapon_skin_state='{\"Owned\":[5],\"Selected\":5}'");
                Execute(db, "INSERT INTO character_dungeon_incount VALUES(1,'incount',7323,20261021,1,20261015,2)");
                Execute(db, "INSERT INTO account_dungeon_incount VALUES('incount',7184,20261015,3)");
                RemoveMainlineV83To87(db);
                if (branch == 83) Execute(db, "DROP TABLE account_dungeon_incount");
                RewindRaidLifecycle(db);
                Execute(db, $"PRAGMA user_version={branch}");
                Check(Query(db, MainlineV83To87Sql) is 0L && Query(db, ProficiencyGainSql) is 2L && Query(db, MainlineAdventureSql) is 9L &&
                    Query(db, IncountTablesSql) is long tables && tables == (branch == 84 ? 2 : 1),
                    "悲叹前旧编号夹具有主线 v62–v82 与次数表，缺主线 v83–v87 悲叹、塔与祭坛、装扮预设结构");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) && Query(db, MainlineV83To87Sql) is 11L && Query(db, ProficiencyGainSql) is 2L &&
                    Query(db, PermanentStatSql) is 1L && Query(db, SkinColumnSql) is 1L && Query(db, MainlineV62To66Sql) is 5L &&
                    Query(db, IncountTablesSql) is 2L && Query(db, MainlineAdventureSql) is 9L,
                    $"悲叹前旧编号 v{branch} 库按表判定后补跑主线 v83–v87，并到当前版本");
                Check(Query(db, "SELECT character_id||':'||dungeon||':'||day||':'||daily_used||':'||week||':'||weekly_used FROM character_dungeon_incount") is "1:7323:20261021:1:20261015:2" &&
                    Query(db, "SELECT name||':'||revision||':'||gold||':'||weapon_skin_state FROM characters") is "GriefOld:7:123:{\"Owned\":[5],\"Selected\":5}" &&
                    (branch == 84
                        ? Query(db, "SELECT account||':'||dungeon||':'||period||':'||used FROM account_dungeon_incount") is "incount:7184:20261015:3"
                        : Query(db, "SELECT count(*) FROM account_dungeon_incount") is 0L),
                    $"悲叹前旧编号 v{branch} 库升级保留原有次数行、角色与幻化");
            }
            Check(ColumnSignature(path) == freshColumns, $"悲叹前旧编号 v{branch} 库升级后表与列集合同当前新建库");
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, $"悲叹前旧编号 v{branch} 升级后重开不重复迁移");
        }

        string? sample = Environment.GetEnvironmentVariable("DFO_MIGRATION_SAMPLE_DB");
        if (string.IsNullOrWhiteSpace(sample)) return;
        string copy = Path.Combine(root, "sample.db");
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sample, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var target = Open(copy))
        {
            source.Open();
            source.BackupDatabase(target);
        }
        string counts(string file) => string.Join('|', new[] { "characters", "character_inventory_slots", "character_dungeon_incount", "account_dungeon_incount", "accounts", "guilds" }
            .Select(table => QueryOn(file, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='" + table + "'") is 1L
                ? table + "=" + QueryOn(file, "SELECT count(*) FROM " + table) : table + "=absent"));
        long sampleVersion = (long)QueryOn(copy, "PRAGMA user_version")!;
        string beforeCounts = counts(copy);
        _ = new CharacterRepository(copy);
        string afterCounts = counts(copy);
        Console.WriteLine($"[incount-numbering] sample v{sampleVersion} -> v{QueryOn(copy, "PRAGMA user_version")}; {beforeCounts} => {afterCounts}");
        Check(Equals(QueryOn(copy, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) && QueryOn(copy, MainlineV62To66Sql) is 5L && QueryOn(copy, IncountTablesSql) is 2L && QueryOn(copy, SkinColumnSql) is 1L &&
            QueryOn(copy, PermanentStatSql) is 1L && QueryOn(copy, MainlineAdventureSql) is 9L &&
            beforeCounts.Replace("account_dungeon_incount=absent", "account_dungeon_incount=0", StringComparison.Ordinal)
                .Replace("character_dungeon_incount=absent", "character_dungeon_incount=0", StringComparison.Ordinal) == afterCounts,
            "真实旧库副本升级到当前版本，主线结构齐全，角色、库存、账号、公会与次数行数不变");
    }

    // 主线 v69/v71/v72/v73 只追加列或表：旧编号夹具真的移除，确保升级走真实 ALTER/CREATE。v70 结算分项按表结构判定，夹具保留目标结构即跳过。
    static void RemoveMainlineAdventureAdditions(SqliteConnection db)
    {
        RemoveAdventureCapsuleWeek(db);
        RemoveAdventureActivity(db);
        RemoveAdventureWeekly(db);
        RemoveAdventureLedger(db);
    }

    // 主线 v83–v87：悲叹四表、塔与祭坛四表二索引、装扮预设列、对象key实例列；全有为 11（四表+四表+二索引+一列）。
    const string MainlineV83To87Sql = "SELECT (SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('account_grief_tower','character_grief_tower','character_grief_tower_entries','character_grief_tower_clears'," +
        "'dungeon_special_entry_receipts','dungeon_altar_clear_receipts','dungeon_despair_progress','dungeon_despair_clear_receipts'))" +
        "+(SELECT count(*) FROM sqlite_schema WHERE type='index' AND name IN('dungeon_special_entry_day','dungeon_altar_best'))" +
        "+(SELECT count(*) FROM pragma_table_info('characters') WHERE name='avatar_preset_state')";

    // 本分支 v83/v84 旧库没有主线 v83–v87 的任何结构：真的移除，确保补跑走真实建表、加列与对象key重建。
    static void RemoveMainlineV83To87(SqliteConnection db)
    {
        Execute(db, "DROP TABLE character_grief_tower_clears; DROP TABLE character_grief_tower_entries; DROP TABLE character_grief_tower; DROP TABLE account_grief_tower");
        Execute(db, "DROP TABLE dungeon_altar_clear_receipts; DROP TABLE dungeon_despair_clear_receipts; DROP TABLE dungeon_despair_progress; DROP TABLE dungeon_special_entry_receipts");
        Execute(db, "ALTER TABLE characters DROP COLUMN avatar_preset_state");
        RewindObjectKeys(db);
    }

    static string ColumnSignature(string path)
    {
        using var db = Open(path);
        return (string)Query(db, "SELECT group_concat(x,',') FROM (SELECT m.name||'.'||p.name AS x FROM sqlite_schema m JOIN pragma_table_info(m.name) p " +
            "WHERE m.type='table' AND m.name NOT LIKE 'sqlite_%' ORDER BY m.name,p.name)")!;
    }

    static void RewindToLocalIncountNumbering(SqliteConnection db, int local)
    {
        RemoveExperienceBonusSchema(db);
        RemoveMainlineAdventureAdditions(db);
        Execute(db, "ALTER TABLE characters DROP COLUMN blacklist_count; ALTER TABLE characters DROP COLUMN weapon_skin_state");
        Execute(db, "DROP TABLE character_consumable_states; DROP TABLE character_growup_change; DROP TABLE character_permanent_stats");
        const string table = "character_quest_completion_receipts";
        if (Query(db, "SELECT count(*) FROM " + table + " WHERE condition_type=60") is not 0L)
            throw new InvalidOperationException("旧版本夹具不能丢弃真实类型60领奖");
        string sql = (string)Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + table + "'")!;
        long schemaVersion = (long)Query(db, "PRAGMA schema_version")!;
        Execute(db, "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql='" +
            sql.Replace("'", "''", StringComparison.Ordinal).Replace("60,64,65,67,70", "64,65,67,70", StringComparison.Ordinal) +
            "' WHERE type='table' AND name='" + table + "'; PRAGMA schema_version=" + (schemaVersion + 1) +
            "; PRAGMA writable_schema=OFF; SELECT * FROM " + table + " LIMIT 0");
        if (local == 62) Execute(db, "DROP TABLE account_dungeon_incount");
        RewindRaidLifecycle(db);
        Execute(db, $"PRAGMA user_version={local}");
    }
}
