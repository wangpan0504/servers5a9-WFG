using System.Text.Json;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    // 可由隔离验证器分别传入两种冻结历史DDL；常规自测使用轻量形状夹具。
    internal static void BattlePassLegacy179Upgrade(string testRoot, string? historicalSchema = null,
        Action<SqliteConnection>? migrate = null, bool expectMissingColumn = false, string? historicalCoreSchema = null)
    {
        string root = Path.Combine(testRoot, expectMissingColumn ? "legacy-battle-pass-red" : "legacy-battle-pass-green");
        Directory.CreateDirectory(root);
        migrate ??= SqliteMigrations.Apply;
        (int Version, bool HasPrivilege, string Mode)[] cases =
        [
            (179, false, "normal"), (180, false, "normal"), (181, false, "normal"), (182, false, "normal"),
            (180, true, "normal"), (181, true, "normal"), (182, true, "normal"), (183, true, "normal"),
            (179, false, "late"), (183, true, "late"),
            (182, false, "unknown"), (179, false, "mixed"), (180, false, "cooldown"),
            (183, true, "unknown"), (180, true, "mixed"), (181, true, "cooldown"),
            (180, false, "quest"), (181, true, "quest"),
            (179, false, "no-bp"), (182, false, "future"),
            (181, true, "preview"), (182, true, "preview"), (183, true, "preview"), (184, true, "preview"),
            (181, true, "preview-unknown"), (181, true, "preview-mixed"), (184, true, "preview-official"),
            (184, true, "preview-cooldown"), (184, true, "preview-quest"), (184, true, "preview-late")
        ];
        string[] passTables =
        [
            "account_battle_pass_seasons", "account_battle_pass_claims", "battle_pass_season_rules",
            "account_battle_pass_mission_progress", "account_battle_pass_mission_claims", "account_battle_pass_source_events",
            "account_battle_pass_certificate_orders", "account_battle_pass_cera_consumption", "account_battle_pass_cera_orders",
            "account_battle_pass_deluxe_orders"
        ];
        string[] post180Tables =
        [
            "character_strongway2019_match_facts", "character_strongway2019_completions",
            "character_strongway2019_statistics", "account_strongway2019_rewards",
            "character_fatigue_acceleration", "character_pcroom_claims", "account_pcroom_claims"
        ];
        void DropPost180(SqliteConnection db)
        {
            foreach (string table in post180Tables)
            {
                Check(Query(db, "SELECT count(*) FROM " + table) is 0L,
                    "只移除隔离夹具中为空的后续正式表：" + table);
                Execute(db, "DROP TABLE " + table);
            }
        }
        try
        {
            foreach (var (version, hasPrivilege, mode) in cases)
            {
                if (expectMissingColumn && mode != "normal") continue;
                bool preview = mode.StartsWith("preview", StringComparison.Ordinal);
                string scenario = preview ? mode == "preview" ? "normal" : mode[8..] : mode;
                string path = Path.Combine(root, $"{version}-{(hasPrivilege ? "B" : "A")}-{mode}.db");
                int stage = version - (preview ? 2 : hasPrivilege ? 1 : 0);
                int count = stage switch { 179 => 2, 180 => 6, 181 => 7, _ => 9 };
                string? sourceSchema = preview ? null : hasPrivilege ? historicalCoreSchema : historicalSchema;
                if (sourceSchema is null) _ = new CharacterRepository(path);
                using (var db = Open(path))
                {
                    if (sourceSchema is { } sql)
                    {
                        // 历史完整DDL按当时四个迁移阶段的原始追加边界截取，不从当前空库改PRAGMA冒充。
                        int end = count == 9 ? sql.Length : sql.IndexOf("CREATE TABLE " + passTables[count] + "(", StringComparison.Ordinal);
                        Check(end > 0, "历史战令DDL包含精确的阶段追加边界");
                        Execute(db, sql[..end]);
                        Execute(db, "INSERT INTO schema_metadata(key,value) VALUES('baseline_id','s5a9-database-v1')");
                    }
                    else
                    {
                        DropPost180(db);
                        for (int index = passTables.Length - 1; index >= count; index--)
                            Execute(db, "DROP TABLE " + passTables[index]);
                        if (!hasPrivilege) Execute(db, "ALTER TABLE character_teleport_cooldowns DROP COLUMN privilege_until");
                        if (!preview) RewindQuestTargetTypes(db);
                    }
                    SeedLegacyBattlePass(db, stage, hasPrivilege);
                    Execute(db, $"PRAGMA user_version={version}");
                    Check(Query(db, "SELECT count(*) FROM pragma_table_info('character_teleport_cooldowns') WHERE name='privilege_until'") is long privilegeCount &&
                        privilegeCount == (hasPrivilege ? 1 : 0) &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND (name GLOB 'account_battle_pass_*' OR name GLOB 'battle_pass_*')") is long actualCount && actualCount == count,
                        $"旧战令{version}夹具包含对应礼遇冷却和当期{count}张战令表");
                    if (scenario == "unknown") Execute(db, "ALTER TABLE account_battle_pass_claims ADD COLUMN unexpected TEXT");
                    if (scenario == "mixed") Execute(db, BattlePassMissionSchema.Rules);
                    if (scenario == "official") Execute(db, Strongway2019RewardSchema.Definition);
                    if (scenario == "cooldown") Execute(db, "ALTER TABLE character_teleport_cooldowns ADD COLUMN unexpected INTEGER");
                    if (scenario == "quest") Execute(db, "ALTER TABLE character_quest_completion_receipts ADD COLUMN unexpected INTEGER");
                    if (scenario == "no-bp") Execute(db, "DROP TABLE account_battle_pass_claims; DROP TABLE account_battle_pass_seasons");
                    if (scenario == "future") Execute(db, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
                }
                string before = Snapshot(path);
                (string Table, string[] Columns, string Sql, string Rows)[] originals;
                using (var db = Open(path))
                    originals = TableNames(db).Select(table => (table, ColumnNames(db, table),
                        (string)Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + table + "'")!,
                        Rows(db, table, ColumnNames(db, table)))).ToArray();
                Exception? failure = null;
                try
                {
                    using var db = Open(path);
                    if (scenario == "late")
                    {
                        var steps = Enumerable.Range(2, 188).Select(number => new SqliteMigration(number, "定向事务回滚夹具", (connection, tx) =>
                        {
                            if (number == 186) BattlePassSchema.Upgrade(connection, tx);
                            if (number == 187) BattlePassMissionSchema.Upgrade(connection, tx);
                            if (number == 188) BattlePassCertificateSchema.Upgrade(connection, tx);
                            if (number == 189)
                            {
                                BattlePassCeraConsumptionSchema.Upgrade(connection, tx);
                                throw new InvalidOperationException("legacy-battle-pass-late-failure");
                            }
                        })).ToArray();
                        SqliteMigrations.Apply(db, steps);
                    }
                    else migrate(db);
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException or SqliteException) { failure = error; }
                if (expectMissingColumn)
                {
                    if (!hasPrivilege && version == 179)
                    {
                        Check(failure is null, "原9e兼容在旧179仍正常经过正式180");
                        Console.WriteLine("legacy-battle-pass control A179: migrated");
                        continue;
                    }
                    Check(failure is InvalidDataException { Message: "关键表的约束或外键不是已确认结构: character_quest_completion_receipts" },
                        $"旧战令{version}/{(hasPrivilege ? "B" : "A")}原实现在任务约束处失败：{failure?.Message}");
                    Check(Snapshot(path) == before, "旧实现失败时完整回滚");
                    Console.WriteLine($"legacy-battle-pass RED {(hasPrivilege ? "B" : "A")}{version}: target-schema, unchanged");
                    continue;
                }
                if (scenario != "normal")
                {
                    bool expected = scenario == "no-bp"
                        ? failure is InvalidDataException { Message: "传送冷却列结构不符：privilege_until" }
                        : scenario == "late" ? failure is InvalidOperationException { Message: "legacy-battle-pass-late-failure" }
                        : scenario == "future" ? failure is InvalidDataException versionError && versionError.Message.Contains("不在当前支持范围", StringComparison.Ordinal)
                        : failure is InvalidDataException shapeError && shapeError.Message.StartsWith("旧战令编号库", StringComparison.Ordinal);
                    Check(expected, $"旧战令{version}/{mode}在预期边界拒绝：{failure?.Message}");
                    Check(Snapshot(path) == before, $"旧战令{version}/{mode}失败完整回滚版本、DDL及所有原账号/角色/战令账本");
                    Console.WriteLine($"legacy-battle-pass GREEN v{version} {mode}: expected-rejection, unchanged");
                    continue;
                }
                Check(failure is null, $"旧战令{version}正常升级：{failure?.Message}");
                using (var db = Open(path))
                {
                    foreach (var original in originals)
                    {
                        Check(Rows(db, original.Table, original.Columns) == original.Rows, $"旧战令{version}完整保留{original.Table}所有既存字段行");
                        if ((preview || original.Table != "character_quest_completion_receipts") &&
                            (hasPrivilege || original.Table != "character_teleport_cooldowns"))
                            Check(Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + original.Table + "'") is string actual && actual == original.Sql,
                                $"旧战令{version}保留{original.Table}原始DDL");
                    }
                    Check(Query(db, "PRAGMA user_version") is long current && current == SqliteMigrations.CurrentVersion &&
                        Query(db, "SELECT count(*) FROM pragma_table_info('character_teleport_cooldowns') WHERE name='privilege_until' AND upper(type)='INTEGER' AND \"notnull\"=1 AND dflt_value='0'") is 1L &&
                        Query(db, "SELECT privilege_until FROM character_teleport_cooldowns WHERE character_id=1") is long privilege &&
                        privilege == (hasPrivilege ? 5000 : 0) &&
                        Query(db, "SELECT sql FROM sqlite_schema WHERE name='character_quest_completion_receipts'") is string questSql &&
                        SqliteMigrations.NormalizeTable(questSql, "character_quest_completion_receipts") ==
                        SqliteMigrations.NormalizeTable(SqliteMigrations.QuestCompletionSchemaV180, "character_quest_completion_receipts") &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND (name GLOB 'account_battle_pass_*' OR name GLOB 'battle_pass_*')") is 10L &&
                        Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                        $"旧战令{version}补齐正式任务约束、完成196且完整性正确");
                    Check(post180Tables.All(table => TableNames(db).Contains(table)), "正式181–185的七张表已补齐");
                    bool constrained = false;
                    try { Execute(db, "UPDATE character_teleport_cooldowns SET privilege_until=-1 WHERE character_id=1"); }
                    catch (SqliteException) { constrained = true; }
                    Check(constrained, "新增礼遇冷却保留非负约束");
                }
                string upgraded = Snapshot(path);
                using (var db = Open(path)) migrate(db);
                Check(Snapshot(path) == upgraded, $"旧战令{version}升级后再次打开幂等且账本无变化");
                Console.WriteLine($"legacy-battle-pass GREEN v{version} normal: preserved, reopened");
            }
            if (!expectMissingColumn)
            {
                foreach (int version in new[] { 186, 187, 188, 189 })
                {
                    string path = Path.Combine(root, $"canonical-{version}.db");
                    _ = new CharacterRepository(path);
                    int stage = version - 7;
                    int count = stage switch { 179 => 2, 180 => 6, 181 => 7, _ => 9 };
                    using (var db = Open(path))
                    {
                        for (int index = passTables.Length - 1; index >= count; index--)
                            Execute(db, "DROP TABLE " + passTables[index]);
                        SeedLegacyBattlePass(db, stage, hasPrivilege: true);
                        Execute(db, $"PRAGMA user_version={version}");
                    }
                    (string Table, string[] Columns, string Rows)[] originals;
                    using (var db = Open(path))
                        originals = TableNames(db).Select(table => (table, ColumnNames(db, table),
                            Rows(db, table, ColumnNames(db, table)))).ToArray();
                    using (var db = Open(path)) migrate(db);
                    using (var db = Open(path))
                    {
                        foreach (var original in originals)
                            Check(Rows(db, original.Table, original.Columns) == original.Rows,
                                $"新编号{version}保留{original.Table}完整事实");
                        Check(Query(db, "PRAGMA user_version") is long current && current == SqliteMigrations.CurrentVersion &&
                            Query(db, "SELECT privilege_until FROM character_teleport_cooldowns WHERE character_id=1") is 5000L &&
                            Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND (name GLOB 'account_battle_pass_*' OR name GLOB 'battle_pass_*')") is 10L &&
                            Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                            $"新编号{version}正常升级且不误当旧库");
                    }
                    string upgraded = Snapshot(path);
                    using (var db = Open(path)) migrate(db);
                    Check(Snapshot(path) == upgraded, $"新编号{version}重开幂等");
                }
                {
                    string path = Path.Combine(root, "official-185.db");
                    _ = new CharacterRepository(path);
                    using (var db = Open(path))
                    {
                        foreach (string table in passTables.Reverse())
                        {
                            Check(Query(db, "SELECT count(*) FROM " + table) is 0L,
                                "正式185夹具只移除空的未来战令表：" + table);
                            Execute(db, "DROP TABLE " + table);
                        }
                        Execute(db, """
                            INSERT INTO accounts(account_id,salt,password_hash,iterations) VALUES('official-pass',x'01',x'02',1);
                            INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                              VALUES(1,'official-pass',0,'OfficialPass',7,1,0,10,10,0,12345);
                            INSERT INTO account_wallets(account,revision,mileage) VALUES('official-pass',11,23);
                            INSERT INTO account_pcroom_claims VALUES('official-pass',20261101);
                            INSERT INTO character_pcroom_claims VALUES(1,'official-pass',20261101);
                            INSERT INTO character_fatigue_acceleration VALUES(1,'official-pass',1);
                            PRAGMA user_version=185;
                            """);
                    }
                    (string Table, string[] Columns, string Sql, string Rows)[] originals;
                    using (var db = Open(path))
                        originals = TableNames(db).Select(table => (table, ColumnNames(db, table),
                            (string)Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + table + "'")!,
                            Rows(db, table, ColumnNames(db, table)))).ToArray();
                    using (var db = Open(path)) migrate(db);
                    using (var db = Open(path))
                    {
                        foreach (var original in originals)
                            Check(Rows(db, original.Table, original.Columns) == original.Rows &&
                                Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + original.Table + "'") is string sql && sql == original.Sql,
                                "正式185保留原结构和事实：" + original.Table);
                        Check(Query(db, "PRAGMA user_version") is long current && current == SqliteMigrations.CurrentVersion &&
                            passTables.All(table => TableNames(db).Contains(table)) &&
                            Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                            "正式185沿新186–189迁移，不误判为旧战令编号库");
                    }
                    string upgraded = Snapshot(path);
                    using (var db = Open(path)) migrate(db);
                    Check(Snapshot(path) == upgraded, "正式185升级后重开幂等，原网吧领取及燃烧选择保持");
                }
            }
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }

    static void SeedLegacyBattlePass(SqliteConnection db, int version, bool hasPrivilege)
    {
        Execute(db, $"""
            INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
              VALUES(1,'legacy-pass',0,'LegacyPass',7,1,0,10,10,0,12345);
            INSERT INTO account_wallets(account,revision,mileage) VALUES('legacy-pass',11,23);
            INSERT INTO account_currency_balances(account,currency_kind,balance) VALUES('legacy-pass',{(int)CurrencyKind.Cera},9876);
            INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until)
              VALUES(1,'legacy-pass',1000,2000,3000,4000);
            INSERT INTO character_quest_completion_receipts(character_id,account,acceptance_id,quest_id,generation,rule_id,
              resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,
              experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,
              rewards,condition_type,plan_snapshot)
              VALUES(1,'legacy-pass','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',100,1,'fixture','fixture','fixture',0,0,0,'1',0,
              0,0,1,1,0,0,0,1,'[]',17,json_object());
            INSERT INTO account_battle_pass_seasons VALUES('legacy-pass','legacy-season',1500,1,4);
            INSERT INTO account_battle_pass_claims VALUES('legacy-pass','legacy-season',1,0,1),('legacy-pass','legacy-season',1,1,1);
            """);
        if (hasPrivilege) Execute(db, "UPDATE character_teleport_cooldowns SET privilege_until=5000 WHERE character_id=1");
        using (var item = db.CreateCommand())
        {
            item.CommandText = "INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(42,1,'legacy-pass',$core)";
            item.Parameters.AddWithValue("$core", JsonSerializer.Serialize(new ItemCore(490705101, ItemKind.Consumable, 3)));
            item.ExecuteNonQuery();
        }
        Execute(db, $"INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'legacy-pass',{(int)InventoryListType.Main},65,42)");
        if (version >= 180) Execute(db, """
            INSERT INTO battle_pass_season_rules VALUES('legacy-season',1000,2000,'{"legacy":true}');
            INSERT INTO account_battle_pass_mission_progress VALUES('legacy-pass','legacy-season',0,20261101,1,7);
            INSERT INTO account_battle_pass_mission_claims VALUES('legacy-pass','legacy-season',0,20261101,1,200,1500,2);
            INSERT INTO account_battle_pass_source_events VALUES('legacy-pass','dungeon','11111111111111111111111111111111','legacy-season',1500);
            """);
        if (version >= 181) Execute(db, """
            INSERT INTO account_battle_pass_certificate_orders VALUES('legacy-pass','legacy-season','22222222222222222222222222222222',105659,490705043,4900,1,2,3,'{"legacy":true}',1500);
            """);
        if (version >= 182) Execute(db, """
            INSERT INTO account_battle_pass_cera_consumption VALUES('legacy-pass','legacy-season',12345,67);
            INSERT INTO account_battle_pass_cera_orders VALUES('legacy-pass','legacy-season','33333333333333333333333333333333',1,345,345,3,1,22,67,3,4,'{"legacy":true}',1500);
            """);
    }
}
