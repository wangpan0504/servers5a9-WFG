using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void BattlePass175Upgrade(string testRoot)
    {
        string root = Path.Combine(testRoot, "battle-pass-175");
        Directory.CreateDirectory(root);
        try
        {
            foreach (int sourceVersion in new[] { 175, 177, 178, 179, 180 })
            foreach (bool incompatible in new[] { false, true })
            {
                string path = Path.Combine(root, $"{sourceVersion}-{(incompatible ? "late-failure" : "normal")}.db");
                string originalCore = JsonSerializer.Serialize(new ItemCore(490705101, ItemKind.Consumable, 3));
                _ = new CharacterRepository(path);
                using (var db = Open(path))
                {
                DropEmptyWeeklyAttendanceTables(db);
                    Check(Query(db, "SELECT (SELECT count(*) FROM account_battle_pass_seasons)+(SELECT count(*) FROM account_battle_pass_claims)+(SELECT count(*) FROM battle_pass_season_rules)+(SELECT count(*) FROM account_battle_pass_mission_progress)+(SELECT count(*) FROM account_battle_pass_mission_claims)+(SELECT count(*) FROM account_battle_pass_source_events)+(SELECT count(*) FROM account_battle_pass_certificate_orders)+(SELECT count(*) FROM account_battle_pass_cera_consumption)+(SELECT count(*) FROM account_battle_pass_cera_orders)+(SELECT count(*) FROM account_battle_pass_deluxe_orders)") is 0L,
                        "只从十张战令表均为空的隔离库构造旧版本夹具");
                    Execute(db, """
                        DROP TABLE account_pcroom_timepoint_claims;
                        DROP TABLE account_pcroom_timepoint_segments;
                        DROP TABLE account_pcroom_timepoint_days;
                        DROP TABLE account_battle_pass_deluxe_orders;
                        DROP TABLE account_battle_pass_cera_orders;
                        DROP TABLE account_battle_pass_cera_consumption;
                        DROP TABLE account_battle_pass_certificate_orders;
                        DROP TABLE account_battle_pass_source_events;
                        DROP TABLE account_battle_pass_mission_claims;
                        DROP TABLE account_battle_pass_mission_progress;
                        DROP TABLE battle_pass_season_rules;
                        DROP TABLE account_battle_pass_claims;
                        DROP TABLE account_battle_pass_seasons;
                        """);
                    Execute(db, $"""
                        INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                          VALUES(1,'pass-migrate',0,'PassMigrate',7,1,0,10,10,0,12345);
                        INSERT INTO account_wallets(account,revision,mileage) VALUES('pass-migrate',11,23);
                        INSERT INTO account_currency_balances(account,currency_kind,balance) VALUES('pass-migrate',{(int)CurrencyKind.Cera},9876);
                        """);
                    string secondAccount = sourceVersion == 175 ? "pass-other" : "pass-migrate";
                    Execute(db, $"""
                        INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                          VALUES(2,'{secondAccount}',1,'PassSpouse',8,1,0,10,10,0,23456);
                        """);
                    var marriage = MarriageDirectRules.Create(Guid.NewGuid(), 31, 1, 2, true, false, 1000);
                    using (var relation = db.CreateCommand())
                    {
                        relation.CommandText = """
                            INSERT INTO marriage_records(marriage_id,context_id,revision,first_id,second_id,first_account,second_account,state,created_at,ended_at,aggregate_json)
                            VALUES($id,$context,$revision,1,2,'pass-migrate',$second,$state,$created,$ended,$json)
                            """;
                        relation.Parameters.AddWithValue("$id", marriage.Id.ToString("N"));
                        relation.Parameters.AddWithValue("$context", marriage.ContextId);
                        relation.Parameters.AddWithValue("$revision", marriage.Revision);
                        relation.Parameters.AddWithValue("$state", marriage.State);
                        relation.Parameters.AddWithValue("$created", marriage.CreatedAt);
                        relation.Parameters.AddWithValue("$ended", marriage.EndedAt);
                        relation.Parameters.AddWithValue("$second", secondAccount);
                        relation.Parameters.AddWithValue("$json", JsonSerializer.Serialize(marriage));
                        relation.ExecuteNonQuery();
                    }
                    Execute(db, $"INSERT INTO marriage_active_members VALUES(1,'{marriage.Id:N}'),(2,'{marriage.Id:N}')");
                    if (sourceVersion == 175)
                    {
                        using var write = db.BeginWrite("DatabaseSchemaTests.PreviousBattlePassMarriage");
                        SqliteMigrations.ReplaceTableConstraint(db, write.Transaction, "marriage_records", "CHECK(length(second_account)>0)",
                            "CHECK(length(second_account)>0 AND second_account<>first_account)");
                        write.Transaction.Commit();
                        Execute(db, "DROP TABLE character_privilege_service");
                    }
                    else
                        Execute(db, "INSERT INTO character_privilege_service VALUES(1,1000,2000,4,5,730000,8,729999,3)");
                    if (sourceVersion < 178)
                        Execute(db, "DROP TABLE character_fiendwar_counter_stream");
                    else
                        Execute(db, "INSERT INTO character_fiendwar_counter_stream VALUES(1,'pass-migrate','fixture-178',17)");
                    Execute(db, """
                        INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until)
                          VALUES(1,'pass-migrate',1000,2000,3000,4000);
                        """);
                    if (sourceVersion < 179)
                        Execute(db, "ALTER TABLE character_teleport_cooldowns DROP COLUMN privilege_until");
                    else
                        Execute(db, "UPDATE character_teleport_cooldowns SET privilege_until=5000");
                    if (sourceVersion < 180) RewindQuestTargetTypes(db);
                    Execute(db, $"PRAGMA user_version={sourceVersion}");
                    Check(Query(db, "SELECT sql FROM sqlite_schema WHERE name='marriage_records'") is string marriageSql &&
                        SqliteMigrations.NormalizeTable(marriageSql, "marriage_records") ==
                        SqliteMigrations.NormalizeTable(sourceVersion == 175 ? MarriageSchema.RecordsSql : MarriageSameAccountSchema.Definition, "marriage_records") &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_privilege_service'") is long privilegeCount &&
                        privilegeCount == (sourceVersion == 175 ? 0 : 1) &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_fiendwar_counter_stream'") is long counterTables &&
                        counterTables == (sourceVersion >= 178 ? 1 : 0) &&
                        Query(db, "SELECT count(*) FROM pragma_table_info('character_teleport_cooldowns') WHERE name='privilege_until'") is long teleportColumns &&
                        teleportColumns == (sourceVersion >= 179 ? 1 : 0),
                        $"{sourceVersion}夹具包含对应婚姻约束、礼遇表、漩涡计数表及礼遇冷却列状态");
                    using var insert = db.CreateCommand();
                    insert.CommandText = "INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(42,1,'pass-migrate',$core)";
                    insert.Parameters.AddWithValue("$core", originalCore);
                    insert.ExecuteNonQuery();
                    Execute(db, $"INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'pass-migrate',{(int)InventoryListType.Main},65,42)");
                    Check(Query(db, "PRAGMA user_version") is long fixtureVersion && fixtureVersion == sourceVersion &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND (name LIKE 'account_battle_pass_%' OR name='battle_pass_season_rules')") is 0L,
                        $"{sourceVersion}夹具已移除全部后续战令表，而非仅修改版本号");
                    if (incompatible)
                        Execute(db, "CREATE TABLE account_battle_pass_cera_orders(unexpected TEXT NOT NULL)");
                }

                string before = Snapshot(path);
                string assets = AssetSnapshot(path);
                string upstreamRows = UpstreamRows(path);
                string upstreamState = UpstreamState(path);
                string counterState = CounterState(path);
                if (incompatible)
                {
                    bool rejected = false;
                    try { _ = new CharacterRepository(path); }
                    catch (InvalidDataException error) when (error.Message == (sourceVersion is 179 or 180
                        ? "旧战令编号库冷却或版本不兼容" : "战令点券消费账结构不兼容")) { rejected = true; }
                    Check(rejected, sourceVersion is 179 or 180
                        ? "179/180的畸形战令表不匹配已知旧编号，在识别阶段拒绝"
                        : $"{sourceVersion}升189在前序迁移后检测到不兼容的点券订单表");
                    Check(Snapshot(path) == before && AssetSnapshot(path) == assets && UpstreamState(path) == upstreamState && CounterState(path) == counterState,
                        $"{sourceVersion}升189失败回滚全部结构、版本、婚姻礼遇、漩涡水位、传送冷却和原角色资产");
                    using var db = Open(path);
                    Check(Query(db, "PRAGMA user_version") is long rollbackVersion && rollbackVersion == sourceVersion &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND (name LIKE 'account_battle_pass_%' OR name='battle_pass_season_rules')") is 1L &&
                        Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                        $"回滚恢复{sourceVersion}且只保留原先注入的不兼容战令表");
                    continue;
                }

                var repository = new CharacterRepository(path);
                _ = new CharacterRepository(path);
                using (var db = Open(path))
                    Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('account_battle_pass_seasons','account_battle_pass_claims','battle_pass_season_rules','account_battle_pass_mission_progress','account_battle_pass_mission_claims','account_battle_pass_source_events','account_battle_pass_certificate_orders','account_battle_pass_cera_consumption','account_battle_pass_cera_orders','account_battle_pass_deluxe_orders')") is 10L &&
                        Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L && AssetSnapshot(path) == assets &&
                        UpstreamRows(path) == upstreamRows &&
                        (sourceVersion == 175 || UpstreamState(path) == upstreamState) &&
                        (sourceVersion < 178 || CounterState(path) == counterState) &&
                        Query(db, "SELECT sql FROM sqlite_schema WHERE name='character_fiendwar_counter_stream'") is string counterSql &&
                        SqliteMigrations.NormalizeTable(counterSql, "character_fiendwar_counter_stream") == SqliteMigrations.NormalizeTable(FiendwarCounterSchema.Definition, "character_fiendwar_counter_stream") &&
                        Query(db, "SELECT count(*) FROM character_fiendwar_counter_stream") is long counterRows && counterRows == (sourceVersion >= 178 ? 1 : 0) &&
                        Query(db, "SELECT sql FROM sqlite_schema WHERE name='marriage_records'") is string marriageSql &&
                        SqliteMigrations.NormalizeTable(marriageSql, "marriage_records") == SqliteMigrations.NormalizeTable(MarriageSameAccountSchema.Definition, "marriage_records") &&
                        Query(db, "SELECT sql FROM sqlite_schema WHERE name='character_privilege_service'") is string privilegeSql &&
                        SqliteMigrations.NormalizeTable(privilegeSql, "character_privilege_service") == SqliteMigrations.NormalizeTable(PrivilegeServiceSchema.Definition, "character_privilege_service") &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name IN('marriage_active_relation','marriage_first_history','marriage_second_history')") is 3L &&
                        Query(db, "SELECT count(*) FROM character_privilege_service") is long privilegeRows && privilegeRows == (sourceVersion == 175 ? 0 : 1) &&
                        Query(db, "SELECT potion_until||':'||aura_until||':'||upgrade_aura_until||':'||party_aura_until||':'||privilege_until FROM character_teleport_cooldowns WHERE character_id=1") is string cooldowns &&
                        cooldowns == $"1000:2000:3000:4000:{(sourceVersion >= 179 ? 5000 : 0)}",
                        $"{sourceVersion}升级当前版本创建十张战令表，保留婚姻礼遇、漩涡水位、传送冷却和资产并可重复打开");
                var owners = new AccountOwners();
                using var owner = owners.Acquire("pass-migrate", () => { });
                var state = repository.ReadBattlePass(owner, "migration-season");
                var restored = new CharacterRepository(path).ReadBattlePass(owner, "migration-season");
                Check(state.TotalExperience == 0 && state.PassType == 0 && state.Revision == 0 && state.Claims.IsEmpty &&
                    restored.SeasonKey == state.SeasonKey && restored.TotalExperience == state.TotalExperience &&
                    restored.PassType == state.PassType && restored.Revision == state.Revision && restored.Claims.SetEquals(state.Claims) &&
                    AssetSnapshot(path) == assets && UpstreamRows(path) == upstreamRows &&
                    (sourceVersion == 175 || UpstreamState(path) == upstreamState) &&
                    (sourceVersion < 178 || CounterState(path) == counterState),
                    "新赛季可重开恢复且不修改已有资产");
            }
            VerifyDeluxeCurrentReopen(root);
            VerifyDeluxeMergeVersionUpgrade(root);
            VerifyDeluxe191PreviewUpgrade(root);
            VerifyDeluxe192PreviewUpgrade(root);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }

        static string UpstreamRows(string path)
        {
            using var db = Open(path);
            return string.Join("|",
                Query(db, "SELECT group_concat(row,'|') FROM (SELECT marriage_id||':'||context_id||':'||revision||':'||first_id||':'||second_id||':'||first_account||':'||second_account||':'||state||':'||created_at||':'||ended_at||':'||aggregate_json AS row FROM marriage_records ORDER BY marriage_id)"),
                Query(db, "SELECT group_concat(row,'|') FROM (SELECT character_id||':'||marriage_id AS row FROM marriage_active_members ORDER BY character_id)"));
        }

        static string UpstreamState(string path)
        {
            using var db = Open(path);
            bool hasPrivilege = Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_privilege_service'") is 1L;
            return string.Join("|", UpstreamRows(path),
                Query(db, "SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_schema WHERE name IN('marriage_records','marriage_active_members','marriage_active_relation','marriage_first_history','marriage_second_history','character_privilege_service') ORDER BY name)"),
                hasPrivilege ? Query(db, "SELECT group_concat(row,'|') FROM (SELECT character_id||':'||started_at||':'||expires_at||':'||purchase_season||':'||reward_season||':'||last_login_day||':'||login_days||':'||daily_claim_day||':'||claimed_milestones AS row FROM character_privilege_service ORDER BY character_id)") : null);
        }

        static string CounterState(string path)
        {
            using var db = Open(path);
            bool exists = Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_fiendwar_counter_stream'") is 1L;
            return string.Join("|", exists,
                exists ? Query(db, "SELECT sql FROM sqlite_schema WHERE name='character_fiendwar_counter_stream'") : null,
                exists ? Query(db, "SELECT group_concat(row,'|') FROM (SELECT character_id||':'||account||':'||stream||':'||sequence AS row FROM character_fiendwar_counter_stream ORDER BY character_id)") : null);
        }

        static string AssetSnapshot(string path)
        {
            using var db = Open(path);
            return string.Join("|", Query(db, "SELECT group_concat(row,'|') FROM (SELECT id||':'||account||':'||revision||':'||gold||':'||inventory_items_revision AS row FROM characters WHERE id IN(1,2) ORDER BY id)"),
                Query(db, "SELECT account||':'||revision||':'||mileage FROM account_wallets WHERE account='pass-migrate'"),
                Query(db, $"SELECT balance FROM account_currency_balances WHERE account='pass-migrate' AND currency_kind={(int)CurrencyKind.Cera}"),
                Query(db, "SELECT i.item_uid||':'||i.item_core||':'||s.list_type||':'||s.slot_index FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid WHERE i.item_uid=42"));
        }
    }

    static void VerifyDeluxe192PreviewUpgrade(string root)
    {
        foreach (string shape in new[] { "normal", "upstream", "unknown-deluxe", "partial-column", "partial-table", "partial-index" })
        {
            string path = Path.Combine(root, "deluxe-preview-192-" + shape + ".db");
            _ = new CharacterRepository(path);
            Dictionary<string, (string[] Columns, string Rows)> original;
            using (var db = Open(path))
            {
                DropEmptyWeeklyAttendanceTables(db);
                Execute(db, """
                    INSERT INTO accounts(account_id,salt,password_hash,iterations) VALUES('deluxe-preview',x'01',x'02',1);
                    INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                      VALUES(1,'deluxe-preview',0,'DeluxePreview',7,38,1,450,234,0,123);
                    INSERT INTO account_battle_pass_seasons VALUES('deluxe-preview','deluxe-season',20000,1,2);
                    INSERT INTO account_battle_pass_deluxe_orders VALUES('deluxe-preview','deluxe-season',
                      '44444444444444444444444444444444',105658,490705044,15600,1,0,2,0,7800,20000,'{}',1500);
                    INSERT INTO account_pcroom_timepoint_days VALUES('deluxe-preview',20261103,4000,1);
                    INSERT INTO account_pcroom_timepoint_segments VALUES('deluxe-preview','33333333333333333333333333333333',20261103,4000);
                    INSERT INTO account_pcroom_timepoint_claims VALUES('deluxe-preview',0,20261103,0,1,731,3037,2,1500);
                    INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,
                      phase1_event,phase1_anchor,phase1_receipt,clear_week)
                      VALUES(1,'deluxe-preview','11111111111111111111111111111111',1,'deluxe-preview',1,2,
                        '22222222222222222222222222222222',2,'{"Eligible":true,"Week":20261103}',20261103);
                    INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type)
                      VALUES(1,'deluxe-preview','11111111111111111111111111111111',1,1,0,20261103,1,'{"original":"reward"}','[731,732]',2,1);
                    INSERT INTO account_fiendwar_guides VALUES('deluxe-preview',0,20261103,0,1);
                    """);
                if (shape == "upstream")
                {
                    Execute(db, """
                        DROP TABLE account_battle_pass_deluxe_orders;
                        UPDATE character_raid_runs SET entry_epoch=7,reward_epoch=9;
                        UPDATE character_raid_reward_receipts SET reward_epoch=9;
                        UPDATE account_fiendwar_guides SET entry_epoch=7;
                        INSERT INTO account_raid_quota_epochs VALUES('deluxe-preview',1,7,9);
                        """);
                }
                else
                {
                    RewindRaidQuotaTo191(db);
                    foreach (var (table, expected) in new[]
                    {
                        ("character_raid_runs", RaidGuildContributionSchema.Definition),
                        ("character_raid_reward_receipts", RaidLifecycleSchema.FiendwarRewardDefinition.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal)),
                        ("account_fiendwar_guides", RaidLifecycleSchema.FiendwarGuideBindings)
                    })
                        Check(Query(db, "SELECT sql FROM sqlite_schema WHERE name='" + table + "'") is string sql &&
                            SqliteMigrations.NormalizeTable(sql, table) == SqliteMigrations.NormalizeTable(expected, table),
                            "旧豪华192夹具精确恢复双世代迁移前DDL：" + table);
                    Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN('account_raid_quota_epochs','character_raid_entry_clear','character_raid_reward_epoch')") is 0L &&
                        Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L, "旧豪华192无未来额度表及索引且原有外键完整");
                    if (shape == "unknown-deluxe") Execute(db, "ALTER TABLE account_battle_pass_deluxe_orders ADD COLUMN unexpected TEXT");
                    if (shape == "partial-column") Execute(db, "ALTER TABLE character_raid_runs ADD COLUMN entry_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(entry_epoch)='integer' AND entry_epoch>=0)");
                    if (shape == "partial-table") Execute(db, RaidQuotaSchema.EpochTable);
                    if (shape == "partial-index") Execute(db, "CREATE INDEX character_raid_entry_clear ON character_raid_runs(account)");
                }
                Execute(db, "PRAGMA user_version=192");
                original = TableNames(db).ToDictionary(table => table, table =>
                {
                    string[] columns = ColumnNames(db, table);
                    return (columns, Rows(db, table, columns));
                });
            }
            if (shape is not ("normal" or "upstream"))
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "未知豪华DDL或部分团本世代结构不能冒认旧192，失败保留全部结构、版本与数据：" + shape);
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    original.All(row => Rows(db, row.Key, row.Value.Columns) == row.Value.Rows),
                    "两类192升级当前版本保留完整订单、在线资格、团本历史和全部原字段与资产");
                if (shape == "normal")
                    Check(Query(db, "SELECT entry_epoch+reward_epoch FROM character_raid_runs") is 0L &&
                        Query(db, "SELECT reward_epoch FROM character_raid_reward_receipts") is 0L &&
                        Query(db, "SELECT entry_epoch FROM account_fiendwar_guides") is 0L &&
                        Query(db, "SELECT count(*) FROM account_raid_quota_epochs") is 0L,
                        "旧豪华192的团本历史归零世代，不补造账号额度或奖励");
                else
                    Check(Query(db, "SELECT count(*) FROM account_battle_pass_deluxe_orders") is 0L &&
                        Query(db, "SELECT entry_epoch||':'||reward_epoch FROM account_raid_quota_epochs") is "7:9",
                        "正式上游192只追加空豪华表，保留非零世代与原有在线奖励事实");
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "两类192升级后完整性和外键保持正确");
            }
            string after = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == after, "两类192升级后重开幂等");
        }
    }

    internal static void RewindRaidQuotaTo191(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        Check(Query(db, "SELECT (SELECT count(*) FROM character_raid_runs WHERE entry_epoch<>0 OR reward_epoch<>0)+(SELECT count(*) FROM character_raid_reward_receipts WHERE reward_epoch<>0)+(SELECT count(*) FROM account_fiendwar_guides WHERE entry_epoch<>0)+(SELECT count(*) FROM account_raid_quota_epochs)") is 0L,
            "旧191/豪华192夹具不丢弃已推进的团本世代与额度事实");
        Execute(db, """
            DROP INDEX character_raid_entry_clear;
            DROP INDEX character_raid_reward_epoch;
            ALTER TABLE character_raid_runs DROP COLUMN entry_epoch;
            ALTER TABLE character_raid_runs DROP COLUMN reward_epoch;
            ALTER TABLE character_raid_reward_receipts DROP COLUMN reward_epoch;
            ALTER TABLE account_fiendwar_guides RENAME TO deluxe_new_guides;
            """ + RaidLifecycleSchema.FiendwarGuideBindings + """
            INSERT INTO account_fiendwar_guides(account,slot,week,character_id)
              SELECT account,slot,week,character_id FROM deluxe_new_guides;
            DROP TABLE deluxe_new_guides;
            DROP TABLE account_raid_quota_epochs;
            """);
    }

    static void VerifyDeluxe191PreviewUpgrade(string root)
    {
        foreach (string shape in new[] { "normal", "partial-online", "unknown-deluxe" })
        {
            string path = Path.Combine(root, "deluxe-preview-191-" + shape + ".db");
            _ = new CharacterRepository(path);
            string orders, seasons;
            using (var db = Open(path))
            {
                DropEmptyWeeklyAttendanceTables(db);
                RewindRaidQuotaTo191(db);
                Execute(db, """
                    INSERT INTO account_battle_pass_seasons VALUES('deluxe-preview','deluxe-season',20000,1,2);
                    INSERT INTO account_battle_pass_deluxe_orders VALUES('deluxe-preview','deluxe-season',
                      '44444444444444444444444444444444',105658,490705044,15600,1,0,2,0,7800,20000,'{}',1500);
                    DROP TABLE account_pcroom_timepoint_claims;
                    DROP TABLE account_pcroom_timepoint_segments;
                    PRAGMA user_version=191;
                    """);
                if (shape != "partial-online") Execute(db, "DROP TABLE account_pcroom_timepoint_days");
                if (shape == "unknown-deluxe") Execute(db, "ALTER TABLE account_battle_pass_deluxe_orders ADD COLUMN unexpected TEXT");
                orders = Rows(db, "account_battle_pass_deluxe_orders", ColumnNames(db, "account_battle_pass_deluxe_orders"));
                seasons = Rows(db, "account_battle_pass_seasons", ColumnNames(db, "account_battle_pass_seasons"));
            }
            if (shape != "normal")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "未知豪华表或部分在线表不冒认191预览库，失败保留原版本、结构和非空订单");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT (SELECT count(*) FROM account_pcroom_timepoint_days)+(SELECT count(*) FROM account_pcroom_timepoint_segments)+(SELECT count(*) FROM account_pcroom_timepoint_claims)") is 0L &&
                    Rows(db, "account_battle_pass_deluxe_orders", ColumnNames(db, "account_battle_pass_deluxe_orders")) == orders &&
                    Rows(db, "account_battle_pass_seasons", ColumnNames(db, "account_battle_pass_seasons")) == seasons &&
                    Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "旧豪华191预览库补齐在线三张空表，保留非空豪华订单和账号资格并升级当前版本");
            string after = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == after, "旧豪华191预览库升级后重开幂等");
        }
    }

    static void VerifyDeluxeCurrentReopen(string root)
    {
        foreach (string shape in new[] { "normal", "missing", "unknown" })
        {
            string path = Path.Combine(root, "deluxe-193-" + shape + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_battle_pass_deluxe_orders'") is string sql &&
                    SqliteMigrations.NormalizeTable(sql, "account_battle_pass_deluxe_orders") ==
                    SqliteMigrations.NormalizeTable(BattlePassDeluxeSchema.Definition, "account_battle_pass_deluxe_orders"),
                    "新库豪华订单DDL与197迁移的完整约束相同");
                Execute(db, """
                    INSERT INTO account_battle_pass_seasons VALUES('deluxe-schema','deluxe-season',20000,1,2);
                    INSERT INTO account_battle_pass_deluxe_orders VALUES('deluxe-schema','deluxe-season',
                      '44444444444444444444444444444444',105658,490705044,15600,1,0,2,0,7800,20000,'{}',1500);
                    """);
                if (shape == "missing") Execute(db, "DROP TABLE account_battle_pass_deluxe_orders");
                if (shape == "unknown") Execute(db, "ALTER TABLE account_battle_pass_deluxe_orders ADD COLUMN unexpected TEXT");
            }
            string before = Snapshot(path);
            if (shape == "normal")
            {
                _ = new CharacterRepository(path);
                Check(Snapshot(path) == before, "当前非空豪华账本重开幂等且不修改既有数据");
            }
            else
            {
                Reject<InvalidDataException>(() => new CharacterRepository(path), "已标当前版本仍拒绝缺失或未知豪华表");
                Check(Snapshot(path) == before, "当前结构拒绝不修改原库版本、DDL或豪华账本");
            }
        }
    }

    internal static void DropEmptyWeeklyAttendanceTables(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        foreach (string table in new[] { "account_weekly_attendance", "weekly_attendance_season" })
        {
            Check(Query(db, "SELECT count(*) FROM " + table) is 0L,
                "旧版本夹具只移除尚未发布且为空的周签到表：" + table);
            Execute(db, "DROP TABLE " + table);
        }
    }

    static void VerifyDeluxeMergeVersionUpgrade(string root)
    {
        foreach (int sourceVersion in new[] { 193, 194, 195, 196 })
        foreach (string shape in sourceVersion == 193
            ? new[] { "normal", "unknown-deluxe", "partial-guild", "partial-week", "partial-weekly" }
            : sourceVersion == 194 ? new[] { "normal", "unknown-deluxe", "old-guild" } : new[] { "normal" })
        {
            string path = Path.Combine(root, $"deluxe-merge-{sourceVersion}-{shape}.db");
            _ = new CharacterRepository(path);
            Dictionary<string, (string[] Columns, string Rows)> original;
            string? deluxeSql = null;
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is 197L,
                    "本夹具明确覆盖原私有193/194及正式195/196升级豪华197，避免混用已占用迁移号");
                Execute(db, """
                    INSERT INTO accounts(account_id,salt,password_hash,iterations)
                      VALUES('deluxe-merge',x'01',x'02',1);
                    INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                      VALUES(1,'deluxe-merge',0,'DeluxeMerge',7,38,1,450,234,0,123);
                    INSERT INTO account_wallets(account,revision,mileage) VALUES('deluxe-merge',11,23);
                    INSERT INTO account_battle_pass_seasons VALUES('deluxe-merge','deluxe-season',20000,1,2);
                    INSERT INTO account_raid_quota_epochs VALUES('deluxe-merge',0,7,9);
                    INSERT INTO account_pcroom_timepoint_days VALUES('deluxe-merge',20261104,4000,1);
                    """);
                Execute(db, $"INSERT INTO account_currency_balances(account,currency_kind,balance) VALUES('deluxe-merge',{(int)CurrencyKind.Cera},9876)");
                if (sourceVersion == 193)
                {
                    Execute(db, """
                        INSERT INTO account_battle_pass_deluxe_orders VALUES('deluxe-merge','deluxe-season',
                          '44444444444444444444444444444444',105658,490705044,15600,1,0,2,0,7800,20000,'{}',1500);
                        INSERT INTO guild_request_claims VALUES(
                          'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1,'deluxe-merge',1);
                        """);
                    using (var tx = db.BeginTransaction())
                    {
                        SqliteMigrations.ReplaceTableConstraint(db, tx, "character_guild_npc_item_crafts",
                            "CHECK(npc_id IN(400001065,400001061))", "CHECK(npc_id=400001065)");
                        SqliteMigrations.ReplaceTableConstraint(db, tx, "character_guild_npc_item_crafts",
                            "CHECK(benefit_id>0)", "CHECK(benefit_id BETWEEN 7001 AND 7005)");
                        SqliteMigrations.ReplaceTableConstraint(db, tx, "guild_request_claims",
                            "CHECK(kind BETWEEN 1 AND 7)", "CHECK(kind BETWEEN 1 AND 6)");
                        tx.Commit();
                    }
                    Execute(db, """
                        DROP TABLE character_guild_npc_craft_weeks;
                        DROP TABLE guild_dungeon_scroll_receipts;
                        DROP TABLE account_endless_adventure_days;
                        DROP TABLE account_daily_attendance_days;
                        DROP TABLE account_daily_attendance_claims;
                        DROP TABLE account_weekly_attendance;
                        DROP TABLE weekly_attendance_season;
                        PRAGMA user_version=193;
                        """);
                    deluxeSql = (string)Query(db, "SELECT sql FROM sqlite_schema WHERE name='account_battle_pass_deluxe_orders'")!;
                    if (shape == "unknown-deluxe") Execute(db, "ALTER TABLE account_battle_pass_deluxe_orders ADD COLUMN unexpected TEXT");
                    if (shape == "partial-guild")
                    {
                        using var tx = db.BeginTransaction();
                        SqliteMigrations.ReplaceTableConstraint(db, tx, "character_guild_npc_item_crafts",
                            "CHECK(npc_id=400001065)", "CHECK(npc_id IN(400001065,400001061))");
                        tx.Commit();
                    }
                    if (shape == "partial-week") Execute(db, GuildNpcItemCraftWeekSchema.Sql);
                    if (shape == "partial-weekly") Execute(db, WeeklyAttendanceSchema.Definition.Split(';')[0]);
                }
                else if (sourceVersion == 194)
                {
                    Execute(db, """
                        INSERT INTO account_battle_pass_deluxe_orders VALUES('deluxe-merge','deluxe-season',
                          '44444444444444444444444444444444',105658,490705044,15600,1,0,2,0,7800,20000,'{}',1500);
                        DROP TABLE account_endless_adventure_days;
                        DROP TABLE account_daily_attendance_days;
                        DROP TABLE account_daily_attendance_claims;
                        DROP TABLE account_weekly_attendance;
                        DROP TABLE weekly_attendance_season;
                        PRAGMA user_version=194;
                        """);
                    deluxeSql = (string)Query(db, "SELECT sql FROM sqlite_schema WHERE name='account_battle_pass_deluxe_orders'")!;
                    if (shape == "unknown-deluxe") Execute(db, "ALTER TABLE account_battle_pass_deluxe_orders ADD COLUMN unexpected TEXT");
                    if (shape == "old-guild")
                    {
                        using var tx = db.BeginTransaction();
                        SqliteMigrations.ReplaceTableConstraint(db, tx, "character_guild_npc_item_crafts",
                            "CHECK(npc_id IN(400001065,400001061))", "CHECK(npc_id=400001065)");
                        tx.Commit();
                    }
                }
                else
                {
                    Execute(db, """
                        INSERT INTO account_endless_adventure_days VALUES('deluxe-merge',739923,147);
                        INSERT INTO account_daily_attendance_days VALUES('deluxe-merge',20261104,2);
                        INSERT INTO account_daily_attendance_claims VALUES('deluxe-merge',0,0,20261104,1,123,490705101,1,1500);
                        DROP TABLE account_battle_pass_deluxe_orders;
                        """);
                    if (sourceVersion == 195)
                        Execute(db, "DROP TABLE account_weekly_attendance; DROP TABLE weekly_attendance_season");
                    else
                    {
                        var catalog = WeeklyAttendanceTests.Catalog();
                        var at = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(8));
                        var season = DfoServer.Game.Events.WeeklyAttendanceSeason.Create(at, catalog.Weeks);
                        var state = DfoServer.Game.Events.WeeklyAttendanceState.Parse(null, catalog);
                        state.Clears[0] = 6;
                        Check(state.Claim(catalog, season, at, true, false).Length == 1 &&
                            state.Signed == 1 && state.Clears[0] == 2,
                            "196迁移夹具保留真实周签到已签位与非零通关进度");
                        using var command = db.CreateCommand();
                        command.CommandText = """
                            INSERT INTO weekly_attendance_season VALUES(1,$start,$end,$at,$weeks);
                            INSERT INTO account_weekly_attendance VALUES('deluxe-merge',$start,$state);
                            """;
                        command.Parameters.AddWithValue("$start", season.StartsAt);
                        command.Parameters.AddWithValue("$end", season.EndsAt);
                        command.Parameters.AddWithValue("$at", season.ActivatedAt);
                        command.Parameters.AddWithValue("$weeks", season.Weeks);
                        command.Parameters.AddWithValue("$state", state.Serialize());
                        command.ExecuteNonQuery();
                    }
                    Execute(db, $"PRAGMA user_version={sourceVersion}");
                }
                original = TableNames(db).ToDictionary(table => table, table =>
                {
                    string[] columns = ColumnNames(db, table);
                    return (columns, Rows(db, table, columns));
                });
            }
            if (shape != "normal")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "未知豪华或部分公会形态不冒认原193/194，拒绝时全库保持：" + shape);
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is 197L &&
                    original.All(row => ColumnNames(db, row.Key).SequenceEqual(row.Value.Columns) &&
                        Rows(db, row.Key, row.Value.Columns) == row.Value.Rows),
                    $"{sourceVersion}升级保留全部既有表的字段、rowid和非空账号/资产/世代事实");
                Check(Query(db, "SELECT sql FROM sqlite_schema WHERE name='character_guild_npc_item_crafts'") is string craft &&
                    craft.Contains("CHECK(npc_id IN(400001065,400001061))", StringComparison.Ordinal) &&
                    craft.Contains("CHECK(benefit_id>0)", StringComparison.Ordinal) &&
                    Query(db, "SELECT sql FROM sqlite_schema WHERE name='guild_request_claims'") is string requests &&
                    requests.Contains("CHECK(kind BETWEEN 1 AND 7)", StringComparison.Ordinal) &&
                    Query(db, "SELECT count(*) FROM character_guild_npc_craft_weeks") is 0L &&
                    Query(db, "SELECT count(*) FROM guild_dungeon_scroll_receipts") is 0L,
                    "正式193三处约束及周次/净化表已补全，不能只跳改版本号");
                if (sourceVersion is 193 or 194)
                    Check(Query(db, "SELECT sql FROM sqlite_schema WHERE name='account_battle_pass_deluxe_orders'") is string actual &&
                        actual == deluxeSql && Query(db, "SELECT count(*) FROM account_battle_pass_deluxe_orders") is 1L,
                        "旧193/194非空豪华订单及原DDL原样保留，197不重建或重发");
                else
                    Check(Query(db, "SELECT count(*) FROM account_battle_pass_deluxe_orders") is 0L &&
                        Query(db, "SELECT earned FROM account_endless_adventure_days WHERE account='deluxe-merge'") is 147L &&
                        Query(db, "SELECT clears FROM account_daily_attendance_days WHERE account='deluxe-merge'") is 2L &&
                        Query(db, "SELECT mail_id FROM account_daily_attendance_claims WHERE account='deluxe-merge'") is 123L,
                        "正式195/196追加空豪华表，既有无尽与签到进度/领取事实保持");
                if (sourceVersion == 196)
                {
                    Check(Query(db, "SELECT count(*) FROM weekly_attendance_season") is 1L &&
                        Query(db, "SELECT count(*) FROM account_weekly_attendance WHERE account='deluxe-merge'") is 1L,
                        "正式196升级197保留非空冻结周赛季与账号进度，不重新激活或清零");
                    using var owner = new AccountOwners().Acquire("deluxe-merge", static () => { });
                    var repository = new CharacterRepository(path);
                    var season = repository.ReadWeeklyAttendanceSeason()!;
                    var state = repository.ReadWeeklyAttendance(owner, season, WeeklyAttendanceTests.Catalog());
                    Check(state.Signed == 1 && state.Clears[0] == 2 && state.LastObserved == season.ActivatedAt,
                        "196原周签到状态升级后仍能按原领域目录正常恢复");
                }
                Check(Query(db, "PRAGMA integrity_check") is "ok" &&
                    Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "迁移后完整性及外键正确");
            }
            string upgraded = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == upgraded, $"{sourceVersion}升级197后再次打开完全幂等");
        }
    }
}
