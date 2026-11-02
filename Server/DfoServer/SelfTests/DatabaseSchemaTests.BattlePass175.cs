using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;

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
                    Check(Query(db, "SELECT (SELECT count(*) FROM account_battle_pass_seasons)+(SELECT count(*) FROM account_battle_pass_claims)+(SELECT count(*) FROM battle_pass_season_rules)+(SELECT count(*) FROM account_battle_pass_mission_progress)+(SELECT count(*) FROM account_battle_pass_mission_claims)+(SELECT count(*) FROM account_battle_pass_source_events)+(SELECT count(*) FROM account_battle_pass_certificate_orders)+(SELECT count(*) FROM account_battle_pass_cera_consumption)+(SELECT count(*) FROM account_battle_pass_cera_orders)") is 0L,
                        "只从九张战令表均为空的隔离库构造旧版本夹具");
                    Execute(db, """
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
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('account_battle_pass_seasons','account_battle_pass_claims','battle_pass_season_rules','account_battle_pass_mission_progress','account_battle_pass_mission_claims','account_battle_pass_source_events','account_battle_pass_certificate_orders','account_battle_pass_cera_consumption','account_battle_pass_cera_orders')") is 9L &&
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
                        $"{sourceVersion}升级当前版本创建九张战令表，保留婚姻礼遇、漩涡水位、传送冷却和资产并可重复打开");
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
}
