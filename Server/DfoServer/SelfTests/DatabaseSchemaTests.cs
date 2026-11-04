using static DfoServer.Testing.TestCheck;
using DfoServer.Persistence;
using DfoServer.Game.Auth;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Diagnostics;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    // 仅用于隔离测试库回退到v100及更早；先卸跨资产触发器，再按子表顺序移除后续公会结构。
    internal const string GuildExtensionFixtureRewindSql = """
        DROP TRIGGER IF EXISTS guild_cargo_exclusive_insert;
        DROP TRIGGER IF EXISTS guild_cargo_exclusive_update;
        DROP TRIGGER IF EXISTS inventory_not_guild_cargo_insert;
        DROP TRIGGER IF EXISTS inventory_not_guild_cargo_update;
        DROP TRIGGER IF EXISTS title_not_guild_cargo_insert;
        DROP TRIGGER IF EXISTS title_not_guild_cargo_update;
        DROP TRIGGER IF EXISTS mail_not_guild_cargo_insert;
        DROP TRIGGER IF EXISTS mail_not_guild_cargo_update;
        DROP TABLE IF EXISTS guild_request_claims;
        DROP TABLE IF EXISTS guild_recommended_channels;
        DROP TABLE IF EXISTS guild_experience_receipts;
        DROP TABLE IF EXISTS guild_teleport_receipts;
        DROP TABLE IF EXISTS guild_attendance;
        DROP TABLE IF EXISTS guild_contribution_settlements;
        DROP TABLE IF EXISTS guild_contribution_events;
        DROP TABLE IF EXISTS guild_npc_training_receipts;
        DROP TABLE IF EXISTS character_guild_npc_craft_weeks;
        DROP TABLE IF EXISTS character_guild_npc_item_crafts;
        DROP TABLE IF EXISTS guild_dungeon_scroll_receipts;
        DROP TABLE IF EXISTS character_guild_npc_benefits;
        DROP TABLE IF EXISTS guild_npc_common_benefits;
        DROP TABLE IF EXISTS guild_npc_receipts;
        DROP TABLE IF EXISTS guild_growth_item_receipts;
        DROP TABLE IF EXISTS guild_npc_craft_receipts;
        DROP TABLE IF EXISTS guild_npc_craft_completions;
        DROP TABLE IF EXISTS guild_npc_crafts;
        DROP TABLE IF EXISTS guild_npc_progress;
        DROP TABLE IF EXISTS guild_appearance;
        DROP TABLE IF EXISTS guild_economy_events;
        DROP TABLE IF EXISTS guild_mileage_history;
        DROP TABLE IF EXISTS guild_economy_receipts;
        DROP TABLE IF EXISTS guild_benefits;
        DROP TABLE IF EXISTS guild_economy;
        DROP TABLE IF EXISTS guild_cargo_receipts;
        DROP TABLE IF EXISTS guild_cargo_history;
        DROP TABLE IF EXISTS guild_cargo_slots;
        DROP TABLE IF EXISTS guild_cargo_state;
        DROP TABLE IF EXISTS guild_applications;
        DROP TABLE IF EXISTS guild_membership_cooldowns;
        DROP TABLE IF EXISTS guild_roles;
        DROP TABLE IF EXISTS guild_settings;
        """;

    internal static void RemoveGuildExtensionSchema(SqliteConnection db) => Execute(db, GuildExtensionFixtureRewindSql);

    const string SettlementColumns = "character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,after_experience,revision";

    // 仅供已发布旧库夹具移除后续活动结构；存在累计、订单或奖励事实时必须拒绝回退。
    internal static void RemoveEmptyPromotionFixtureSchema(SqliteConnection db)
    {
        Check(Query(db, "SELECT (SELECT count(*) FROM cash_shop_promotion_awards)+" +
            "(SELECT count(*) FROM cash_shop_promotion_progress)+(SELECT count(*) FROM cash_shop_promotion_orders)+" +
            "(SELECT count(*) FROM cash_shop_promotion_rules)") is 0L,
            "已发布旧库夹具只移除全空的后续活动结构，不丢弃累计或订单事实");
        Execute(db, "DROP TABLE cash_shop_promotion_awards; DROP TABLE cash_shop_promotion_progress; " +
            "DROP TABLE cash_shop_promotion_orders; DROP TABLE cash_shop_promotion_rules");
    }

    internal static void RemoveExperienceBonusSchema(SqliteConnection db)
    {
        RewindZeroExperience(db);
        bool bonus = Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name='other_bonus_experience'") is 1L;
        bool proficiency = Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name='proficiency_gain'") is 1L;
        if (!bonus && !proficiency) return;
        if (bonus && Query(db, "SELECT count(*) FROM character_dungeon_settlement_receipts WHERE other_bonus_experience<>0 OR bonus_json<>'[]'") is not 0L ||
            proficiency && Query(db, "SELECT count(*) FROM character_dungeon_settlement_receipts WHERE proficiency_gain<>0") is not 0L)
            throw new InvalidOperationException("旧库夹具不能丢弃真实经验加成分项");
        using var tx = db.BeginTransaction();
        if (proficiency)
            SqliteMigrations.Execute(db, tx, "ALTER TABLE character_dungeon_settlement_receipts DROP COLUMN proficiency_gain");
        if (!bonus) { tx.Commit(); return; }
        // 回退到本分支账本迁移之前的结构：保留秘药/冒险团分项，只去掉上游 #543 的其他加成。
        SqliteMigrations.ReplaceTableConstraint(db, tx, DungeonExperienceBonusSchema.Table,
            "total_experience=base_experience+rank_experience+growth_contract_experience+potion_experience+adventure_experience+other_bonus_experience)",
            "total_experience=base_experience+rank_experience+growth_contract_experience+potion_experience+adventure_experience)");
        SqliteMigrations.Execute(db, tx, "ALTER TABLE character_dungeon_settlement_receipts DROP COLUMN bonus_json");
        SqliteMigrations.Execute(db, tx, "ALTER TABLE character_dungeon_settlement_receipts DROP COLUMN other_bonus_experience");
        tx.Commit();
    }

    internal static void RemoveAdventurePurchases(SqliteConnection db)
    {
        RemoveCardClaimSchema(db);
        Execute(db, "DROP TABLE IF EXISTS account_adventure_purchases");
    }

    internal static void RemoveCardClaimSchema(SqliteConnection db)
    {
        RemoveAdventureNames(db);
        Execute(db, "DROP TABLE IF EXISTS server_events");
        if (Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_card_rewards') WHERE name='claimed'") is 1L)
            Execute(db, "ALTER TABLE character_dungeon_card_rewards DROP COLUMN claimed");
        if (Query(db, "SELECT count(*) FROM pragma_table_info('account_card_memberships') WHERE name='returning_player_expires'") is 1L)
            Execute(db, "ALTER TABLE account_card_memberships DROP COLUMN returning_player_expires");
    }
    internal static void RemoveAdventureNames(SqliteConnection db)
    {
        RewindQuestSceneClearSchema(db);
        Execute(db, "DROP INDEX IF EXISTS account_adventure_name");
        if (Query(db, "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='name_initialized'") is 1L)
            Execute(db, "ALTER TABLE account_adventures DROP COLUMN name_initialized");
        RemoveAdventureCapsuleWeek(db);
        RemoveAdventureActivity(db);
        RemoveAdventureWeekly(db);
        RemoveAdventureLedger(db);
    }

    // v72 只追加每周地下城配置表、v76 追加角色每周计数表；回退到更早的夹具应真的移除，
    // 确保重放时走真实建表而不是按已有结构跳过。
    internal static void RemoveAdventureWeekly(SqliteConnection db)
    {
        RemoveSkillTreeExpansionSchema(db);
        Execute(db, "DROP TABLE IF EXISTS character_weekly_dungeon; DROP TABLE IF EXISTS account_adventure_weekly_config");
    }

    // v73 追加开启/排除额两列与角色里程、开启标记两张表；回退到 v72 及更早的夹具必须真的移除，
    // 否则真实 ALTER/CREATE 会被按已有结构跳过，旧版模拟失去意义。
    internal static void RemoveAdventureLedger(SqliteConnection db)
    {
        if (Query(db, "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='opening_experience'") is 1L)
            Execute(db, "ALTER TABLE account_adventures DROP COLUMN opening_experience");
        if (Query(db, "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='excluded_experience'") is 1L)
            Execute(db, "ALTER TABLE account_adventures DROP COLUMN excluded_experience");
        Execute(db, "DROP TABLE IF EXISTS character_adventure_credits");
        Execute(db, "DROP TABLE IF EXISTS adventure_ledger_meta");
    }

    // v69 只追加胶囊周期列；任何回退到 v70 及更早的夹具都必须真的移除该列，否则真实 ALTER 撞列。
    internal static void RemoveAdventureCapsuleWeek(SqliteConnection db)
    {
        if (Query(db, "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='capsule_week'") is 1L)
            Execute(db, "ALTER TABLE account_adventures DROP COLUMN capsule_week");
    }

    // v71 追加登录周期与推荐地下城计数；回退到 v70 及更早的夹具必须真的移除，否则迁移被跳过而未验证。
    internal static void RemoveAdventureActivity(SqliteConnection db)
    {
        if (Query(db, "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='login_streak_days'") is 1L)
            Execute(db, "ALTER TABLE account_adventures DROP COLUMN login_streak_days");
        if (Query(db, "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='last_login_day'") is 1L)
            Execute(db, "ALTER TABLE account_adventures DROP COLUMN last_login_day");
        if (Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_state') WHERE name='recommended_dungeon_clears'") is 1L)
            Execute(db, "ALTER TABLE character_dungeon_state DROP COLUMN recommended_dungeon_clears");
    }

    // 旧结构夹具确实移除v40来源列和空场景表，保留旧清图、完成FK与历史行。
    internal static void RewindQuestSceneClearSchema(SqliteConnection db)
    {
        RemoveExperienceBonusSchema(db);
        RewindQuestRentalCompletion(db);
        Execute(db, "DROP TABLE IF EXISTS cube_premium");
        const string table = "character_quest_clear_receipts";
        if (Query(db,"SELECT count(*) FROM pragma_table_info('character_quest_clear_receipts') WHERE name='source_type'") is 1L)
        {
            if (Query(db,"SELECT count(*) FROM character_quest_clear_receipts WHERE source_type<>'MonsterDeath'") is not 0L)
                throw new InvalidOperationException("旧版本夹具不能丢弃实际场景来源");
            var columns = ColumnNames(db,table).Where(name => name is not "\"source_type\"" and not "\"growth_event_id\"" and not "\"scene_operation_id\"").ToArray();
            bool foreignKeys = Query(db,"PRAGMA foreign_keys") is 1L;
            Execute(db,"PRAGMA foreign_keys=OFF");
            try { ChangeReceiptDefinition(db,table,_=>SqliteMigrations.QuestClearSchemaV14,columns); }
            finally { if (foreignKeys) Execute(db,"PRAGMA foreign_keys=ON"); }
        }
        Execute(db,"DROP TABLE IF EXISTS character_dungeon_scene_clear_receipts");
    }
    internal static void RemoveAdventureExpeditions(SqliteConnection db)
    {
        RemoveAdventurePurchases(db);
        Execute(db, "DROP TABLE IF EXISTS account_adventure_receipts; DROP TABLE IF EXISTS account_adventure_expedition_members; DROP TABLE IF EXISTS account_adventure_expeditions");
    }
    internal static void RemoveAdventureRepresentatives(SqliteConnection db)
    {
        RemoveAdventureExpeditions(db);
        Execute(db, "DROP TABLE IF EXISTS account_adventure_representatives");
    }
    internal static void RemoveAdventureSchema(SqliteConnection db)
    {
        RemoveAdventureRepresentatives(db);
        Execute(db, "DROP TABLE IF EXISTS account_adventures");
    }

    internal static void RemoveAdventureSchema(string path)
    {
        using var db = Open(path);
        RemoveAdventureSchema(db);
    }
    internal static void RemoveTitleSchema(SqliteConnection db)
    {
        RemoveAdventureSchema(db);
        Execute(db, """
        DROP TRIGGER IF EXISTS title_book_exclusive_insert; DROP TRIGGER IF EXISTS title_book_exclusive_update;
        DROP TRIGGER IF EXISTS inventory_not_title_insert; DROP TRIGGER IF EXISTS inventory_not_title_update;
        DROP TRIGGER IF EXISTS mail_not_title_insert; DROP TRIGGER IF EXISTS mail_not_title_update;
        DROP TABLE IF EXISTS character_title_book_slots; DROP TABLE IF EXISTS character_title_progress; DROP TABLE IF EXISTS character_title_sections;
        """);
    }

    public static void Run()
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-database-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            InventoryItemRevisionUpgrade(root);
            GuildExtensionsUpgrade(root);
            SkillTreeUpgrade(root);
            CloneTitleUpgrade(root);
            TagEvildomUpgrade(root);
            OrdinaryLukeUpgrade(root);
            AntonNormalUpgrade(Path.Combine(root, "anton-normal"));
            PreyNormalUpgrade(root);
            FrozenLootVersionCollisionUpgrade(root);
            PcRoomVersionCollisionUpgrade(root);
            PreyGuideUpgrade(Path.Combine(root, "prey-guide"));
            FiendwarUpgrade(Path.Combine(root, "fiendwar"));
            OrdinaryLukeDropUpgrade(root);
            PvpArenaResultUpgrade(root);
            PvpRatingUpgrade(root);
            PvpTotalTeamUpgrade(root);
            RaidLifecycleUpgrade(root);
            RaidGuildContributionUpgrade(root);
            FiendwarRecoveryUpgrade(root);
            FiendwarTitlesUpgrade(root);
            AccountMaterialsUpgrade(root);
            AccountMaterialPickupsUpgrade(root);
            QuestNotifyUpgrade(root);
            MarriageSameAccountUpgrade(root);
            PrivilegeServiceUpgrade(root);
            FiendwarCounterUpgrade(root);
            BattlePass175Upgrade(root);
            BattlePassLegacy179Upgrade(root);
            BattlePassDeluxe194Renumber(root);
            PvpTotalRatingUpgrade(root);
            PvpMissionUpgrade(root);
            PvpDailyUpgrade(root);
            PvpRankUpgrade(root);
            FatigueDailyUpgrade(root);
            ServerItemUpgrade(root);
            AccountSkinStorageUpgrade(root);
            PlayerInteractionPreviewUpgrade(root);
            VerifyReceiptDefinitionFixture(root);
            ObjectKeyUpgrade(root);
            AuraCooldownUpgrade(root);
            ZeroExperienceUpgrade(root);
            ItemMakingUpgrade(root);
            PassiveItemsUpgrade(root);
            PickupUidUpgrade(root);
            ConsumableStatesUpgrade(root);
            BossSelectionBuffUpgrade(root);
            BlacklistCountUpgrade(root);
            IncountNumberingUpgrade(root);
            Quest704NumberingUpgrade(root);
            TalismanUpgrade(root);
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
                "ALTER TABLE characters DROP COLUMN blacklist_count",
                "ALTER TABLE characters DROP COLUMN clone_title_item_id",
                "ALTER TABLE characters DROP COLUMN selected_skill_tree",
                "ALTER TABLE account_wallets DROP COLUMN mileage_cash_earned",
                "ALTER TABLE account_wallets DROP COLUMN mileage_shop_purchases",
                "ALTER TABLE character_teleport_cooldowns DROP COLUMN upgrade_aura_until",
                "ALTER TABLE inventory_creature_details DROP COLUMN detail", "DROP TABLE account_client_settings", "DROP TABLE character_client_settings",
                "DROP TABLE character_game_settings",
                "DROP TABLE cube_premium", "ALTER TABLE cube_premium DROP COLUMN cube_type",
                "DROP TABLE character_quest_lifecycle_receipts",
                "DROP TABLE character_tutorial_receipts", "ALTER TABLE character_tutorial_receipts DROP COLUMN plan_snapshot",
                "DROP TABLE character_tutorial_state", "ALTER TABLE character_tutorial_state DROP COLUMN reward_received",
                "DROP TABLE character_quest_skip_receipts", "ALTER TABLE character_quest_skip_receipts DROP COLUMN plan_snapshot",
                "DROP TABLE character_tp_sources", "DROP TABLE character_skill_commands", "DROP TABLE character_skill_combos",
                "DROP TABLE character_dungeon_growth_receipts",
                "ALTER TABLE character_dungeon_growth_receipts DROP COLUMN resource_source",
                "DROP TABLE character_dungeon_state", "DROP TABLE character_dungeon_loot_receipts",
                "DROP TABLE character_dungeon_pickup_receipts", "DROP TABLE character_quest_lifecycle_receipts; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_dungeon_settlement_receipts",
                "ALTER TABLE character_dungeon_settlement_receipts DROP COLUMN report_body",
                "DROP TABLE character_title_progress", "DROP TABLE character_title_book_slots", "DROP TRIGGER inventory_not_title_insert",
                "DROP TABLE character_collectbox_state", "DROP TABLE character_collectbox_slots",
                "DROP TABLE character_collectbox_expirations",
                "DROP TABLE character_skill_switch_inventory_items",
                "DROP TABLE character_skill_switch_inventory_items; DROP TABLE character_skill_switch_inventory",
                "DROP INDEX auction_listings_expiry; DROP INDEX auction_listings_seller; DROP INDEX auction_listings_template; DROP TABLE auction_listings",
                "DROP TABLE account_adventure_weekly_config"
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
                RemoveEmotionSchema(db);
                RemoveHotkeySchema(db);
                RemoveTutorialSchema(db);
                RemoveTitleSchema(db);
                RewindRaidLifecycle(db);
                Execute(db, "DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=14");
                // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
                DropLaterTables(db);
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
            VerifyAcceptanceAssaultV47(root);
            VerifyCubeContractMigration(root);
            VerifyQuestRentalCompletionMigration(root);
            VerifyQuestRentalCompletionMigration(root,45);
            VerifyDungeonSceneSettlementMigration(root);
            VerifySettlementBreakdownMigration(root);
            VerifyAdventureWeeklyMigration(root);
            VerifyWeeklyDungeonUsageMigration(root);
            VerifyAdventureTemporaryNumberingCompatibility(root);
            VerifyPremiumContractsMigration(root);
            VerifyMasterQuest14Compatibility(root);
            VerifyQuestSystemMigration(root);
            VerifyExistingQuestSystemV14(root);
            VerifyExistingQuestType49(root);
            VerifyMasterQuest17Migration(root);
            VerifyLegacyDropV18Migration(root);
            VerifyExistingQuestFatigueV14(root);
            VerifyMissingFatigueMigration(root, 18);
            VerifyMissingFatigueMigration(root, 19);
            VerifyAuctionListingSchema(root);
            VerifyGuildSchema(root);
            VerifyQuestSkipConstraints(root);
            VerifyDropCollectionSchema(root);
            VerifyTutorialSchema(root);
            VerifyTitleMigration(root);
            VerifyCollectBoxMigrations(root);
            VerifyQuestStageMigrations(root);
            VerifySkillSwitchInventoryMigration(root);
            VerifyExperienceCapMigration(root);
            VerifyAccountQuestSourceMigration(root);
            VerifyFutureMigrations(path);
            VerifyQuestEffectsMigration(root);
            VerifyCardClaimUpgrade(root);
            VerifyQuestSceneClearMigration(root);
            string concurrent = Path.Combine(root, "concurrent.db");
            Task.WaitAll(Task.Run(() => new CharacterRepository(concurrent)), Task.Run(() => new AuthenticationRepository(concurrent)));
            using (var db = Open(concurrent))
                Check(Query(db, "SELECT count(*) FROM schema_metadata") is 1L, "并发初始化只创建一份完整基线");
        }
        finally
        {
            TestFiles.DeleteDirectory(root, true);
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} 统一数据库基线、拒绝、重开及增量迁移原子性断言");
    }

    static void BlacklistCountUpgrade(string root)
    {
        root = Path.Combine(root, "blacklist-upgrade");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "blacklist-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('blacklist',0,'BlacklistOld',7,38,1,450,234,0,123)");
                Check(Query(db, "SELECT blacklist_count FROM characters") is 0L, "完整建库为角色建立零计分列");
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "ALTER TABLE characters DROP COLUMN blacklist_count; PRAGMA user_version=64");
                if (scenario == "conflict")
                    Execute(db, "ALTER TABLE characters ADD COLUMN blacklist_count TEXT NOT NULL DEFAULT '0'");
                if (scenario == "late-failure")
                    Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "计分迁移遇到未知同名列或晚期结构错误整笔回滚，不推进版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT blacklist_count||':'||revision||':'||gold FROM characters") is "0:7:123",
                    "真实v64旧结构增量加入零计分，不改角色版本和资产");
                foreach (string value in new[] { "-1", "2147483648", "1.5", "NULL", "'invalid'" })
                    Reject<SqliteException>(() => Execute(db, "UPDATE characters SET blacklist_count=" + value),
                        "计分列拒绝负数、越界、非整数及空值");
                Execute(db, "UPDATE characters SET blacklist_count=2147483647");
                Check(Query(db, "SELECT blacklist_count FROM characters") is 2147483647L, "保留客户端有符号比较的最大合法计分");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "迁移重开不清零既有计分或重复改变结构");
        }
    }

    static void BossSelectionBuffUpgrade(string root)
    {
        root = Path.Combine(root, "boss-buff-upgrade");
        Directory.CreateDirectory(root);
        foreach (bool conflict in new[] { false, true })
        {
            string path = Path.Combine(root, "boss-buff-upgrade-" + conflict + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveGuildExtensionSchema(db);
                RewindRaidLifecycle(db);
                Execute(db, "DROP TABLE character_boss_selection_buff; PRAGMA user_version=103");
                if (conflict) Execute(db, "CREATE TABLE character_boss_selection_buff(character_id INTEGER,account TEXT,state_json TEXT)");
            }
            if (conflict)
            {
                string before = Snapshot(path);
                Reject<InvalidDataException>(() => new CharacterRepository(path), "同名药效表缺少主键和owner外键不能接受为正式结构");
                Check(Snapshot(path) == before, "药效迁移结构冲突整体回滚，不推进版本");
            }
            else
            {
                _ = new CharacterRepository(path);
                using var db = Open(path);
                Check(Query(db, "SELECT count(*) FROM character_boss_selection_buff") is 0L &&
                    Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "v103升级只建空药效表，不补造已使用药剂");
                string before = Snapshot(path);
                _ = new CharacterRepository(path);
                Check(before == Snapshot(path), "升级后重开不重复建表或改写数据");
            }
        }
    }

    static void ConsumableStatesUpgrade(string root)
    {
        root = Path.Combine(root, "consumable-upgrade");
        Directory.CreateDirectory(root);
        foreach (bool conflict in new[] { false, true })
        {
            string path = Path.Combine(root, "consumable-upgrade-" + conflict + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "DROP TABLE character_consumable_states; PRAGMA user_version=63");
                if (conflict) Execute(db, "CREATE VIEW character_consumable_states AS SELECT 1 AS character_id");
            }
            if (conflict)
            {
                string before = Snapshot(path);
                Reject<Exception>(() => new CharacterRepository(path), "持续状态迁移遇到同名视图必须失败");
                Check(before == Snapshot(path), "迁移失败不留下半结构或推进版本");
            }
            else
            {
                _ = new CharacterRepository(path);
                using var db = Open(path);
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_consumable_states") is 0L,
                    "v63仅增量建立持续状态表，不给旧角色补药效或冷却");
            }
        }
    }

    static void PassiveItemsUpgrade(string root)
    {
        root = Path.Combine(root, "passive-upgrade");
        Directory.CreateDirectory(root);
        foreach (string mode in new[] { "upgrade", "broken", "ddl-failure" })
        {
            string path = Path.Combine(root, "passive-" + mode + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('passive-migration',0,'PassiveOld',7,38,1,450,234,0)");
                Execute(db, """
                    INSERT INTO guilds(guild_id,name,address,master_character_id,public_funds,created_at_unix)
                      VALUES(1,'PassiveGuild','passive-guild',1,123,456);
                    INSERT INTO guild_members(guild_id,character_id,contribution,joined_at_unix)
                      VALUES(1,1,19,456);
                    """);
                RewindPickupUids(db);
                RewindRaidLifecycle(db);
                Execute(db, "DROP TABLE character_dungeon_passive_pickups; PRAGMA user_version=44");
                if (mode == "broken")
                    Execute(db, """
                        CREATE TABLE character_dungeon_passive_pickups(character_id INTEGER,account TEXT,ground_key INTEGER,
                          instance_id TEXT,task_attempt INTEGER,epoch INTEGER,picked_core TEXT,gold_amount INTEGER,
                          destination_slot INTEGER,item_uid INTEGER,after_core TEXT,currency_kind INTEGER,amount INTEGER,
                          after_balance INTEGER,account_revision INTEGER,revision INTEGER)
                        """);
                if (mode == "ddl-failure")
                    Execute(db, "CREATE VIEW character_dungeon_passive_pickups AS SELECT 1 AS character_id");
            }
            string old = Snapshot(path);
            if (mode != "upgrade")
            {
                if (mode == "broken")
                    Reject<InvalidDataException>(() => new CharacterRepository(path), "v45不能接管缺少拾取约束或归属外键的同名表");
                else
                    Reject<SqliteException>(() => new CharacterRepository(path), "v45建表冲突拒绝，不能推进版本后留下半结构");
                Check(Snapshot(path) == old, "v45升级失败保留原角色、公会、结构和版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "SELECT revision FROM characters WHERE name='PassiveOld'") is 7L &&
                    Query(db, "SELECT count(*) FROM character_dungeon_passive_pickups") is 0L && Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT name||':'||address||':'||master_character_id||':'||public_funds||':'||created_at_unix FROM guilds") is "PassiveGuild:passive-guild:1:123:456" &&
                    Query(db, "SELECT guild_id||':'||character_id||':'||contribution||':'||joined_at_unix FROM guild_members") is "1:1:19:456",
                    "v44到v45只新增拾取结构，不发放物品或重写角色、公会及成员");
            string upgraded = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == upgraded, "v45重开不重复拾取或改变原存档");
        }
    }

    static void PickupUidUpgrade(string root)
    {
        root = Path.Combine(root, "pickup-uids");
        Directory.CreateDirectory(root);
        string[] tables = ["character_dungeon_pickup_receipts", "character_dungeon_drop_pickups"];
        foreach (string mode in new[] { "upgrade", "unknown-shape", "dependency", "late-failure" })
        {
            string path = Path.Combine(root, mode + ".db");
            _ = new CharacterRepository(path);
            string[] rows;
            string[][] columns;
            string secondary;
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,experience,gold)
                      VALUES('old',0,'UidOld',7,38,1,450,234,0,2,60,123);
                    INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision)
                      VALUES(1,'old','legacy','legacy-instance',3145,1,1,76121,123,99,1,20,'legacy-rule','legacy-source',1,1,2,2,40,60,7);
                    INSERT INTO character_dungeon_loot_receipts
                      VALUES(1,'old','legacy','legacy-instance',1,1,76121,123,'legacy-rule','old-source',513,'{}',100,200,0);
                    INSERT INTO character_dungeon_pickup_receipts
                      VALUES(1,'old','legacy',513,'legacy-instance',1,1,0,177,1,'{}',8);
                    """);
                AddDropDependencyFixture(db);
                RewindPickupUids(db);
                for (int i = 0; i < tables.Length; i++)
                {
                    string table = tables[i];
                    Execute(db, $"UPDATE {table} SET rowid={73 + i}");
                    Execute(db, $"""
                        CREATE INDEX uid_index_{i} ON {table}(item_uid);
                        CREATE TRIGGER uid_guard_{i} BEFORE UPDATE ON {table}
                          WHEN NEW.item_uid=17 BEGIN SELECT RAISE(ABORT,'UID guard'); END;
                        CREATE TRIGGER uid_no_replay_{i} AFTER INSERT ON {table}
                          BEGIN UPDATE characters SET revision=revision+99 WHERE id=NEW.character_id; END;
                        """);
                    Reject<SqliteException>(() => Execute(db, $"UPDATE {table} SET item_uid=2147483648"),
                        "旧版真实32位上限拒绝高UID，夹具不只修改版本号");
                }
                columns = tables.Select(table => ColumnNames(db, table)).ToArray();
                rows = tables.Select((table, index) => Rows(db, table, columns[index])).ToArray();
                secondary = (string)Query(db, "SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_schema WHERE name LIKE 'uid_%' ORDER BY name)")!;
                RewindRaidLifecycle(db);
                Execute(db, "DROP TABLE character_dungeon_passive_pickups; PRAGMA user_version=44");
                if (mode == "unknown-shape")
                    ChangeReceiptDefinition(db, tables[1], sql => sql.Replace("CHECK(amount>0)", "CHECK(amount>=0)", StringComparison.Ordinal));
                if (mode == "dependency")
                    Execute(db, """
                        CREATE TABLE unknown_pickup_child(character_id INTEGER,ground_key INTEGER,
                          FOREIGN KEY(character_id,ground_key) REFERENCES character_dungeon_drop_pickups(character_id,ground_key));
                        """);
                if (mode == "late-failure")
                    Execute(db, "CREATE VIEW character_dungeon_passive_pickups AS SELECT 1 AS character_id");
            }
            string old = Snapshot(path);
            if (mode != "upgrade")
            {
                if (mode == "late-failure")
                    Reject<SqliteException>(() => new CharacterRepository(path), "两张UID回执迁移后新增表失败，整个v45回滚");
                else
                    Reject<InvalidDataException>(() => new CharacterRepository(path), "未知回执定义或入向依赖拒绝自动重建");
                Check(Snapshot(path) == old, "迁移拒绝保留原回执、索引、触发器、角色资产及版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(tables.Select((table, index) => Rows(db, table, columns[index])).SequenceEqual(rows) &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "UID迁移保留两类回执全部原列和rowid，不重放历史插入触发器或发放资产");
                Check(Query(db, "SELECT count(*) FROM character_dungeon_drop_pickups WHERE receipt_format=0 AND destination_list IS NULL AND epic_piece IS NULL") is 1L,
                    "旧拾取回执只补v160默认格式及空目标列表、碎片列，保留历史领取语义");
                Check(Query(db, "SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_schema WHERE name LIKE 'uid_%' ORDER BY name)") is string actual &&
                    actual == secondary && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "UID迁移完整保留原索引、触发器与来源外键");
                foreach (string table in tables)
                {
                    Execute(db, $"UPDATE {table} SET item_uid=2147483648");
                    Check(Query(db, $"SELECT item_uid FROM {table}") is 2147483648L, "当前两类回执接受普通库存内部64位UID");
                    foreach (long value in new long[] { 0, -1, 17 })
                        Reject<SqliteException>(() => Execute(db, $"UPDATE {table} SET item_uid={value}"),
                            "迁移后仍拒绝无效UID并保留原更新触发器");
                }
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion && Query(db, "PRAGMA integrity_check") is "ok",
                    "UID与特殊物品拾取结构在同一连续迁移提交");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "高UID历史回执重开保持原值，不重复迁移或改写角色");
        }
    }

    // 独立覆盖同编号分支库与已发布主线，不依赖Run之前的夹具或资源。
    static void FrozenLootVersionCollisionUpgrade(string root)
    {
        root = Path.Combine(root, "frozen-loot-version-collision");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "source160", "master159", "master160", "master161", "master160-missing-prey", "half", "unknown", "invalid-row", "late-failure" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            bool published = scenario.StartsWith("master", StringComparison.Ordinal);
            int oldVersion = scenario == "master159" ? 159 : scenario == "master161" ? 161 : 160;
            Dictionary<string, (string[] Columns, string Rows)> beforeRows;
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,experience,gold)
                      VALUES('frozen-old',0,'FrozenOld',7,38,1,450,234,0,2,60,123);
                    INSERT INTO account_wallets(account,revision,rental_points) VALUES('frozen-old',5,17);
                    INSERT INTO character_dungeon_growth_receipts
                      (character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,
                       combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,
                       before_level,after_level,before_experience,after_experience,revision)
                      VALUES(1,'frozen-old','legacy','legacy-instance',3145,1,1,76121,123,
                       99,1,20,'legacy-rule','legacy-source',1,1,2,2,40,60,7);
                    INSERT INTO character_dungeon_drop_batches
                      (character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,object_count)
                      VALUES(1,'frozen-old','legacy','legacy-instance',3145,1,1,76121,123,'legacy-rule','legacy-source',1);
                    INSERT INTO character_dungeon_drop_objects
                      (character_id,event_id,instance_id,ordinal,ground_key,core,gold_amount,x,y,record_slot)
                      VALUES(1,'legacy','legacy-instance',0,512,NULL,1,100,200,0);
                    INSERT INTO character_dungeon_drop_pickups
                      (character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,
                       destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision)
                      VALUES(1,'frozen-old','legacy',512,'legacy-instance',1,1,NULL,NULL,NULL,0,1,123,5,7);
                    """);
                if (published)
                {
                    RewindFrozenLootSchema(db);
                    if (oldVersion < 160) Execute(db, "DROP TABLE character_prey_normal_progress");
                    else Execute(db, "INSERT INTO character_prey_normal_progress VALUES(1,'frozen-old',20261031,3,4)");
                    if (scenario == "master160-missing-prey") Execute(db, "DROP TABLE character_prey_normal_progress");
                    if (oldVersion < 161) Execute(db, "DROP TABLE character_name_history");
                    else Execute(db, "INSERT INTO character_name_history VALUES(1,'frozen-old',6,'FormerFrozen',123)");
                }
                else
                {
                    Execute(db, """
                        UPDATE character_dungeon_drop_batches SET freeze_format=1,combat_sequence=99,committed_epoch=1,map_snapshot='{}',uses_collection=1;
                        UPDATE character_dungeon_drop_pickups SET receipt_format=1;
                        DROP TABLE character_prey_normal_progress;
                        DROP TABLE character_name_history;
                        """);
                    if (scenario == "half") Execute(db, "ALTER TABLE character_dungeon_drop_batches DROP COLUMN map_snapshot");
                    if (scenario == "unknown") Execute(db, "ALTER TABLE character_dungeon_drop_pickups ADD COLUMN unknown_frozen INTEGER");
                    if (scenario == "invalid-row") Execute(db, "UPDATE character_dungeon_drop_batches SET combat_sequence=NULL");
                    if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                }
                Execute(db, $"PRAGMA user_version={oldVersion}");
                beforeRows = TableNames(db).ToDictionary(table => table,
                    table => (ColumnNames(db, table), Rows(db, table, ColumnNames(db, table))));
            }
            if (scenario is "master160-missing-prey" or "half" or "unknown" or "invalid-row" or "late-failure")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before,
                    "同编号冻结旧库不完整、未知结构、非法历史或后段失败均整笔回滚结构、版本与资产：" + scenario);
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion && version >= 162,
                    "旧冻结160与已发布159/160/161连续升级至冻结162后的当前版本：" + scenario);
                Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_prey_normal_progress','character_name_history')") is 2L,
                    "升级同时保留已发布普雷160与改名161结构：" + scenario);
                foreach (var (table, original) in beforeRows)
                    Check(Rows(db, table, original.Columns) == original.Rows,
                        "碰撞升级保留原表全部历史行、rowid和字段：" + scenario + "/" + table);
                Check(Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_drop_batches') WHERE name='freeze_format'") is 1L &&
                    Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_drop_pickups') WHERE name='epic_piece'") is 1L,
                    "已发布主线真实补建冻结列：" + scenario);
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "碰撞升级后完整性与来源外键保持：" + scenario);
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved, "碰撞升级重开幂等，不补发历史资产：" + scenario);
        }
    }

    // 仅用于还原发布主线162之前的真实夹具；禁止丢弃已经生成的冻结事实。
    static void RewindFrozenLootSchema(SqliteConnection db)
    {
        const string batches = DungeonFrozenLootSchema.Batches;
        const string pickups = DungeonFrozenLootSchema.Pickups;
        if (Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_drop_batches') WHERE name='freeze_format'") is 1L &&
            Query(db, "SELECT count(*) FROM character_dungeon_drop_batches WHERE freeze_format<>0 OR combat_sequence IS NOT NULL OR committed_epoch IS NOT NULL OR map_snapshot IS NOT NULL OR uses_collection IS NOT NULL") is not 0L ||
            Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_drop_pickups') WHERE name='receipt_format'") is 1L &&
            Query(db, "SELECT count(*) FROM character_dungeon_drop_pickups WHERE receipt_format<>0 OR destination_list IS NOT NULL OR epic_piece IS NOT NULL") is not 0L)
            throw new InvalidOperationException("旧版夹具不能丢弃真实冻结或拾取事实");
        string batchDefinition = DungeonDropCollectionSchema.Definition.Split(';')
            .Single(sql => sql.Contains("CREATE TABLE character_dungeon_drop_batches(", StringComparison.Ordinal));
        string[] batchColumns = ColumnNames(db, batches).Where(name => name is not "\"freeze_format\"" and not "\"combat_sequence\"" and
            not "\"committed_epoch\"" and not "\"map_snapshot\"" and not "\"uses_collection\"").ToArray();
        string[] pickupColumns = ColumnNames(db, pickups).Where(name => name is not "\"receipt_format\"" and not "\"destination_list\"" and not "\"epic_piece\"" and not "\"account_material\"").ToArray();
        bool foreignKeys = Query(db, "PRAGMA foreign_keys") is 1L;
        Execute(db, "PRAGMA foreign_keys=OFF");
        try
        {
            ChangeReceiptDefinition(db, pickups, _ => DungeonObjectKeySchema.Definition(DungeonDropCollectionSchema.PickupDefinition, pickups), pickupColumns);
            ChangeReceiptDefinition(db, batches, _ => DungeonObjectKeySchema.Definition(batchDefinition, batches), batchColumns);
        }
        finally { if (foreignKeys) Execute(db, "PRAGMA foreign_keys=ON"); }
    }

    static void RewindPickupUids(SqliteConnection db)
    {
        RewindObjectKeys(db);
        RewindQuestDungeonAlternatives(db);
        // 回退v45前的真实拾取定义，同时移除v162列及碎片分支，不能留下新旧混合夹具。
        const string pickups = "character_dungeon_drop_pickups";
        string[] columns = ColumnNames(db, pickups).Where(name => name is not "\"receipt_format\"" and not "\"destination_list\"" and not "\"epic_piece\"" and not "\"account_material\"").ToArray();
        ChangeReceiptDefinition(db, pickups, _ => DungeonDropCollectionSchema.PreviousPickupDefinition, columns);
        foreach (string table in new[] { "character_dungeon_pickup_receipts", "character_dungeon_drop_pickups" })
            ChangeReceiptDefinition(db, table, sql => sql.Replace("item_uid>0", "item_uid BETWEEN 1 AND 2147483647", StringComparison.Ordinal));
    }

    static void RewindDevilServices(SqliteConnection db)
    {
        DropQuestStageSchema(db);
        foreach (string column in new[] { "devil_usage", "stamina_until" })
            if (Query(db, "SELECT count(*) FROM pragma_table_info('characters') WHERE name='" + column + "'") is 1L)
                Execute(db, "ALTER TABLE characters DROP COLUMN " + column);
    }

    static void RewindQuestDungeonAlternatives(SqliteConnection db)
    {
        RewindDevilServices(db);
        const string table = "character_quest_completion_receipts";
        if (Query(db,"SELECT sql FROM sqlite_schema WHERE name='"+table+"'") is not string sql ||
            !sql.Contains("condition_type IN(1,3,45)",StringComparison.Ordinal)) return;
        if (Query(db,"SELECT count(*) FROM "+table+" WHERE condition_type=45") is not 0L)
            throw new InvalidOperationException("旧版本夹具不能丢弃真实类型45领奖");
        ChangeReceiptDefinition(db,table,_ => SqliteMigrations.QuestCompletionSchemaV42);
    }

    static void RewindExpertJobTutorialCompletion(SqliteConnection db)
    {
        RewindQuestTargetTypes(db);
        const string table = "character_quest_completion_receipts";
        if (Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + table + "'") is not string sql ||
            !sql.Contains("12,17,19,25", StringComparison.Ordinal)) return;
        if (Query(db, "SELECT count(*) FROM " + table + " WHERE condition_type=17") is not 0L)
            throw new InvalidOperationException("旧版本夹具不能丢弃真实类型17领奖");
        long schemaVersion = (long)Query(db, "PRAGMA schema_version")!;
        Execute(db, "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql='" +
            sql.Replace("'", "''", StringComparison.Ordinal).Replace("12,17,19,25", "12,19,25", StringComparison.Ordinal)
                .Replace("60,64,65,67,70", "64,65,67,70", StringComparison.Ordinal) +
            "' WHERE type='table' AND name='" + table + "'; PRAGMA schema_version=" + (schemaVersion + 1) +
            "; PRAGMA writable_schema=OFF; SELECT * FROM " + table + " LIMIT 0");
    }

    static void RewindQuestRentalCompletion(SqliteConnection db)
    {
        RewindDungeonSceneSettlement(db);
        const string table = "character_quest_completion_receipts";
        if (Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + table + "'") is string sql &&
            (sql.Contains("12,17,19,25", StringComparison.Ordinal) || sql.Contains("12,19,25", StringComparison.Ordinal)))
            ChangeReceiptDefinition(db, table, definition => definition
                .Replace("12,17,19,25", "12,25", StringComparison.Ordinal)
                .Replace("12,19,25", "12,25", StringComparison.Ordinal));
    }

    // v42仍是正死亡场景与结算；回退只接受没有v43/v70新事实的夹具，不删除真实来源或奖励。
    internal static void RewindDungeonSceneSettlement(SqliteConnection db)
    {
        RemoveExperienceBonusSchema(db);
        RewindQuestDungeonAlternatives(db);
        DropGuildSchema(db);
        const string settlement = "character_dungeon_settlement_receipts", scene = "character_dungeon_scene_clear_receipts";
        bool hasNewColumns = Query(db, "SELECT count(*) FROM pragma_table_info('" + settlement + "') WHERE name='scene_operation_id'") is 1L;
        bool hasBreakdown = Query(db, "SELECT count(*) FROM pragma_table_info('" + settlement + "') WHERE name='potion_experience'") is 1L;
        if (hasBreakdown && !hasNewColumns) throw new InvalidOperationException("旧版本夹具遇到半份通关经验分项结构");
        string breakdown = hasBreakdown ? " OR potion_experience<>0 OR adventure_experience<>0" : "";
        if (hasNewColumns && Query(db, "SELECT count(*) FROM " + settlement + " WHERE scene_operation_id IS NOT NULL OR growth_contract_experience<>0" + breakdown) is not 0L ||
            Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='" + scene + "'") is 1L &&
            Query(db, "SELECT count(*) FROM " + scene + " WHERE length(death_events)=0") is not 0L)
            throw new InvalidOperationException("旧版本夹具不能丢弃实际空场景来源、成长契约、秘药或冒险团经验");
        bool foreignKeys = Query(db, "PRAGMA foreign_keys") is 1L;
        Execute(db, "PRAGMA foreign_keys=OFF");
        try
        {
            if (hasNewColumns)
                ChangeReceiptDefinition(db, settlement, _ => DungeonSceneSettlementSchema.PreviousSettlementSql,
                    ColumnNames(db, settlement).Where(name => name is not "\"scene_operation_id\"" and not "\"growth_contract_experience\""
                        and not "\"potion_experience\"" and not "\"adventure_experience\"").ToArray());
            if (Query(db, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + scene + "'") is string definition &&
                !definition.Contains("length(death_events)>0", StringComparison.Ordinal))
                ChangeReceiptDefinition(db, scene, _ => SqliteMigrations.QuestSceneClearSchema);
        }
        finally { if (foreignKeys) Execute(db, "PRAGMA foreign_keys=ON"); }
    }

    // v70 只追加秘药与冒险团分项；旧版本夹具必须真的移除两列，保留结算、翻牌历史及外键。
    internal static void RewindSettlementBreakdown(SqliteConnection db)
    {
        const string settlement = "character_dungeon_settlement_receipts";
        // 上游 #543 的其他加成分项（v66）先于本步（v70）；回退到 v69 前必须先移除这两列，
        // 否则重建回执表时的复制列表引用了目标定义中不存在的列。
        RemoveExperienceBonusSchema(db);
        if (Query(db, "SELECT count(*) FROM pragma_table_info('" + settlement + "') WHERE name='potion_experience'") is not 1L) return;
        if (Query(db, "SELECT count(*) FROM " + settlement + " WHERE potion_experience<>0 OR adventure_experience<>0") is not 0L)
            throw new InvalidOperationException("旧版本夹具不能丢弃实际秘药或冒险团经验");
        bool foreignKeys = Query(db, "PRAGMA foreign_keys") is 1L;
        Execute(db, "PRAGMA foreign_keys=OFF");
        try
        {
            ChangeReceiptDefinition(db, settlement, _ => DungeonSceneSettlementSchema.SettlementSql,
                ColumnNames(db, settlement).Where(name => name is not "\"potion_experience\"" and not "\"adventure_experience\"").ToArray());
        }
        finally { if (foreignKeys) Execute(db, "PRAGMA foreign_keys=ON"); }
    }

    private static void VerifyDungeonSceneSettlementMigration(string root)
    {
        string path = Path.Combine(root, "scene-settlement-v42.db");
        _ = new CharacterRepository(path);
        new AuthenticationService(new AuthenticationRepository(path)).Create("scene-settlement-history", "fixture-password");
        const string operation = "11111111111111111111111111111111", acceptance = "22222222222222222222222222222222";
        const string emptyOperation = "44444444444444444444444444444444";
        string[] tables = ["character_quest_lifecycle_receipts", "character_dungeon_scene_clear_receipts",
            "character_dungeon_settlement_receipts", "character_quest_clear_receipts", "character_quest_completion_receipts",
            "character_dungeon_card_rewards", "character_dungeon_card_options"];
        string[][] columns;
        string[] history;
        using (var db = Open(path))
        {
            RewindDungeonSceneSettlement(db);
            Execute(db, $$"""
                INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                  VALUES('scene-settlement-history',0,'SceneSettlementHistory',8,38,1,450,234,0,123);
                INSERT INTO account_wallets(account,revision,rental_points) VALUES('scene-settlement-history',5,17);
                INSERT INTO cube_premium VALUES(1,1,5);
                INSERT INTO character_quest_lifecycle_receipts(rowid,character_id,account,operation_id,action,quest_id,
                  acceptance_id,before_revision,revision,before_active,after_active,changed_items,scene_event,before_completed,after_completed)
                  VALUES(11,1,'scene-settlement-history','{{operation}}','Condition',100,'{{acceptance}}',7,8,
                    '[]','[]','[]','{"Frozen":"scene"}','[]','[]');
                INSERT INTO character_dungeon_scene_clear_receipts(rowid,character_id,account,operation_id,source_type,
                  instance_id,quest_id,task_attempt,before_epoch,epoch,dungeon_id,map_id,template_id,source_ordinal,fingerprint,death_events,generation,revision)
                  VALUES(13,1,'scene-settlement-history','{{operation}}','PassiveObject','scene-history',100,1,1,2,
                    191,70407,2115,0,'{{new string('a',64)}}',zeroblob(16),1,8);
                INSERT INTO character_quest_clear_receipts(rowid,character_id,account,acceptance_id,quest_id,quest_slot,
                  event_id,instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision,source_type,growth_event_id,scene_operation_id)
                  VALUES(17,1,'scene-settlement-history','{{acceptance}}',100,0,'{{operation}}','scene-history',1,2,
                    191,70407,1,8,'PassiveObject',NULL,'{{operation}}');
                INSERT INTO character_quest_completion_receipts(rowid,character_id,account,acceptance_id,quest_id,event_id,instance_id,
                  task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,
                  difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,
                  before_revision,revision,rewards,clear_acceptance_id)
                  VALUES(19,1,'scene-settlement-history','{{acceptance}}',100,'{{operation}}','scene-history',1,2,1,
                    'history-rule','history-resource','history-quest',0,0,0,'normal',100,0,0,1,1,60,60,7,8,'[]','{{acceptance}}');
                INSERT INTO character_quest_completion_receipts(rowid,character_id,account,acceptance_id,quest_id,generation,
                  rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,
                  experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,rewards,condition_type,plan_snapshot)
                  VALUES(21,1,'scene-settlement-history','33333333333333333333333333333333',8581,1,
                    'rental-history','rental-resource','rental-quest',0,0,0,'normal',100,0,0,1,1,60,60,7,8,'[]',19,'{"History":19}');
                INSERT INTO character_dungeon_settlement_receipts(rowid,{{SettlementColumns}})
                  VALUES(23,1,'scene-settlement-history','history-settlement','scene-history',100,1,2,191,70407,
                    zeroblob(16),X'0102',1,20,2,22,0,'history-rule','history-resource',1,1,60,82,8);
                INSERT INTO character_dungeon_card_rewards(rowid,character_id,account,instance_id,plan_json,reward_json,revision,claimed)
                  VALUES(29,1,'scene-settlement-history','scene-history','{"Frozen":"card"}','{"History":"reward"}',8,0);
                INSERT INTO character_dungeon_card_options(rowid,character_id,account,instance_id,options_json,black_diamond_reward_json)
                  VALUES(31,1,'scene-settlement-history','scene-history','{"Frozen":"option"}','{"History":"bonus"}');
                CREATE TABLE scene_settlement_migration_audit(value TEXT);
                PRAGMA user_version=42;
                """);
            foreach (string table in tables.Skip(1))
                Execute(db, "CREATE INDEX " + table + "_history ON " + table + "(account); CREATE TRIGGER " + table +
                    "_history_insert AFTER INSERT ON " + table + " BEGIN INSERT INTO scene_settlement_migration_audit VALUES('" + table + "'); END");
            columns = tables.Select(table => ColumnNames(db, table)).ToArray();
            history = tables.Select((table, index) => Rows(db, table, columns[index])).ToArray();
            Check(Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name IN('scene_operation_id','growth_contract_experience')") is 0L &&
                Query(db, "SELECT sql FROM sqlite_schema WHERE name='character_dungeon_scene_clear_receipts'") is string scene &&
                scene.Contains("length(death_events)>0", StringComparison.Ordinal), "v42夹具真实保留非空场景约束，移除两个v43结算列");
        }
        foreach (string scenario in new[] { "late-table", "late-validation", "unknown-scene", "unknown-settlement", "unknown-child", "partial-shape" })
        {
            string damaged = Path.Combine(root, "scene-settlement-v42-" + scenario + ".db");
            using (var source = Open(path)) using (var target = Open(damaged)) source.BackupDatabase(target);
            using (var db = Open(damaged))
            {
                Execute(db, "PRAGMA foreign_keys=OFF");
                if (scenario == "late-table") Execute(db, "CREATE VIEW character_quest_completion_receipts_scene_settlement AS SELECT 1 AS collision");
                else if (scenario == "late-validation") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                else if (scenario == "unknown-scene")
                    ChangeReceiptDefinition(db, tables[1], sql => sql.Replace("source_ordinal>=0", "source_ordinal BETWEEN 0 AND 999", StringComparison.Ordinal));
                else if (scenario == "unknown-settlement")
                    ChangeReceiptDefinition(db, tables[2], sql => sql.Replace("rank_bonus_index BETWEEN -1 AND 4", "rank_bonus_index BETWEEN -1 AND 5", StringComparison.Ordinal));
                else if (scenario == "partial-shape") ChangeReceiptDefinition(db, tables[1], _ => DungeonSceneSettlementSchema.SceneSql);
                else Execute(db, "CREATE TABLE unknown_scene_settlement_child(character_id INTEGER,operation_id TEXT,FOREIGN KEY(character_id,operation_id) REFERENCES character_dungeon_scene_clear_receipts(character_id,operation_id))");
                Execute(db, "PRAGMA foreign_keys=ON");
            }
            string before = Snapshot(damaged);
            RejectDatabase(() => new CharacterRepository(damaged));
            Check(Snapshot(damaged) == before, "物件结算拒绝未知或半份结构、末表及末段校验失败时完整回滚历史父子表、DDL和版本：" + scenario);
        }
        var repository = new CharacterRepository(path);
        using (var db = Open(path))
        {
            for (int index = 0; index < tables.Length; index++)
                Check(Rows(db, tables[index], columns[index]) == history[index], "v43保留全部原列、rowid和冻结JSON/BLOB，不改历史关联：" + tables[index]);
            Check(Query(db, "SELECT count(*) FROM scene_settlement_migration_audit") is 0L &&
                tables.Skip(1).All(table => Query(db, "SELECT count(*) FROM sqlite_schema WHERE tbl_name='" + table +
                    "' AND ((type='index' AND name='" + table + "_history') OR (type='trigger' AND name='" +
                    table + "_history_insert'))") is 2L),
                "v43保留六表索引和触发器，复制历史时不重放业务");
            Check(Query(db, "SELECT scene_operation_id IS NULL AND growth_contract_experience=0 FROM character_dungeon_settlement_receipts WHERE rowid=23") is 1L &&
                Query(db, "SELECT condition_type||':'||plan_snapshot FROM character_quest_completion_receipts WHERE rowid=21") is "19:{\"History\":19}" &&
                Query(db, "SELECT selected||':'||cube_type FROM cube_premium") is "1:5", "v43旧结算只补NULL/0，v42租赁来源与v41晶体选择保持");
            Execute(db, $$"""
                INSERT INTO character_quest_lifecycle_receipts
                  SELECT character_id,account,'{{emptyOperation}}',action,quest_id,'55555555555555555555555555555555',
                    before_revision,revision,before_active,after_active,changed_items,scene_event,before_completed,after_completed
                  FROM character_quest_lifecycle_receipts WHERE rowid=11;
                INSERT INTO character_dungeon_scene_clear_receipts
                  SELECT character_id,account,'{{emptyOperation}}',source_type,'empty-scene',quest_id,task_attempt,before_epoch,epoch,
                    dungeon_id,map_id,template_id,source_ordinal,fingerprint,X'',generation,revision
                  FROM character_dungeon_scene_clear_receipts WHERE rowid=13;
                INSERT INTO character_dungeon_settlement_receipts({{SettlementColumns}},scene_operation_id,growth_contract_experience)
                  SELECT character_id,account,'empty-settlement','empty-scene',quest_id,task_attempt,epoch,dungeon_id,map_id,X'',
                    report_body,generation,base_experience,rank_experience,27,rank_bonus_index,rule_id,resource_source,before_level,
                    after_level,before_experience,87,revision,'{{emptyOperation}}',5
                  FROM character_dungeon_settlement_receipts WHERE rowid=23;
                """);
            Check(Query(db, "SELECT length(death_events)||':'||base_experience||':'||rank_experience||':'||growth_contract_experience||':'||total_experience FROM character_dungeon_settlement_receipts WHERE event_id='empty-settlement'") is "0:20:2:5:27",
                "空死亡物件结算绑定原场景来源，基础、rank和成长契约奖励完整分解");
            foreach (string mutation in new[] { "scene_operation_id=NULL", "scene_operation_id='missing'", "death_events=NULL",
                "death_events=X'01'", "total_experience=26", "growth_contract_experience=-1", "growth_contract_experience=4294967296" })
                Reject<SqliteException>(() => Execute(db, "UPDATE character_dungeon_settlement_receipts SET " + mutation + " WHERE event_id='empty-settlement'"),
                    "物件结算拒绝缺来源、外键断裂、非法死亡BLOB或不一致奖励分解：" + mutation);
            Check(Query(db, "SELECT count(*) FROM scene_settlement_migration_audit") is 2L,
                "恢复后的场景和结算触发器只观察两个新提交，不重复历史");
            Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "v43物件来源、领奖和翻牌历史完整外键保持");
        }
        string stable = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == stable, "v43重开幂等，不重新生成物件来源、成长契约奖励或翻牌");
        using var owner = new AccountOwners().Acquire("scene-settlement-history", () => { });
        var targetCharacter = repository.LoadDeletionTarget(owner, 0);
        Check(repository.Delete(owner, targetCharacter) == targetCharacter, "实际删角按新结算→场景来源顺序删除，不被新增外键阻塞");
        using (var db = Open(path))
            Check(tables.All(table => Query(db, "SELECT count(*) FROM " + table) is 0L) &&
                Query(db, "SELECT count(*) FROM characters") is 0L && Query(db, "SELECT rental_points FROM account_wallets") is 17L &&
                Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L, "删角清理来源、结算、清图、完成和翻牌，保留账号钱包且无残留外键");
    }

    // v70 只追加秘药与冒险团经验分项：v69 历史行按0补齐，翻牌子表与索引触发器保留，非法分解拒绝。
    private static void VerifySettlementBreakdownMigration(string root)
    {
        const string settlement = "character_dungeon_settlement_receipts";
        string path = Path.Combine(root, "breakdown-v69.db");
        _ = new CharacterRepository(path);
        new AuthenticationService(new AuthenticationRepository(path)).Create("breakdown-history", "fixture-password");
        using (var db = Open(path))
        {
            RemoveEmptyPromotionFixtureSchema(db);
            RewindSettlementBreakdown(db);
            Execute(db, $$"""
                INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction)
                  VALUES('breakdown-history',0,'BreakdownHistory',7,38,1,450,234,0);
                INSERT INTO character_dungeon_settlement_receipts(rowid,{{SettlementColumns}},scene_operation_id,growth_contract_experience)
                  VALUES(11,1,'breakdown-history','event-before-v70','instance-before-v70',100,1,2,191,70407,
                    zeroblob(16),X'0102',1,20,2,22,0,'history-rule','history-resource',1,1,60,82,8,NULL,0);
                INSERT INTO character_dungeon_card_rewards(rowid,character_id,account,instance_id,plan_json,reward_json,revision,claimed)
                  VALUES(17,1,'breakdown-history','instance-before-v70','{"Frozen":"card"}','{"History":"reward"}',8,0);
                INSERT INTO character_dungeon_card_options(rowid,character_id,account,instance_id,options_json,black_diamond_reward_json)
                  VALUES(19,1,'breakdown-history','instance-before-v70','{"Frozen":"option"}','{"History":"bonus"}');
                CREATE INDEX breakdown_history_index ON character_dungeon_settlement_receipts(account);
                CREATE TABLE breakdown_migration_audit(value TEXT);
                CREATE TRIGGER breakdown_history_insert AFTER INSERT ON character_dungeon_settlement_receipts
                  BEGIN INSERT INTO breakdown_migration_audit VALUES('settlement'); END;
                PRAGMA user_version=66;
                """);
            Check(Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name IN('potion_experience','adventure_experience')") is 0L,
                "v69夹具真实移除秘药与冒险团经验分项列");
        }
        foreach (string scenario in new[] { "late-failure", "unknown-definition", "unknown-child" })
        {
            string damaged = Path.Combine(root, "breakdown-v69-" + scenario + ".db");
            using (var source = Open(path))
            using (var target = Open(damaged)) source.BackupDatabase(target);
            using (var db = Open(damaged))
            {
                Execute(db, "PRAGMA foreign_keys=OFF");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                else if (scenario == "unknown-definition")
                    ChangeReceiptDefinition(db, settlement, sql => sql.Replace("rank_bonus_index BETWEEN -1 AND 4", "rank_bonus_index BETWEEN -1 AND 5", StringComparison.Ordinal));
                else Execute(db, "CREATE TABLE unknown_breakdown_child(character_id INTEGER,instance_id TEXT,FOREIGN KEY(character_id,instance_id) REFERENCES character_dungeon_settlement_receipts(character_id,instance_id))");
                Execute(db, "PRAGMA foreign_keys=ON");
            }
            string before = Snapshot(damaged);
            RejectDatabase(() => new CharacterRepository(damaged));
            Check(Snapshot(damaged) == before, "v70分项迁移拒绝未知结构/依赖或末段校验失败，整笔保留原DDL、历史行与版本：" + scenario);
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT base_experience||':'||rank_experience||':'||growth_contract_experience||':'||potion_experience||':'||adventure_experience||':'||total_experience FROM " + settlement + " WHERE rowid=11") is "20:2:0:0:0:22",
                "v70历史结算按0补齐秘药与冒险团分项，不改原奖励分解");
            Check(Query(db, "SELECT plan_json||':'||reward_json||':'||claimed FROM character_dungeon_card_rewards WHERE rowid=17") is "{\"Frozen\":\"card\"}:{\"History\":\"reward\"}:0" &&
                Query(db, "SELECT options_json||':'||black_diamond_reward_json FROM character_dungeon_card_options WHERE rowid=19") is "{\"Frozen\":\"option\"}:{\"History\":\"bonus\"}",
                "v70重建父表时保留翻牌冻结计划、已发奖励与领取位");
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='breakdown_history_index'") is 1L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name='breakdown_history_insert'") is 1L &&
                Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "v70恢复结算索引与触发器，版本、外键和完整性一致");
            Execute(db, $$"""
                INSERT INTO character_dungeon_settlement_receipts(rowid,{{SettlementColumns}},scene_operation_id,growth_contract_experience,potion_experience,adventure_experience)
                  VALUES(31,1,'breakdown-history','event-after-v70','instance-after-v70',100,1,2,191,70407,
                    zeroblob(16),X'0103',1,20,2,37,0,'history-rule','history-resource',1,1,60,97,8,NULL,5,3,7);
                """);
            Check(Query(db, "SELECT potion_experience||':'||adventure_experience||':'||total_experience FROM " + settlement + " WHERE rowid=31") is "3:7:37" &&
                Query(db, "SELECT count(*) FROM breakdown_migration_audit") is 1L,
                "v70新回执接受五项经验分解，恢复后的触发器只观察新提交");
            foreach (string mutation in new[] { "potion_experience=4", "adventure_experience=6", "total_experience=38", "adventure_experience=-1" })
                Reject<SqliteException>(() => Execute(db, "UPDATE " + settlement + " SET " + mutation + " WHERE rowid=31"),
                    "v70拒绝与实际到账不一致的秘药或冒险团分解：" + mutation);
        }
        string stable = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == stable, "v70重开幂等，不重复重建结算或翻牌结构");
    }

    private static void VerifyAdventureWeeklyMigration(string root)
    {
        string path = Path.Combine(root, "weekly-v71.db");
        _ = new CharacterRepository(path);
        new AuthenticationService(new AuthenticationRepository(path)).Create("weekly-history", "fixture-password");
        using (var db = Open(path))
        {
            Execute(db, """
                INSERT INTO account_adventures(account,revision,name,created_date,level,experience,remainder,capsule_experience,
                  brave_experience,points,month,name_initialized,capsule_week,login_streak_days,last_login_day)
                  VALUES('weekly-history',3,'WeeklyHistory',20261020,1,0,'0',0,0,zeroblob(20),202610,1,0,0,0);
                """);
            RemoveAdventureWeekly(db);
            RewindRaidLifecycle(db);
            Execute(db, "PRAGMA user_version=68");
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='account_adventure_weekly_config'") is 0L,
                "v71夹具真实移除每周地下城配置表");
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='account_adventure_weekly_config'") is 1L &&
                Query(db, "SELECT count(*) FROM pragma_table_info('account_adventure_weekly_config') WHERE name IN('account','slots')") is 2L &&
                Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "v72从v71只追加每周地下城配置表");
            Reject<SqliteException>(() => Execute(db,
                "INSERT INTO account_adventure_weekly_config(account,slots) VALUES('weekly-history',zeroblob(4))"),
                "v72拒绝非5字节槽位");
            Execute(db, "INSERT INTO account_adventure_weekly_config(account,slots) VALUES('weekly-history',X'00010203FF')");
            Check(Query(db, "SELECT hex(slots) FROM account_adventure_weekly_config WHERE account='weekly-history'") is "00010203FF" &&
                Query(db, "SELECT revision FROM account_adventures WHERE account='weekly-history'") is 3L &&
                Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "v72保存5槽位排列，旧账号进度行与外键不变");
        }
        // 撞名对象：v72必须拒绝并整笔回滚，不能改版本或删既有对象。
        string collision = Path.Combine(root, "weekly-v71-collision.db");
        using (var source = Open(path))
        using (var target = Open(collision)) source.BackupDatabase(target);
        using (var db = Open(collision))
        {
            RemoveAdventureWeekly(db);
            RewindRaidLifecycle(db);
            Execute(db, "PRAGMA user_version=68");
            Execute(db, "CREATE VIEW account_adventure_weekly_config AS SELECT 1 AS account, zeroblob(5) AS slots");
        }
        string before = Snapshot(collision);
        RejectDatabase(() => new CharacterRepository(collision));
        using (var db = Open(collision))
            Check(Snapshot(collision) == before && Query(db, "PRAGMA user_version") is 68L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='view' AND name='account_adventure_weekly_config'") is 1L,
                "v72撞名失败回滚版本与既有对象");
        string stable = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == stable, "v72重开幂等，不重写配置");
    }

    // v76 只追加角色每周地下城计数表：从 v75 夹具必须真实缺表，升级后校验结构、约束、索引与外键。
    private static void VerifyWeeklyDungeonUsageMigration(string root)
    {
        string path = Path.Combine(root, "weekly-usage-v75.db");
        _ = new CharacterRepository(path);
        new AuthenticationService(new AuthenticationRepository(path)).Create("weekly-usage", "fixture-password");
        using (var db = Open(path))
        {
            RemoveGuildExtensionSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                  VALUES('weekly-usage',0,'Usage',0,38,1,450,234,0,0);
                ALTER TABLE characters DROP COLUMN selected_skill_tree;
                DROP TABLE character_weekly_dungeon;
                PRAGMA user_version=75;
                """);
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_weekly_dungeon'") is 0L,
                "v76前置夹具真实移除每周地下城计数表");
        }
        long id;
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM pragma_table_info('character_weekly_dungeon') WHERE name IN('character_id','account','week','slot','used')") is 5L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='character_weekly_dungeon_account'") is 1L &&
                Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "v76从v75只追加每周地下城计数表与账号索引");
            id = (long)Query(db, "SELECT id FROM characters WHERE name='Usage'")!;
            Reject<SqliteException>(() => Execute(db, $"INSERT INTO character_weekly_dungeon(character_id,account,week,slot,used) VALUES({id},'weekly-usage',20261022,9,1)"),
                "v76拒绝9号以外的列号");
            Reject<SqliteException>(() => Execute(db, $"INSERT INTO character_weekly_dungeon(character_id,account,week,slot,used) VALUES({id},'other',20261022,0,1)"),
                "v76拒绝账号与角色不一致的计数行");
            Execute(db, $"INSERT INTO character_weekly_dungeon(character_id,account,week,slot,used) VALUES({id},'weekly-usage',20261022,0,1)");
            Reject<SqliteException>(() => Execute(db, $"INSERT INTO character_weekly_dungeon(character_id,account,week,slot,used) VALUES({id},'weekly-usage',20261022,0,2)"),
                "v76同一角色同一周同一列只有一行");
            Check(Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L, "v76计数行外键一致");
        }
        // 撞名视图：v76 必须整笔回滚，不改版本、不删既有对象。
        string collision = Path.Combine(root, "weekly-usage-v75-collision.db");
        using (var source = Open(path))
        using (var target = Open(collision)) source.BackupDatabase(target);
        using (var db = Open(collision))
        {
            Execute(db, "DROP TABLE character_weekly_dungeon");
            Execute(db, "CREATE VIEW character_weekly_dungeon AS SELECT 1 AS character_id,'x' AS account,0 AS week,0 AS slot,0 AS used");
            RemoveSkillTreeExpansionSchema(db);
            Execute(db, "PRAGMA user_version=75");
        }
        string before = Snapshot(collision);
        RejectDatabase(() => new CharacterRepository(collision));
        using (var db = Open(collision))
            Check(Snapshot(collision) == before && Query(db, "PRAGMA user_version") is 75L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='view' AND name='character_weekly_dungeon'") is 1L,
                "v76撞名视图失败回滚版本与既有对象");
    }

    // 本地整合构建曾把冒险团四项记成v64–v67；上游随后占用v64/v65，这类库必须按v63重跑而不是只跑v68+。
    private static void VerifyAdventureTemporaryNumberingCompatibility(string root)
    {
        string path = Path.Combine(root, "temp-numbering-v67.db");
        _ = new CharacterRepository(path);
        new AuthenticationService(new AuthenticationRepository(path)).Create("temp-numbering", "fixture-password");
        using (var db = Open(path))
        {
            RemoveGuildExtensionSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO account_adventures(account,revision,name,created_date,level,experience,remainder,capsule_experience,
                  brave_experience,points,month,name_initialized,capsule_week,login_streak_days,last_login_day)
                  VALUES('temp-numbering',5,'TempNumbering',20261020,1,0,'0',0,0,zeroblob(20),202610,1,7,3,20261020);
                INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('temp-numbering',0,'Temp',9,38,1,450,234,0,456);
                ALTER TABLE characters DROP COLUMN selected_skill_tree;
                DROP TABLE character_consumable_states;
                ALTER TABLE characters DROP COLUMN blacklist_count;
                PRAGMA user_version=67;
                """);
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_consumable_states'") is 1L &&
                Query(db, "SELECT blacklist_count||':'||gold||':'||revision FROM characters WHERE name='Temp'") is "0:456:9" &&
                Query(db, "SELECT capsule_week||':'||login_streak_days||':'||last_login_day FROM account_adventures WHERE account='temp-numbering'") is "7:3:20261020",
                "临时编号v67库按v63重跑上游v64/v65与冒险团步骤，保留账号与角色资产");
        }
        string stable = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == stable, "临时编号兼容重跑后重开幂等");
    }

    private static void VerifyQuestRentalCompletionMigration(string root, int oldVersion = 41)
    {
        const string table = "character_quest_completion_receipts";
        string path = Path.Combine(root, $"quest-completion-v{oldVersion}.db");
        _ = new CharacterRepository(path);
        new AuthenticationService(new AuthenticationRepository(path)).Create("rental-history", "fixture-password");
        string[] names;
        string originalRows;
        using (var db = Open(path))
        {
            if (oldVersion == 45) RewindQuestDungeonAlternatives(db);
            else RewindQuestRentalCompletion(db);
            Execute(db, """
                INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                  VALUES('rental-history',0,'RentalHistory',8,38,1,450,234,0,123);
                INSERT INTO cube_premium VALUES(1,1,5);
                INSERT INTO character_quest_completion_receipts(rowid,character_id,account,acceptance_id,quest_id,generation,
                  rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,
                  experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,
                  rewards,condition_type,plan_snapshot)
                  VALUES(87,1,'rental-history','1234567890abcdef1234567890abcdef',3145,1,
                    'historical-rule','historical-resource','historical-quest',0,0,0,'normal',100,0,0,1,1,0,0,7,8,'[]',4,'{"History":true}');
                CREATE INDEX rental_history_type ON character_quest_completion_receipts(condition_type);
                CREATE TABLE rental_migration_audit(value INTEGER);
                CREATE TRIGGER rental_history_insert AFTER INSERT ON character_quest_completion_receipts
                  BEGIN INSERT INTO rental_migration_audit VALUES(1); END;
                """);
            RewindRaidLifecycle(db);
            Execute(db,$"PRAGMA user_version={oldVersion}");
            names = ColumnNames(db, table);
            originalRows = Rows(db, table, names);
        }
        foreach (string scenario in new[] { "late-failure", "unknown-definition", "unknown-child" })
        {
            string damaged = Path.Combine(root, $"quest-completion-v{oldVersion}-" + scenario + ".db");
            using (var source = Open(path))
            using (var target = Open(damaged)) source.BackupDatabase(target);
            using (var db = Open(damaged))
            {
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                else if (scenario == "unknown-definition")
                    ChangeReceiptDefinition(db, table, sql => sql.Replace("native_index>=0", "native_index>=0 AND native_index<999", StringComparison.Ordinal));
                else Execute(db, "CREATE TABLE unknown_rental_child(character_id INTEGER,acceptance_id TEXT,FOREIGN KEY(character_id,acceptance_id) REFERENCES character_quest_completion_receipts(character_id,acceptance_id) ON DELETE CASCADE)");
            }
            string before = Snapshot(damaged);
            RejectDatabase(() => new CharacterRepository(damaged));
            Check(Snapshot(damaged) == before, "租赁迁移拒绝未知结构/子表或末段失败，整笔保留原DDL、历史行与版本");
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Rows(db, table, names) == originalRows && Query(db, "SELECT count(*) FROM rental_migration_audit") is 0L,
                "租赁迁移保留历史全部列及rowid，不重放插入触发器");
            Check(Query(db, "SELECT selected||':'||cube_type FROM cube_premium WHERE charac_no=1") is "1:5" &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='rental_history_type'") is 1L,
                "租赁迁移保留v41晶体选择及原索引");
            string copied = string.Join(',', names.Select(name => name switch
            {
                "\"acceptance_id\"" => "'234567890abcdef1234567890abcdef1'", "\"quest_id\"" => "8581", "\"condition_type\"" => "19", _ => name
            }));
            Execute(db, "INSERT INTO " + table + "(" + string.Join(',', names) + ") SELECT " + copied + " FROM " + table + " WHERE rowid=87");
            Check(Query(db, "SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=8581 AND condition_type=19 AND event_id IS NULL AND plan_snapshot IS NOT NULL") is 1L &&
                Query(db, "SELECT count(*) FROM rental_migration_audit") is 1L,
                "native19使用无清图来源的冻结计划，恢复后的原触发器只观察新事件");
            Reject<SqliteException>(() => Execute(db, "UPDATE character_quest_completion_receipts SET plan_snapshot=NULL WHERE condition_type=19"),
                "native19不允许缺失冻结计划");
            Reject<SqliteException>(() => Execute(db, "UPDATE character_quest_completion_receipts SET event_id='fake',instance_id='fake',task_attempt=1,epoch=1 WHERE condition_type=19"),
                "native19不能伪装清图来源");
            if (oldVersion == 45)
            {
                Reject<SqliteException>(() => Execute(db,"UPDATE character_quest_completion_receipts SET condition_type=45 WHERE rowid=87"),
                    "native45不能沿用仅有冻结计划的普通领奖形状");
                Reject<SqliteException>(() => Execute(db,"UPDATE character_quest_completion_receipts SET condition_type=45,event_id='fixture',instance_id='fixture',task_attempt=1,epoch=1 WHERE rowid=87"),
                    "native45显式拒绝NULL接取外键，不能借SQLite CHECK的NULL结果绕过清图来源");
            }
            Check(Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "租赁迁移保留领奖外键完整性");
        }
        string after = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == after, "租赁迁移重开幂等，不重复领奖或改写回执");
    }

    private static void VerifyCubeContractMigration(string root)
    {
        string path = Path.Combine(root, "cube-v40.db");
        _ = new CharacterRepository(path);
        new AuthenticationService(new AuthenticationRepository(path)).Create("cube-history", "fixture-password");
        using (var db = Open(path))
        {
            RewindQuestRentalCompletion(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                  VALUES('cube-history',0,'CubeHistory',7,38,1,450,234,0,123);
                INSERT INTO account_wallets(account,revision,premium_contracts)
                  VALUES('cube-history',5,'{"84":1800000000,"97":1900000000}');
                INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until) VALUES(1,'cube-history',1000,2000);
                DROP TABLE cube_premium;
                PRAGMA user_version=40;
                """);
        }

        string failed = Path.Combine(root, "cube-v41-rollback.db");
        using (var source = Open(path))
        using (var target = Open(failed))
        {
            source.BackupDatabase(target);
            Execute(target, "ALTER TABLE accounts DROP COLUMN disabled");
        }
        string original = Snapshot(failed);
        RejectDatabase(() => new CharacterRepository(failed));
        Check(Snapshot(failed) == original, "v41末段校验失败回滚晶体表和版本，保留上游清图结构、传送冷却及原资产");

        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "SELECT count(*) FROM cube_premium") is 0L,
                "已发布v40只追加空晶体选择表，不推断旧角色颜色");
            Check(Query(db, "SELECT revision||':'||gold FROM characters WHERE id=1") is "7:123" &&
                Query(db, "SELECT revision||':'||premium_contracts FROM account_wallets WHERE account='cube-history'") is
                    "5:{\"84\":1800000000,\"97\":1900000000}" &&
                Query(db, "SELECT potion_until||':'||aura_until FROM character_teleport_cooldowns WHERE character_id=1") is "1000:2000",
                "晶体迁移保留角色revision、资产、账号期限及上游冷却");
            Execute(db, "INSERT INTO cube_premium VALUES(1,0,5)");
            Reject<SqliteException>(() => Execute(db, "UPDATE cube_premium SET selected=2"), "晶体选择首字段保留0/1约束");
            Reject<SqliteException>(() => Execute(db, "UPDATE cube_premium SET cube_type=6"), "晶体颜色拒绝超出六种类型");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO cube_premium VALUES(2,0,-1)"), "晶体选择不能绑定不存在的角色");
        }
        string upgraded = Snapshot(path);
        _ = new CharacterRepository(path);
        _ = new AuthenticationRepository(path);
        Check(Snapshot(path) == upgraded, "v41重开幂等，保留已存晶体选择和账号期限");
    }

    private static void VerifyQuestEffectsMigration(string root)
    {
        foreach(bool local32 in new[]{false,true})
        {
            string path=Path.Combine(root,local32 ? "quest-effects-local32.db" : "quest-effects-v33.db");
            _=new CharacterRepository(path);
            new AuthenticationService(new AuthenticationRepository(path)).Create("effects-upgrade","fixture-password");
            using(var db=Open(path))
            {
                RemoveAdventureNames(db);
                Execute(db,"INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('effects-upgrade',0,'EffectOld',7,38,1,450,234,0)");
                Execute(db,"INSERT INTO account_wallets(account,revision,rental_points,seria_luck) VALUES('effects-upgrade',11,23,0)");
                if(local32)
                {
                    Execute(db,"""
                        INSERT INTO character_quest_effect_receipts VALUES(1,'00112233445566778899aabbccddeeff',6,7,'[100]','[]');
                        INSERT INTO character_quest_special_reward_claims VALUES(1,100,'00112233445566778899aabbccddeeff');
                        DROP TABLE account_adventure_purchases;
                        ALTER TABLE account_wallets DROP COLUMN seria_luck;
                        DROP INDEX IF EXISTS auction_listings_expiry; DROP INDEX IF EXISTS auction_listings_seller; DROP INDEX IF EXISTS auction_listings_template; DROP TABLE IF EXISTS auction_listings; DROP INDEX IF EXISTS guild_members_grade; DROP TABLE IF EXISTS guild_members; DROP TABLE IF EXISTS guilds; PRAGMA user_version=32;
                        """);
                }
                else Execute(db,"DROP INDEX IF EXISTS auction_listings_expiry; DROP INDEX IF EXISTS auction_listings_seller; DROP INDEX IF EXISTS auction_listings_template; DROP TABLE IF EXISTS auction_listings; DROP INDEX IF EXISTS guild_members_grade; DROP TABLE IF EXISTS guild_members; DROP TABLE IF EXISTS guilds; DROP TABLE character_quest_special_reward_claims; DROP TABLE character_quest_effect_receipts; PRAGMA user_version=33");
            }
            _=new CharacterRepository(path);
            using(var db=Open(path))
            {
                Check(Query(db,"PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion && Query(db,"SELECT revision FROM characters") is 7L &&
                    Query(db,"SELECT revision FROM account_wallets") is 11L && Query(db,"SELECT rental_points FROM account_wallets") is 23L &&
                    Query(db,"SELECT seria_luck FROM account_wallets") is 0L,"正式v33及本地任务v32升级当前版本保留角色与账号资产/revision");
                Check(Query(db,"SELECT count(*) FROM account_adventure_purchases") is 0L &&
                    (long)Query(db,"SELECT count(*) FROM character_quest_effect_receipts")! == (local32 ? 1 : 0) &&
                    (long)Query(db,"SELECT count(*) FROM character_quest_special_reward_claims")! == (local32 ? 1 : 0),
                    "只补空表，已有任务补领回执及领取资格保留");
                if(local32) Check(Query(db,"SELECT claimed_quests FROM character_quest_effect_receipts") is "[100]" &&
                    Query(db,"SELECT quest_id FROM character_quest_special_reward_claims") is 100L,"本地旧32真实任务账本值不重写或重领");
                Check(Query(db,"PRAGMA integrity_check") is "ok" && Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "任务账本升级保留完整外键");
            }
            string saved=Snapshot(path);_=new CharacterRepository(path);
            Check(Snapshot(path)==saved,"升级后反复初始化只读保留完整状态");
        }
        string broken=Path.Combine(root,"quest-effects-v33-failure.db");_=new CharacterRepository(broken);
        using(var db=Open(broken))
        {
            RewindDungeonSceneSettlement(db);
            RewindRaidLifecycle(db);
            Execute(db,"""
                DROP TABLE character_quest_special_reward_claims; DROP TABLE character_quest_effect_receipts;
                DROP INDEX IF EXISTS auction_listings_expiry; DROP INDEX IF EXISTS auction_listings_seller; DROP INDEX IF EXISTS auction_listings_template; DROP TABLE IF EXISTS auction_listings; CREATE TABLE character_quest_special_reward_claims(bad INTEGER); PRAGMA user_version=33;
                """);
        }
        string original=Snapshot(broken);RejectDatabase(()=>new CharacterRepository(broken));
        Check(Snapshot(broken)==original,"末段列校验失败回滚新增任务表与版本，保留原库");
    }

    private static void VerifyQuestSceneClearMigration(string root)
    {
        string path=Path.Combine(root,"scene-clear-v39.db");
        _=new CharacterRepository(path);
        const string eventId="11111111111111111111111111111111",acceptance="22222222222222222222222222222222";
        const string clearColumns="rowid,character_id,account,acceptance_id,quest_id,quest_slot,event_id,instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision";
        string clearRows,completedRows;
        using(var db=Open(path))
        {
            RewindQuestSceneClearSchema(db);
            Execute(db,"INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('scene-history',0,'SceneHistory',8,38,1,450,234,0)");
            Execute(db,$"INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES(1,'scene-history','{eventId}','old-instance',100,1,1,70407,1,1,1,1,'fixture','fixture',1,1,1,1,0,1,7)");
            Execute(db,$"INSERT INTO character_quest_clear_receipts({clearColumns}) VALUES(17,1,'scene-history','{acceptance}',100,0,'{eventId}','old-instance',1,2,191,70407,1,7)");
            Execute(db,$"""
                INSERT INTO character_quest_completion_receipts(rowid,character_id,account,acceptance_id,quest_id,event_id,instance_id,
                  task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,
                  difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,
                  before_revision,revision,rewards,clear_acceptance_id)
                VALUES(29,1,'scene-history','{acceptance}',100,'{eventId}','old-instance',1,2,1,'fixture','fixture','fixture',0,0,0,'fixture',0,0,0,1,1,0,0,7,8,'[]','{acceptance}')
                """);
            RewindRaidLifecycle(db);
            Execute(db,"CREATE TABLE scene_migration_audit(value INTEGER); CREATE INDEX scene_history_map ON character_quest_clear_receipts(map_id); CREATE TRIGGER scene_history_insert AFTER INSERT ON character_quest_clear_receipts BEGIN INSERT INTO scene_migration_audit VALUES(1); END; PRAGMA user_version=39");
            clearRows=JsonSerializer.Serialize(TcpTestFixture.Rows(path,"SELECT "+clearColumns+" FROM character_quest_clear_receipts"));
            completedRows=JsonSerializer.Serialize(TcpTestFixture.Rows(path,"SELECT rowid,* FROM character_quest_completion_receipts"));
        }
        _=new CharacterRepository(path);
        using(var db=Open(path))
        {
            Check(Query(db,"PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db,"SELECT count(*) FROM character_dungeon_scene_clear_receipts") is 0L,
                "v39连续升级到当前版本，保留空场景来源，不补造历史物件清图");
            Check(JsonSerializer.Serialize(TcpTestFixture.Rows(path,"SELECT "+clearColumns+" FROM character_quest_clear_receipts"))==clearRows &&
                JsonSerializer.Serialize(TcpTestFixture.Rows(path,"SELECT rowid,* FROM character_quest_completion_receipts"))==completedRows &&
                Query(db,"SELECT source_type||':'||growth_event_id FROM character_quest_clear_receipts") is string typed && typed=="MonsterDeath:"+eventId,
                "旧清图与完成的原列、rowid和FK保全，event明确绑定原growth");
            Check(Query(db,"SELECT count(*) FROM scene_migration_audit") is 0L && Query(db,"SELECT count(*) FROM sqlite_schema WHERE name IN('scene_history_map','scene_history_insert')") is 2L &&
                Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L,"保全索引触发器而不重放历史业务，完成FK仍完整");
            Reject<SqliteException>(()=>Execute(db,"UPDATE character_quest_clear_receipts SET source_type='PassiveObject'"),
                "旧growth不能只改来源类型就冒充场景物件");
        }
        string stable=Snapshot(path); _=new CharacterRepository(path);
        Check(Snapshot(path)==stable,"v40重开幂等，既有历史凭据不变");

        string failed=Path.Combine(root,"scene-clear-v39-invalid-fk.db");
        using(var source=Open(path)) using(var target=Open(failed)) source.BackupDatabase(target);
        using(var db=Open(failed))
        {
            RewindQuestSceneClearSchema(db);
            RewindRaidLifecycle(db);
            Execute(db,"PRAGMA foreign_keys=OFF; DELETE FROM character_dungeon_growth_receipts; PRAGMA foreign_keys=ON; PRAGMA user_version=39");
        }
        string damaged=Snapshot(failed);
        Reject<InvalidDataException>(()=>new CharacterRepository(failed),"旧历史FK断裂拒绝整次场景来源迁移");
        Check(Snapshot(failed)==damaged,"新增来源表、重建清图与版本在同原事务失败回滚，保留损坏现场");
    }

    private static void VerifyCardClaimUpgrade(string root)
    {
        string path = Path.Combine(root, "cards-v33.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            RewindDungeonSceneSettlement(db);
            Execute(db, """
                INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('history',0,'History',7,38,1,450,234,0);
                INSERT INTO account_card_memberships(account,black_diamond_expires,gold_card_expires,revision) VALUES('history',100,200,3);
                INSERT INTO character_dungeon_settlement_receipts VALUES(1,'history','event','instance',0,1,1,3,1,zeroblob(16),X'01',1,99,0,99,0,'old','old',1,1,0,99,7);
                INSERT INTO character_dungeon_card_rewards(character_id,account,instance_id,plan_json,reward_json,mail_id,revision) VALUES(1,'history','instance','{}','{}',NULL,7);
                """);
            RemoveCardClaimSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "PRAGMA user_version=33; CREATE TABLE server_events(collision INTEGER)");
        }
        string original = Snapshot(path);
        RejectDatabase(() => new CharacterRepository(path));
        Check(Snapshot(path) == original,"v36末条DDL失败回滚领取位及版本，不改变旧资产和回执");
        using (var db = Open(path)) Execute(db, "DROP TABLE server_events");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db,"SELECT claimed FROM character_dungeon_card_rewards") is 1L &&
                Query(db,"SELECT reward_json||':'||revision FROM character_dungeon_card_rewards") is "{}:7",
                "旧已发免费奖励升级为已领取，不改历史结果或再发一次");
            Check(Query(db,"SELECT black_diamond_expires||':'||gold_card_expires||':'||revision FROM account_card_memberships") is "100:200:3" &&
                Query(db,"SELECT revision FROM characters") is 7L && Query(db,"SELECT count(*) FROM server_events") is 0L,
                "保留原会员、角色和revision，活动空表不自动启用");
            Check(Query(db,"SELECT count(*) FROM pragma_table_info('account_card_memberships') WHERE name='returning_player_expires'") is 0L,
                "翻牌升级只建立领取位和活动管理，不新增回归玩家库表字段");
            Check(Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L,"翻牌增量升级保留原外键关系");
            Check(Query(db,"SELECT json_extract(premium_contracts,'$.56')||':'||json_extract(premium_contracts,'$.58') FROM account_wallets WHERE account='history'") is "100:200",
                "原会员56/58期限接管至统一契约账本，旧列只保留迁移历史，不补发奖励");
        }
        string upgraded = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == upgraded,"翻牌增量升级重开幂等，不重复DDL或奖励");
        VerifyLocalCardContractsMigration(root,path);
    }

    private static void VerifyLocalCardContractsMigration(string root, string source)
    {
        foreach (string scenario in new[] { "known-v34", "partial-v34", "rollback-v34" })
        {
            string path = Path.Combine(root,"card-contracts-"+scenario+".db");
            using (var old = Open(source))
            using (var copy = Open(path)) old.BackupDatabase(copy);
            using (var db = Open(path))
            {
                RemoveAdventureNames(db);
                Execute(db,"""
                    DROP TABLE character_quest_special_reward_claims;
                    DROP TABLE character_quest_effect_receipts;
                    ALTER TABLE account_wallets DROP COLUMN premium_contracts;
                    ALTER TABLE account_card_memberships ADD COLUMN returning_player_expires INTEGER NOT NULL DEFAULT 0 CHECK(returning_player_expires>=0);
                    UPDATE account_card_memberships SET returning_player_expires=123;
                    UPDATE character_dungeon_card_rewards SET claimed=0;
                    PRAGMA user_version=34;
                    """);
                if (scenario == "partial-v34")
                    Execute(db,"ALTER TABLE character_dungeon_card_rewards DROP COLUMN claimed");
                if (scenario == "rollback-v34")
                    Execute(db,"CREATE TRIGGER fail_card_contracts AFTER UPDATE ON account_wallets BEGIN SELECT RAISE(ABORT,'contract migration rollback'); END");
            }
            string before = Snapshot(path);
            if (scenario != "known-v34")
            {
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before,"未知旧翻牌v34或末段契约写入失败完整回滚任务表、期限列、资产和版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using var upgraded = Open(path);
            Check(Query(upgraded,"SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')") is 2L &&
                Query(upgraded,"SELECT json_extract(premium_contracts,'$.56')||':'||json_extract(premium_contracts,'$.58') FROM account_wallets WHERE account='history'") is "100:200" &&
                Query(upgraded,"SELECT claimed FROM character_dungeon_card_rewards") is 0L &&
                Query(upgraded,"SELECT revision FROM characters") is 7L &&
                Query(upgraded,"SELECT returning_player_expires FROM account_card_memberships") is 123L &&
                Query(upgraded,"PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "已知旧翻牌v34连续接入上游任务/契约，原未领取状态和角色revision保持");
        }
        foreach (var (name,sql) in new[]
        {
            ("invalid-terms","UPDATE account_wallets SET premium_contracts='{\"27\":\"bad\"}' WHERE account='history'"),
            ("revision-overflow","UPDATE account_wallets SET revision=9223372036854775807,premium_contracts='{}' WHERE account='history'")
        })
        {
            string damaged = Path.Combine(root,"card-contracts-"+name+".db");
            using (var old = Open(source))
            using (var copy = Open(damaged)) old.BackupDatabase(copy);
            using (var db = Open(damaged))
            {
                RemoveCardClaimSchema(db);
                Execute(db,sql);
                RewindRaidLifecycle(db);
                Execute(db,"PRAGMA user_version=35");
            }
            string before = Snapshot(damaged);
            RejectDatabase(() => new CharacterRepository(damaged));
            Check(Snapshot(damaged) == before,"错误契约期限对象或钱包版本溢出拒绝，领取字段、原数据与版本全部回滚");
        }
        string mixed = Path.Combine(root,"card-contracts-upstream-v35.db");
        using (var old = Open(source))
        using (var copy = Open(mixed)) old.BackupDatabase(copy);
        using (var db = Open(mixed))
        {
            RemoveCardClaimSchema(db);
            RewindRaidLifecycle(db);
            Execute(db,"UPDATE account_wallets SET revision=9,premium_contracts='{\"27\":400,\"58\":300}' WHERE account='history'; PRAGMA user_version=35");
            Execute(db,$"INSERT INTO account_currency_balances VALUES('history',{(int)CurrencyKind.Cera},321)");
        }
        _ = new CharacterRepository(mixed);
        using var migrated = Open(mixed);
        Check(Query(migrated,"SELECT revision||':'||json_extract(premium_contracts,'$.27')||':'||json_extract(premium_contracts,'$.56')||':'||json_extract(premium_contracts,'$.58') FROM account_wallets WHERE account='history'") is "10:400:100:300" &&
            Query(migrated,"SELECT balance FROM account_currency_balances WHERE account='history'") is 321L &&
            Query(migrated,"SELECT revision FROM characters") is 7L,
            "上游v35契约与旧会员期限取最大值，其他契约和余额保留，仅真实账号账本变化推进版本");
    }

    private static void VerifyExperienceCapMigration(string root)
    {
        string path = Path.Combine(root,"experience-v22.db"), rollback = Path.Combine(root,"experience-v22-fail.db");
        _ = new CharacterRepository(path);
        string[] tables; string[][] columns; string[] before;
        using (var db = Open(path))
        {
            Execute(db,"INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('xp-old',0,'XpOld',7,38,1,450,234,0)");
            Execute(db,"INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES(1,'xp-old','legacy','legacy-instance',0,1,1,76121,123,99,1,20,'legacy-rule','old-resource',1,1,1,1,40,60,7)");
            AddDropDependencyFixture(db);
            Execute(db,"CREATE TABLE character_dungeon_growth_receipts_extra(character_id INTEGER,event_id TEXT,value TEXT DEFAULT 'character_dungeon_growth_receipts',FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id)); INSERT INTO character_dungeon_growth_receipts_extra(character_id,event_id) VALUES(1,'legacy')");
            RewindExperienceCaps(db);
            RewindAccountQuestSource(db);
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db,"PRAGMA user_version=22");
            // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
            DropLaterTables(db);
            tables = TableNames(db); columns = tables.Select(table => ColumnNames(db,table)).ToArray();
            before = tables.Select((table,index) => Rows(db,table,columns[index])).ToArray();
            using var target = Open(rollback); db.BackupDatabase(target);
        }
        using (var db = Open(rollback)) Execute(db,"CREATE VIEW character_quest_completion_receipts_xp_cap AS SELECT 1 AS value");
        string original = Snapshot(rollback);
        Reject<SqliteException>(() => new CharacterRepository(rollback),"封顶迁移后段撞到视图时整笔失败");
        Check(Snapshot(rollback) == original,"失败回滚已复制父子表、索引、触发器、历史数据和版本");
        string stageBefore = Snapshot(path);
        using (var db = Open(path))
        using (var tx = db.BeginTransaction())
        {
            ExperienceCapSchema.Upgrade(db, tx);
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE name='character_dungeon_growth_receipts'";
            Check(query.ExecuteScalar() is string growthDefinition && growthDefinition.Contains(
                "CHECK(after_experience>=before_experience AND after_experience<=before_experience+amount)", StringComparison.Ordinal),
                "独立v23步骤已接入封顶到账约束");
            query.CommandText = "SELECT \"notnull\" FROM pragma_table_info('account_completed_quests') WHERE name='character_id'";
            Check(query.ExecuteScalar() is 1L, "v23步骤保留旧账号任务来源约束，由后续v24单独接管");
            tx.Rollback();
        }
        Check(Snapshot(path) == stageBefore, "独立v23步骤回滚后仍为完整原v22夹具");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            for (int index = 0; index < tables.Length; index++)
                Check(Rows(db,tables[index],columns[index]) == before[index],"封顶迁移保留原表内容及rowid："+tables[index]);
            Check(Query(db,"PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion && Query(db,"PRAGMA integrity_check") is "ok" &&
                Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "真实v22经v23封顶和v24来源升级完整提交当前版本，动态外键后代保持一致");
            Check(Query(db,"SELECT count(*) FROM sqlite_schema WHERE name IN ('drop_dependency_index','drop_dependency_guard')") is 2L,
                "封顶迁移保留原集合索引与触发器，不重放触发器");
            Check(Query(db,"SELECT dflt_value FROM pragma_table_info('character_dungeon_growth_receipts_extra') WHERE name='value'") is "'character_dungeon_growth_receipts'",
                "外键后代名称含父表前缀时正确替换，字符串默认值保持原样");
            Execute(db,"INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES(1,'xp-old','cap','cap-instance',0,2,2,76121,124,100,1,20,'cap-rule','resource',1,1,95,95,60,60,8)");
            Reject<SqliteException>(() => Execute(db,"UPDATE character_dungeon_growth_receipts SET after_experience=59 WHERE event_id='cap'"),"封顶约束仍拒绝经验倒退");
            Reject<SqliteException>(() => Execute(db,"UPDATE character_dungeon_growth_receipts SET after_experience=81 WHERE event_id='cap'"),"封顶约束仍拒绝超过原冻结额度");
        }
        string applied = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == applied,"封顶及后续迁移完成后重开不重复迁移或改变回执");
    }

    private static void VerifyDropCollectionSchema(string root)
    {
        string path = Path.Combine(root, "drop-collection-v20.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM character_dungeon_drop_batches") is 0L &&
                Query(db, "SELECT count(*) FROM character_dungeon_drop_objects") is 0L &&
                Query(db, "SELECT count(*) FROM character_dungeon_drop_pickups") is 0L,
                "新库直接建立v20完整空集合账本，不补发历史掉落");
            RewindDropCollection(db);
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=17");
            // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
            DropLaterTables(db);
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('drop-old',0,'DropOld',7,38,1,450,234,0,123)");
            // 最后一张表撞到视图，前两张DDL和版本必须在同一升级事务回滚。
            Execute(db, "CREATE VIEW character_dungeon_drop_pickups AS SELECT id FROM characters");
        }
        string beforeUpgrade = Snapshot(path);
        Reject<SqliteException>(() => new CharacterRepository(path), "v20末表建表失败回滚任务升级、前两张集合表和版本");
        Check(Snapshot(path) == beforeUpgrade, "v20失败保留真实v17、旧角色金币和全部原结构");
        using (var db = Open(path)) Execute(db, "DROP VIEW character_dungeon_drop_pickups");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
            Check(Query(db, "PRAGMA user_version") is long upgradedVersion && upgradedVersion == SqliteMigrations.CurrentVersion && Query(db, "SELECT revision||':'||gold FROM characters") is "7:123" &&
                Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "真实v17升级当前版本完成任务结构及空集合表，保留原资产、revision和外键");
        string complete = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == complete, "正式集合结构重开不改变现有基线、版本或资产");
        foreach (var (table, column) in new[]
        {
            ("character_dungeon_drop_pickups", "after_core"),
            ("character_dungeon_drop_objects", "gold_amount"),
            ("character_dungeon_drop_batches", "object_count")
        })
        {
            using var db = Open(path);
            foreach (bool missingTable in new[] { true, false })
            {
                using var tx = db.BeginTransaction();
                SqliteMigrations.Execute(db, tx, missingTable ? "DROP TABLE " + table :
                    "ALTER TABLE " + table + " RENAME COLUMN " + column + " TO fixture_missing_column");
                if (missingTable)
                    Reject<InvalidDataException>(() => SqliteMigrations.ValidateSchema(db, tx), "集合部分表不能通过完整数据库结构校验");
                else
                    Reject<SqliteException>(() => SqliteMigrations.ValidateSchema(db, tx), "集合三表齐全仍拒绝缺失完整数量或拾取核心列");
                tx.Rollback();
                Check(Snapshot(path) == complete, "集合结构拒绝并回滚后保留原完整结构与版本");
            }
        }
        using (var db = Open(path))
        {
            using var tx = db.BeginTransaction();
            foreach (string table in new[] { "character_dungeon_drop_pickups", "character_dungeon_drop_objects", "character_dungeon_drop_batches" })
                SqliteMigrations.Execute(db, tx, "DROP TABLE " + table);
            Reject<InvalidDataException>(() => SqliteMigrations.ValidateSchema(db, tx), "正式v20缺全部集合表也拒绝，不回退可选结构");
            tx.Rollback();
        }
        using (var db = Open(path))
            Execute(db, "DROP TABLE character_dungeon_drop_pickups");
        string incomplete = Snapshot(path);
        Reject<InvalidDataException>(() => new CharacterRepository(path), "重开已有部分集合结构时在启动前拒绝");
        Check(Snapshot(path) == incomplete, "重开拒绝不补造集合表、不修改原数据或版本");
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
            Execute(db, "INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) SELECT character_id,account,'ordinary','new-run',0,task_attempt,epoch,map_id,2,2,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_growth_receipts WHERE rowid=41");
            Execute(db, "INSERT INTO character_dungeon_settlement_receipts(" + SettlementColumns + ") SELECT character_id,account,'ordinary-settlement','new-run',0,task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_settlement_receipts WHERE event_id='old-settlement'");
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
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE character_dungeon_fatigue_receipts; DROP TABLE character_fatigue; DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=14");
            DropLaterTables(db);
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
        TestFiles.Copy(path, rollback);
        using (var db = Open(rollback)) Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
        string before = Snapshot(rollback);
        RejectDatabase(() => new CharacterRepository(rollback));
        Check(Snapshot(rollback) == before, "任务v14末段校验失败回滚版本、父子账本、索引及触发器");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long compatibleVersion && compatibleVersion == SqliteMigrations.CurrentVersion && Query(db, "SELECT revision||':'||quest_cycle_day||':'||equipment_slot_flags FROM characters") is "9:20261009:7" &&
                Query(db, "SELECT daily_period||':'||answer FROM character_completed_quests") is "20261009:2" &&
                Query(db, "SELECT daily_period||':'||answer FROM account_completed_quests") is "20261009:2" &&
                Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "master已发布任务v14升级当前版本不重复建任务结构、不改周期答案、资产或revision");
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
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db,"DROP TABLE character_dungeon_fatigue_receipts; DROP TABLE character_fatigue; DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=14");
            DropLaterTables(db);
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
            Check(Query(db,"PRAGMA user_version") is long currentVersion && currentVersion == SqliteMigrations.CurrentVersion,"旧任务v14按既定迁移顺序升级至当前版本");
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
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db,"DROP TABLE character_dungeon_fatigue_receipts; DROP TABLE character_fatigue; DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=14");
            DropLaterTables(db);
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
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=17");
            DropLaterTables(db);
            Execute(db, "INSERT INTO character_quest_skip_receipts(rowid,character_id,account,acceptance_id,parent_key,question_quest,before_revision,revision,plan_snapshot) VALUES(59,1,'history','34567890abcdef1234567890abcdef12',19,6217,6,7,'{\"Old\":true}')");
            Execute(db, "INSERT INTO character_dungeon_state(character_id,account,next_object_key,last_attempt,capsule_experience,proficiency_level,proficiency_experience) VALUES(1,'history',700,42,123,4,56)");
            Execute(db, "INSERT INTO character_fatigue VALUES(1,'history',7,156,9,149,3,4)");
            Execute(db, "INSERT INTO character_dungeon_fatigue_receipts(rowid,character_id,account,instance_id,task_attempt,map_id,epoch,generation,rule_id,before_revision,revision,before_state,after_state) VALUES(81,1,'history','abcdef1234567890abcdef1234567890',42,76126,9,7,'fatigue-old',6,7,'{\"Used\":6}','{\"Used\":7}')");
            Execute(db, "INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(12345,1,'history','{\"Uid\":12345,\"Legacy\":true}')");
            Execute(db, "INSERT INTO inventory_avatar_details VALUES(12345,'{\"Detail\":true}')");
            Execute(db, "INSERT INTO character_quest_drop_receipts(rowid,character_id,account,event_id,before_revision,revision,plan_snapshot,changed_items) VALUES(73,1,'history','1234567890abcdef1234567890abcdef',6,7,'{\"QuestDrop\":true}','[]')");
            Execute(db, "INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) SELECT character_id,account,'legacy','drop-old-run',quest_id,task_attempt,epoch,map_id,3,3,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_growth_receipts WHERE rowid=41");
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
            Check(Query(db,"PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "master17按顺序执行后续迁移并达到当前版本");
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

    private static void VerifyLegacyDropV18Migration(string root)
    {
        string path = Path.Combine(root, "legacy-drop-v18.db");
        using (var source = Open(Path.Combine(root, "master-quest-v17.db")))
        using (var target = Open(path)) source.BackupDatabase(target);
        string[] tables;
        string[][] columns;
        string[] before;
        using (var db = Open(path))
        {
            // 原掉落v18有完整集合，但任务回执仍是master v17，不能只调整最终版本号。
            RewindQuestReceiptsToV17(db);
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=18");
            // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
            DropLaterTables(db);
            Execute(db, "UPDATE character_dungeon_drop_objects SET core='{\"Uid\":0,\"Attrs\":[9,8,7],\"Legacy\":true}' WHERE ground_key=512");
            Execute(db, "UPDATE character_dungeon_drop_pickups SET item_uid=12345,after_core='{\"Uid\":12345,\"Attrs\":[9,8,7],\"Legacy\":true}' WHERE ground_key=512");
            tables = TableNames(db);
            columns = tables.Select(table => ColumnNames(db, table)).ToArray();
            before = tables.Select((table, index) => Rows(db, table, columns[index])).ToArray();
        }
        string rollback = Path.Combine(root, "legacy-drop-v18-rollback.db");
        using (var source = Open(path))
        using (var target = Open(rollback)) source.BackupDatabase(target);
        using (var db = Open(rollback)) Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
        string original = Snapshot(rollback);
        RejectDatabase(() => new CharacterRepository(rollback));
        Check(Snapshot(rollback) == original, "旧掉落v18后段校验失败时回滚任务重建、集合、全部数据和版本");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "旧掉落分支v18经兼容步骤升级当前版本，不跳过已发布任务v18");
            foreach (var (table, index) in tables.Select((table, index) => (table, index)))
                Check(Rows(db, table, columns[index]) == before[index], "旧掉落v18保留原表所有列、rowid、Core、UID及冻结结果：" + table);
            Check(Query(db, "SELECT count(*) FROM pragma_table_info('character_quest_skip_receipts') WHERE name IN('operation_id','operation_kind')") is 2L &&
                Query(db, "SELECT operation_id=acceptance_id AND operation_kind='QuickClear' FROM character_quest_skip_receipts WHERE rowid=59") is 1L,
                "旧跳过补齐真实操作身份，保留原接取与冻结规划");
            Check(Query(db, "SELECT count(*) FROM character_dungeon_drop_objects") is 1L &&
                Query(db, "SELECT count(*) FROM character_dungeon_drop_pickups") is 1L &&
                Query(db, "SELECT count(*) FROM v18_trigger_calls") is 0L,
                "已冻结和已拾取集合不重建、不补发、不重放历史触发器");
            Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "旧掉落v18兼容升级保持完整性与历史外键");
        }
        string committed = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == committed, "兼容旧掉落v18后再次打开不修改任何回执或资产");
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
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db,"DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=14");
            DropLaterTables(db);
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
            Check(Query(db,"PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "已存在疲劳的任务v14保留master迁移顺序并升级至当前版本");
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

    private static void VerifyMissingFatigueMigration(string root, int sourceVersion)
    {
        string path = Path.Combine(root, $"missing-fatigue-v{sourceVersion}.db");
        _ = new CharacterRepository(path);
        string[] tables; string[][] columns; string[] before;
        using (var db = Open(path))
        {
            Execute(db, """
                INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction) VALUES
                  (1,'legacy',0,'Missing',11,39,5,284,276,0),(2,'legacy',1,'Consumed',12,39,5,284,276,0),
                  (3,'legacy',2,'Unknown',13,39,5,284,276,0),(4,'legacy',3,'ReceiptOnly',14,39,5,284,276,0);
                INSERT INTO character_dungeon_state(character_id,account,next_object_key,last_attempt,
                  capsule_experience,proficiency_level,proficiency_experience) VALUES
                  (1,'legacy',2753,47,123,4,56),(2,'legacy',700,42,123,4,56),(4,'legacy',900,42,123,4,56);
                INSERT INTO character_fatigue VALUES(2,'legacy',7,156,9,149,3,4);
                INSERT INTO character_dungeon_fatigue_receipts(rowid,character_id,account,instance_id,task_attempt,map_id,epoch,generation,
                  rule_id,before_revision,revision,before_state,after_state)
                  VALUES(81,4,'legacy','abcdef1234567890abcdef1234567890',42,16408,9,7,'fatigue-old',6,7,'{"Used":6}','{"Used":7}');
                """);
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindExperienceCaps(db);
            RewindAccountQuestSource(db);
            RewindRaidLifecycle(db);
            Execute(db, $"DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version={sourceVersion}");
            DropLaterTables(db);
            tables = TableNames(db).Where(table => table != "character_fatigue").ToArray();
            columns = tables.Select(table => ColumnNames(db, table)).ToArray();
            before = tables.Select((table, index) => Rows(db, table, columns[index])).ToArray();
        }
        string rollback = Path.Combine(root, $"missing-fatigue-v{sourceVersion}-rollback.db");
        using (var from = Open(path))
        using (var to = Open(rollback)) from.BackupDatabase(to);
        string original = Snapshot(rollback);
        using (var db = Open(rollback))
        {
            Execute(db, "CREATE TEMP TRIGGER reject_fatigue BEFORE INSERT ON character_fatigue BEGIN SELECT RAISE(ABORT,'fatigue failure'); END");
            Reject<SqliteException>(() => SqliteMigrations.Apply(db), "旧角色疲劳补齐写入失败时拒绝升级");
        }
        Check(Snapshot(rollback) == original, $"疲劳补齐失败回滚全部记录和v{sourceVersion}版本");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                $"已有v{sourceVersion}只执行追加迁移并升级到当前版本");
            Check(Query(db, "SELECT used||':'||maximum||':'||used_maximum||':'||remaining||':'||context_auxiliary||':'||selection_auxiliary FROM character_fatigue WHERE character_id=1") is "0:156:0:156:0:0",
                "已有真实副本owner且无扣点历史的旧角色补齐本服初始疲劳");
            Check(Query(db, "SELECT used||':'||maximum||':'||used_maximum||':'||remaining||':'||context_auxiliary||':'||selection_auxiliary FROM character_fatigue WHERE character_id=2") is "7:156:9:149:3:4",
                "已有疲劳的六项字段完整保留，不重置实际余额");
            Check(Query(db, "SELECT count(*) FROM character_fatigue WHERE character_id IN(3,4)") is 0L,
                "未知副本owner或已有扣点历史缺状态的角色不伪造初始值");
            for (int index = 0; index < tables.Length; index++)
                Check(Rows(db, tables[index], columns[index]) == before[index], "疲劳补齐保留其他原表数据、rowid与角色revision：" + tables[index]);
            Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "疲劳补齐后数据库及外键完整");
        }
        string committed = Snapshot(path);
        _ = new CharacterRepository(path);
        _ = new AuthenticationRepository(path);
        Check(Snapshot(path) == committed, "补齐后重开不重复初始化或重置疲劳");
    }

    // v37一步建出拍卖行挂牌表（含保证金列）；两个夹具都恢复到真实旧结构，不以改版本号冒充旧库。
    private static void VerifyAuctionListingSchema(string root)
    {
        Check(SqliteMigrations.CurrentVersion >= 37, "当前连续迁移包含v37拍卖行挂牌与保证金");
        string path = Path.Combine(root, "auction-v36.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='auction_listings'") is 1L &&
                Query(db, "SELECT group_concat(name,'|') FROM (SELECT name FROM sqlite_schema WHERE type='index' AND tbl_name='auction_listings' ORDER BY name)") is "auction_listings_expiry|auction_listings_seller|auction_listings_template",
                "v37新库建立挂牌表及到期、卖家、模板三个索引");
            Check(Query(db, "SELECT count(*) FROM pragma_table_info('auction_listings')") is 16L &&
                Query(db, "SELECT group_concat(name,',') FROM pragma_table_info('auction_listings')") is
                    "listing_id,seller_character_id,seller_name,item_template_id,item_kind,item_count,item_entry,starting_price,buyout_price,current_bid,current_bidder_character_id,state,posted_at_unix,expires_at_unix,settled_at_unix,deposit_gold",
                "v37挂牌表列齐全且顺序为挂牌、卖家、物品、价格、竞价、状态、时间与保证金");
            Check(Query(db, "SELECT count(*) FROM pragma_table_info('auction_listings') WHERE name='listing_id' AND pk=1") is 1L,
                "挂牌id为主键，客户端回传的购买与下架身份稳定");
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='auction_listings' AND sql LIKE '%AUTOINCREMENT%'") is 1L,
                "挂牌id不回用已下架或已过期记录的身份");
            Check(Query(db, "SELECT count(*) FROM pragma_foreign_key_list('auction_listings')") is 0L,
                "挂牌表不引用characters，机器人卖家没有角色行");
        }
        string structure = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == structure, "v37重复初始化不重建挂牌表或索引");
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='auction_listings'") is 1L,
                "重复执行不重复建表");
            Execute(db, "INSERT INTO auction_listings(seller_character_id,seller_name,item_template_id,item_kind,item_count,item_entry,starting_price,buyout_price,current_bid,state,posted_at_unix,expires_at_unix) VALUES(1,'新库卖家',1001,0,1,X'0A0B',150,-1,0,0,1700000000,1700086400)");
            Check(Query(db, "SELECT count(*) FROM auction_listings") is 1L &&
                Query(db, "SELECT last_insert_rowid()") is 1L,
                "新库挂牌表接受纯竞拍的在售记录");
            Check(Query(db, "SELECT deposit_gold FROM auction_listings WHERE listing_id=1") is 0L,
                "★ 不写保证金时默认 0：历史行与种子行没交过钱，到期结算因此不会退钱（不凭空造币）");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO auction_listings(seller_name,item_template_id,item_kind,item_count,item_entry,starting_price,buyout_price,current_bid,state,posted_at_unix,expires_at_unix,deposit_gold) VALUES('负保证金',1001,0,1,X'0A0B',150,200,0,0,1700000000,1700086400,-1)"),
                "挂牌表拒绝负数保证金");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO auction_listings(seller_name,item_template_id,item_kind,item_count,item_entry,starting_price,buyout_price,current_bid,state,posted_at_unix,expires_at_unix) VALUES('越界状态',1001,0,1,X'0A0B',150,200,0,9,1700000000,1700086400)"),
                "挂牌表拒绝0至3以外的状态");
        }

        string upgrade = Path.Combine(root, "auction-v36-upgrade.db");
        _ = new CharacterRepository(upgrade);
        using (var db = Open(upgrade))
        {
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('legacy',0,'AuctionLegacy',7,38,1,450,234,0)");
            Execute(db, "INSERT INTO account_wallets(account,revision,rental_points) VALUES('legacy',5,17)");
        }
        ExecuteOn(upgrade, db =>
        {
            // 恢复真实v36后连续升级；v37的建表、三个索引及保证金断言不依赖最新版本号。
            DropLaterTables(db);
            RemoveAdventureNames(db);
            RewindRaidLifecycle(db);
            Execute(db, "PRAGMA user_version=36");
        });
        Check(Snapshot(upgrade) != Snapshot(path), "v36夹具与v37新库结构不同");
        Check(QueryOn(upgrade, "SELECT count(*) FROM sqlite_schema WHERE name='auction_listings'") is 0L,
            "v36夹具确实缺少挂牌表，不以改版本号冒充旧库");
        _ = new CharacterRepository(upgrade);
        using (var db = Open(upgrade))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "v36连续执行v37及后续迁移，推进到当前版本号");
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND tbl_name='auction_listings'") is 3L,
                "v37升级为旧库补齐到期、卖家、模板三个索引");
            Check(Query(db, "SELECT c.revision||':'||w.revision||':'||w.rental_points FROM characters c JOIN account_wallets w USING(account) WHERE c.account='legacy'") is "7:5:17" &&
                Query(db, "SELECT count(*) FROM characters WHERE account='legacy'") is 1L,
                "v36升级保留既有角色与钱包数据");
            Execute(db, "INSERT INTO auction_listings(seller_character_id,seller_name,item_template_id,item_kind,item_count,item_entry,starting_price,buyout_price,current_bid,state,posted_at_unix,expires_at_unix) VALUES(1,'旧库卖家',2002,0,5,X'0102',800,1500,0,0,1700000000,1700086400)");
            Check(Query(db, "SELECT seller_name||':'||item_count||':'||buyout_price||':'||state||':'||deposit_gold FROM auction_listings WHERE listing_id=1") is "旧库卖家:5:1500:0:0",
                "★ 升级后旧库挂牌表可插入并查询，且保证金默认 0（旧库升级不预置保证金）");
            Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "挂牌表没有外键，升级后数据库仍完整");
        }
        string migrated = Snapshot(upgrade);
        _ = new CharacterRepository(upgrade);
        Check(Snapshot(upgrade) == migrated, "已到当前版本的旧库重开不重复执行建表");
    }

    // v44只追加公会与成员两表；旧库夹具移除实际新增结构，不只回退版本号。
    private static void VerifyGuildSchema(string root)
    {
        string path = Path.Combine(root, "guild-v44.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('guilds','guild_members')") is 2L,
                "v44新库建立公会与成员两表");
            Check(Query(db, "SELECT group_concat(name,',') FROM pragma_table_info('guilds')") is
                "guild_id,name,address,master_character_id,tier,level,experience,public_funds,guild_crystal,notice,memo,created_at_unix",
                "公会表列齐全且顺序为身份、名称、地址、会长、档位等级经验、基金与时间");
            Check(Query(db, "SELECT group_concat(name,',') FROM pragma_table_info('guild_members')") is
                "guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix",
                "成员表列齐全且顺序为公会、角色、职位、贡献、备注与时间");
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='guild_members_grade'") is 1L,
                "成员表按公会与职位建索引，成员列表查询不必全表扫描");
        }
        string structure = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == structure, "v44重复初始化不重建公会表或索引");
        using (var db = Open(path))
        {
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('guild',0,'GuildMaster',7,38,1,450,234,0)");
            Execute(db, "INSERT INTO guilds(name,address,master_character_id,tier,level,experience,public_funds,guild_crystal,notice,memo,created_at_unix) VALUES('BUG公会','bug-guild',1,0,1,0,0,0,'','',1700000000)");
            Execute(db, "INSERT INTO guild_members(guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix) VALUES(1,1,0,0,'',1700000000,0)");
            Check(Query(db, "SELECT count(*) FROM guilds WHERE name='BUG公会'") is 1L && Query(db, "SELECT count(*) FROM guild_members") is 1L,
                "新库接受公会行与会长成员行");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO guilds(name,address,master_character_id) VALUES('BUG公会','other-name',1)"),
                "★ 同名公会必须被唯一约束拒绝：客户端「重复确认」据此返回已被占用");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO guilds(name,address,master_character_id) VALUES('其它公会','bug-guild',1)"),
                "★ 同地址公会必须被唯一约束拒绝：客户端地址查重（0x9D）据此返回已被占用");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO guilds(name,address,master_character_id) VALUES('超过十四个字符的公会名称','long-name',1)"),
                "公会名长度上限 14 由结构兜底（客户端提示：中文7字符或英文14字符）");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO guild_members(guild_id,character_id) VALUES(1,2)"),
                "成员表外键拒绝不存在的角色");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO guild_members(guild_id,character_id) VALUES(2,1)"),
                "成员表外键拒绝不存在的公会");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO guild_members(guild_id,character_id) VALUES(1,1)"),
                "一个角色只能在一个公会：重复入会必须被拒绝");
        }

        string upgrade = Path.Combine(root, "guild-v43-upgrade.db");
        _ = new CharacterRepository(upgrade);
        using (var db = Open(upgrade))
        {
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('legacy',0,'GuildLegacy',7,38,1,450,234,0)");
        }
        ExecuteOn(upgrade, db =>
        {
            // 还原主线v43的实际结构，移除公会及后续拾取结构。
            RewindPickupUids(db);
            Execute(db, "DROP TABLE character_dungeon_passive_pickups");
            DropGuildSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "PRAGMA user_version=43");
        });
        Check(QueryOn(upgrade, "SELECT count(*) FROM sqlite_schema WHERE name='guilds'") is 0L,
            "v43夹具确实缺少公会表，不以改版本号冒充旧库");
        _ = new CharacterRepository(upgrade);
        using (var db = Open(upgrade))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "v43只追加公会与成员结构并推进版本号");
            Check(Query(db, "SELECT count(*) FROM characters WHERE account='legacy'") is 1L,
                "v44升级保留既有角色数据");
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='guild_members_grade'") is 1L,
                "v44升级为旧库补齐成员索引");
            Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "公会两表升级后数据库仍完整");
        }
        string migrated = Snapshot(upgrade);
        _ = new CharacterRepository(upgrade);
        Check(Snapshot(upgrade) == migrated, "公会升级后的旧库重开不重复执行建表");

        string broken = Path.Combine(root, "guild-v43-partial.db");
        _ = new CharacterRepository(broken);
        ExecuteOn(broken, db =>
        {
            RewindPickupUids(db);
            Execute(db, "DROP TABLE character_dungeon_passive_pickups");
            DropGuildSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "CREATE TABLE guilds(bad INTEGER); PRAGMA user_version=43");
        });
        string original = Snapshot(broken);
        RejectDatabase(() => new CharacterRepository(broken));
        Check(Snapshot(broken) == original, "v44拒绝未知公会结构，新增成员表和版本全部回滚");
    }

    static void ChangeCompletionDefinition(SqliteConnection db,Func<string,string> change)
    {
        RewindQuestDungeonAlternatives(db);
        ChangeReceiptDefinition(db, "character_quest_completion_receipts", sql => change(sql.Replace("12,17,19,25", "12,25", StringComparison.Ordinal).Replace("12,19,25", "12,25", StringComparison.Ordinal).Replace(
            "CHECK(after_experience>=before_experience AND after_experience<=before_experience+experience_amount)",
            "CHECK(after_experience=before_experience+experience_amount)", StringComparison.Ordinal)));
    }

    // 旧库夹具同时移除后续拍卖、公会、收集箱和Buff强化结构；子表先于所属表删除。
    static void DropLaterTables(SqliteConnection db)
    {
        Execute(db, "DROP TABLE IF EXISTS character_pvp_daily_missions; DROP TABLE IF EXISTS character_pvp_mission_rewards; DROP TABLE IF EXISTS character_pvp_mission_facts");
        RemoveSkillTreeExpansionSchema(db);
        DropSkillSwitchInventorySchema(db);
        DropCollectBoxSchema(db);
        DropAuctionSchema(db);
        DropGuildSchema(db);
    }

    static void DropSkillSwitchInventorySchema(SqliteConnection db) =>
        Execute(db, "DROP TABLE IF EXISTS character_skill_switch_inventory_items; DROP TABLE IF EXISTS character_skill_switch_inventory");

    static void DropCollectBoxSchema(SqliteConnection db) =>
        Execute(db, "DROP TABLE IF EXISTS character_collectbox_expirations; DROP TABLE IF EXISTS character_collectbox_slots; DROP TABLE IF EXISTS character_collectbox_state");

    internal static void DropQuestStageSchema(SqliteConnection db)
    {
        RewindDailyBoosterUsage(db);
        Execute(db,"DROP TABLE IF EXISTS character_quest_stage_claims; DROP TABLE IF EXISTS character_quest_stage_witnesses");
    }

    static void RewindDailyBoosterUsage(SqliteConnection db)
    {
        RewindPvpRankCompletion(db);
        RemoveSkillTreeExpansionSchema(db);
        if (Query(db, "SELECT count(*) FROM pragma_table_info('characters') WHERE name='daily_booster_usage'") is 1L)
            Execute(db, "ALTER TABLE characters DROP COLUMN daily_booster_usage");
    }

    static void DropAuctionSchema(SqliteConnection db) =>
        Execute(db, "DROP INDEX IF EXISTS auction_listings_expiry; DROP INDEX IF EXISTS auction_listings_seller; DROP INDEX IF EXISTS auction_listings_template; DROP TABLE IF EXISTS auction_listings");

    static void DropGuildSchema(SqliteConnection db)
    {
        RemoveGuildExtensionSchema(db);
        Execute(db, "DROP INDEX IF EXISTS guild_members_grade; DROP TABLE IF EXISTS guild_members; DROP TABLE IF EXISTS guilds");
    }

    static void VerifyReceiptDefinitionFixture(string root)
    {
        string folder = Path.Combine(root, "definition-fixture");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "game.db");
        using var db = Open(path);
        Execute(db, """
            CREATE TABLE fixture_receipts(id INTEGER PRIMARY KEY,value TEXT,omitted TEXT DEFAULT 'fallback');
            CREATE INDEX fixture_value ON fixture_receipts(value);
            CREATE TABLE fixture_audit(id INTEGER);
            CREATE TRIGGER fixture_insert AFTER INSERT ON fixture_receipts
              BEGIN INSERT INTO fixture_audit VALUES(new.id); END;
            INSERT INTO fixture_receipts VALUES(1,'keep','old');
            """);
        string before = Snapshot(path);
        object? schemaVersion = Query(db, "PRAGMA schema_version");
        int calls = 0;
        ChangeReceiptDefinition(db, "fixture_receipts", ddl => { calls++; return ddl; });
        Check(calls == 1, "无变化回退仍只计算一次目标声明");
        Check(Snapshot(path) == before && Equals(Query(db, "PRAGMA schema_version"), schemaVersion),
            "无变化且无列投影的回退不写schema、行、索引或触发器");
        ChangeReceiptDefinition(db, "fixture_receipts", ddl => ddl, ["\"id\"", "\"value\""]);
        Check(Query(db, "SELECT value||':'||omitted FROM fixture_receipts WHERE id=1") is "keep:fallback",
            "显式列投影即使DDL相同也执行重建，不误判为空操作");
        Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN('fixture_value','fixture_insert')") is 2L &&
            Query(db, "SELECT count(*) FROM fixture_audit") is 1L, "投影重建保留索引和触发器，历史行不重放触发器");
        Execute(db, "INSERT INTO fixture_receipts(id,value) VALUES(2,'next')");
        Check(Query(db, "SELECT count(*) FROM fixture_audit") is 2L, "重建恢复的触发器仍观察新的写入");
    }

    static void ChangeReceiptDefinition(SqliteConnection db, string table, Func<string,string> change, string[]? columns = null)
    {
        string ddl = (string)Query(db,"SELECT sql FROM sqlite_schema WHERE name='" + table + "'")!;
        string updated = change(ddl);
        // 嵌套旧版夹具可能重复回退同一声明；无列投影且声明未变时保留原表，不重复复制数据。
        if (columns is null && string.Equals(updated, ddl, StringComparison.Ordinal))
            return;
        var names = columns ?? ColumnNames(db,table);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name='" + table + "' AND type IN('index','trigger') AND sql IS NOT NULL";
        var dependencies = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) dependencies.Add(reader.GetString(0));
        Execute(db,"CREATE TEMP TABLE saved_receipt AS SELECT rowid AS old_rowid,* FROM " + table);
        Execute(db,"DROP TABLE " + table); Execute(db,updated);
        Execute(db,"INSERT INTO " + table + "(rowid," + string.Join(',',names) + ") SELECT old_rowid," + string.Join(',',names) + " FROM temp.saved_receipt");
        Execute(db,"DROP TABLE temp.saved_receipt");
        foreach (string dependency in dependencies) Execute(db,dependency);
    }

    // 按实际v17回执约束及八列定义回退，保留疲劳和普通副本历史，不只改版本号。
    static void RewindQuestReceiptsToV17(SqliteConnection db)
    {
        RemoveEmotionSchema(db);
        RemoveHotkeySchema(db);
        RemoveTutorialSchema(db);
        RewindExperienceCaps(db);
        RewindAccountQuestSource(db);
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

    // #233 验收构建以v47发布强袭表：恢复该库真实结构（有强袭表，缺master v47角色栏、v48装扮、v49魔王列），须补跑v47起并保留强袭行。
    private static void VerifyAcceptanceAssaultV47(string root)
    {
        string path = Path.Combine(root, "acceptance-assault-v47.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            RewindDevilServices(db);
            Execute(db, "ALTER TABLE characters DROP COLUMN avatar_expansion");
            Execute(db, "ALTER TABLE account_wallets DROP COLUMN character_slots");
            RewindRaidLifecycle(db);
            Execute(db, "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'CHECK(slot BETWEEN 0 AND 61)','CHECK(slot BETWEEN 0 AND 31)') WHERE name='characters'; " +
                "UPDATE sqlite_schema SET sql=replace(replace(sql,'CHECK(list_type<>1 OR slot_index<420)','CHECK(list_type<>1 OR slot_index<210)'),'CHECK(slot_index BETWEEN 0 AND 351 OR (list_type=1 AND slot_index BETWEEN 352 AND 419))','CHECK(slot_index BETWEEN 0 AND 351)') WHERE name='character_inventory_slots'; " +
                "PRAGMA writable_schema=OFF; PRAGMA schema_version=9000; PRAGMA user_version=47");
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,experience,gold) VALUES('accept',0,'Accept',3,38,1,450,234,0,2,60,0)");
            Execute(db, "INSERT INTO character_assault_daily VALUES(1,'accept',20000,2,1)");
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='character_slots'") is 1L &&
                Query(db, "SELECT avatar_expansion||':'||devil_usage FROM characters WHERE name='Accept'") is "105:[]" &&
                Query(db, "SELECT clear_count||':'||hidden_count FROM character_assault_daily") is "2:1" &&
                Query(db, "PRAGMA integrity_check") is "ok",
                "验收v47强袭库补跑角色栏/装扮/魔王迁移并保留强袭次数");
    }

    static void BattlePassDeluxe194Renumber(string root)
    {
        string directory = Path.Combine(root, "deluxe-194");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "game.db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Execute(db, "DROP TABLE account_endless_adventure_days; DROP TABLE account_daily_attendance_claims; DROP TABLE account_daily_attendance_days; DROP TABLE account_weekly_attendance; DROP TABLE weekly_attendance_season; PRAGMA user_version=194");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('account_endless_adventure_days','account_daily_attendance_days','account_daily_attendance_claims','weekly_attendance_season','account_weekly_attendance','account_battle_pass_deluxe_orders')") is 6L &&
                    Query(db, "SELECT count(*) FROM account_battle_pass_deluxe_orders") is 0L &&
                    Query(db, "PRAGMA integrity_check") is "ok",
                    "分支v194豪华订单库补跑无尽日额度、日签到与周签到，并保留197订单表");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
    }

    static void RewindReleasedBattlePassTables(SqliteConnection db, int oldVersion)
    {
        // 正式战令186..189、197及在线活动191与周签到196追加这些表；按子表先行移除旧版本尚未发布的空结构。
        (int Version, string Table)[] tables =
        [
            (197, "account_battle_pass_deluxe_orders"),
            (196, "account_weekly_attendance"),
            (196, "weekly_attendance_season"),
            (191, "account_pcroom_timepoint_claims"),
            (191, "account_pcroom_timepoint_segments"),
            (191, "account_pcroom_timepoint_days"),
            (189, "account_battle_pass_cera_orders"),
            (189, "account_battle_pass_cera_consumption"),
            (188, "account_battle_pass_certificate_orders"),
            (187, "account_battle_pass_source_events"),
            (187, "account_battle_pass_mission_claims"),
            (187, "account_battle_pass_mission_progress"),
            (187, "battle_pass_season_rules"),
            (186, "account_battle_pass_claims"),
            (186, "account_battle_pass_seasons")
        ];
        string[] futureTables = [.. tables.Where(row => oldVersion < row.Version).Select(row => row.Table)];
        foreach (string table in futureTables)
        {
            Check(Query(db, "SELECT count(*) FROM " + table) is 0L,
                "已发布旧库夹具只移除未来空战令与在线活动表，不丢弃订单、领取或任务事实：" + table);
        }
        foreach (string table in futureTables) Execute(db, "DROP TABLE " + table);
    }

    private static void VerifyReleasedMigrations(string root)
    {
        // 默认 fresh；显式开启仅复用本轮空库准备，版本范围和全部迁移断言不变。
        bool reuseEmpty = Environment.GetEnvironmentVariable("DFO_SCHEMA_RELEASED_TEMPLATE") == "1";
        long setupStarted = Stopwatch.GetTimestamp();
        var template = reuseEmpty ? new ReleasedDatabaseTemplate(root) : null;
        if (template is not null) VerifyReleasedTemplateContract(template, root);
        double setupMs = Stopwatch.GetElapsedTime(setupStarted).TotalMilliseconds;
        double prepareMs = 0, rewindMs = 0, verifyMs = 0;
        int completedVersions = 0;
        foreach (int oldVersion in Enumerable.Range(1, SqliteMigrations.CurrentVersion - 1))
        {
            string path = Path.Combine(root, $"released-v{oldVersion}.db");
            long phaseStarted = Stopwatch.GetTimestamp();
            template?.CopyTo(path);
            _ = new CharacterRepository(path);
            prepareMs += Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
            phaseStarted = Stopwatch.GetTimestamp();
            using (var db = Open(path))
            {
                RewindReleasedBattlePassTables(db, oldVersion);
                if (oldVersion < 162) RewindFrozenLootSchema(db);
                if (oldVersion < 160) Execute(db, "DROP TABLE character_prey_normal_progress");
                if (oldVersion < 161) Execute(db, "DROP TABLE character_name_history");
                if (oldVersion < 156) Execute(db, "DROP TABLE character_rename_pending");
                if (oldVersion < 153) Execute(db, "DROP TABLE character_ekern_cycle");
                if (oldVersion is 66 or 67 or 77 or 78) RemoveEmptyPromotionFixtureSchema(db);
                RemoveExperienceBonusSchema(db);
                if (oldVersion < 141) Execute(db, "DROP TABLE guild_dungeon_effective_progress");
                if (oldVersion < 140) Execute(db, "DROP TABLE guild_dungeon_reward_receipts");
                if (oldVersion < 139) Execute(db, "DROP TABLE guild_ranking_months; DROP TABLE guild_ranking_party_clears");
                if (oldVersion < 138) Execute(db, "DROP TABLE guild_hongbao_claims; DROP TABLE guild_hongbao_records");
                RewindDailyBoosterUsage(db);
                if (oldVersion < 49) RewindDevilServices(db);
                if (oldVersion < 53) DropQuestStageSchema(db);
                if (oldVersion < 58 && oldVersion >= 46) RewindExpertJobTutorialCompletion(db);
                if (oldVersion < 46) RewindQuestDungeonAlternatives(db);
                if (oldVersion < 45)
                {
                    RewindPickupUids(db);
                    Execute(db, "DROP TABLE character_dungeon_passive_pickups");
                }
                // v70 追加结算经验分项；先回退到 v43 形状，再由 v43 回退处理更早版本。
                if (oldVersion < 67) RewindSettlementBreakdown(db);
                if (oldVersion < 43) RewindDungeonSceneSettlement(db);
                if (oldVersion < 42) RewindQuestRentalCompletion(db);
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,experience,gold) VALUES('migrated',0,'Migrated',7,38,1,450,234,0,2,60,123)");
                Execute(db, "INSERT INTO character_skill_points VALUES(1,0,12,0),(1,1,34,0)");
                Execute(db, "INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES(1,0,3145,1,'1234567890abcdef1234567890abcdef')");
                Execute(db, "INSERT INTO account_wallets(account,revision,rental_points) VALUES('migrated',5,17)");
                if (oldVersion < 35)
                    Execute(db, "ALTER TABLE account_wallets DROP COLUMN premium_contracts");
                if (oldVersion < 34)
                    Execute(db, "DROP TABLE character_quest_special_reward_claims; DROP TABLE character_quest_effect_receipts");
                if (oldVersion >= 3)
                    Execute(db, "INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES(1,'migrated','legacy','legacy-instance',3145,1,1,76121,123,99,1,20,'legacy-rule','legacy-hash',1,1,2,2,40,60,7)");
                // 旧版必须确实缺少新结构；只改版本号会让真实 CREATE/ALTER 迁移撞表或撞列。
                if (oldVersion < 40) RewindQuestSceneClearSchema(db);
                if (oldVersion < 34) RemoveCardClaimSchema(db);
                if (oldVersion < 28) RemoveEmotionSchema(db);
                if (oldVersion < 27) RemoveHotkeySchema(db);
                if (oldVersion < 26) RemoveTutorialSchema(db);
                if (oldVersion < 25) RemoveTitleSchema(db);
                if (oldVersion < 29) RemoveAdventureSchema(db);
                if (oldVersion < 30) RemoveAdventureRepresentatives(db);
                if (oldVersion < 31) RemoveAdventureExpeditions(db);
                if (oldVersion < 32) RemoveAdventurePurchases(db);
                if (oldVersion < 15)
                    Execute(db, "DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue");
                if (oldVersion < 20) RewindDropCollection(db);
                else
                {
                    if (oldVersion < 23) RewindExperienceCaps(db);
                    if (oldVersion == 20) Execute(db, "DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE character_dungeon_card_rewards");
                    else if (oldVersion == 21) RewindPremiumCards(db);
                }
                if (oldVersion == 17) RewindQuestReceiptsToV17(db);
                else if (oldVersion < 17) RewindQuestSystemToV13(db, keepFatigue: oldVersion >= 15);
                // v16已经发布普通0身份，任务系统仍沿v13；不能用正任务限定冒充v16账本。
                if (oldVersion == 16)
                    foreach (string table in new[] { "character_dungeon_growth_receipts", "character_dungeon_settlement_receipts" })
                        ChangeReceiptDefinition(db, table, sql => sql.Replace("CHECK(quest_id>0)", "CHECK(quest_id>=0)", StringComparison.Ordinal));
                if (oldVersion >= 17 && oldVersion < 24) RewindAccountQuestSource(db);
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
                // 拍卖行挂牌表只出现在 v37：模拟 1..36 的旧库都必须把该表与三个索引一起移除
                // （不能用「改版本号」冒充旧库，否则 v37 的 CREATE 会撞表）。
                if (oldVersion < 37) DropAuctionSchema(db);
                if (oldVersion < 44) DropGuildSchema(db);
                if (oldVersion < 54) DropSkillSwitchInventorySchema(db);
                if (oldVersion < 39) Execute(db, "DROP TABLE IF EXISTS character_teleport_cooldowns");
                if (oldVersion < 50) Execute(db, "DROP TABLE IF EXISTS character_assault_daily");
                if (oldVersion < 61) Execute(db, "DROP TABLE IF EXISTS character_crack_daily");
                if (oldVersion < 63) Execute(db, "DROP TABLE IF EXISTS character_growup_change");
                if (oldVersion < 92) RewindTalismanInventory(db);
                if (oldVersion < 93) Execute(db,"DROP TABLE IF EXISTS character_epic_production");
                if (oldVersion < 94) Execute(db,"DROP TABLE IF EXISTS quest_eligibility_mail_dispatches");
                if (oldVersion < 95) Execute(db,"DROP TABLE IF EXISTS character_pvp_training_skills");
                if (oldVersion < 150) Execute(db,"DROP TABLE IF EXISTS character_pvp_mission_rewards; DROP TABLE IF EXISTS character_pvp_mission_facts");
                // 222cd1db 的每日账本迁移在本整合线顺延到 151。
                if (oldVersion < 151) Execute(db,"DROP TABLE IF EXISTS character_pvp_daily_missions");
                if (oldVersion < 96) Execute(db,"DROP TABLE IF EXISTS account_pvp_private_characters");
                if (oldVersion < 97) Execute(db,"DROP TABLE IF EXISTS character_operation_hope_reward_receipts");
                if (oldVersion < 88) Execute(db, "DROP TABLE IF EXISTS character_dungeon_incount");
                if (oldVersion < 89) Execute(db, "DROP TABLE IF EXISTS account_dungeon_incount");
                if (oldVersion < 90) Execute(db, "DROP TABLE character_social_links; DROP TABLE account_social_links; DROP TABLE account_social_requests");
                else if (oldVersion == 90) Execute(db, "DROP TABLE account_social_requests;" + SocialSchema.LegacyRequestSql);
                if (oldVersion < 41) Execute(db, "DROP TABLE IF EXISTS cube_premium");
                if (oldVersion < 38) RemoveAdventureNames(db);
                if (oldVersion < 3)
                    Execute(db, "DROP TABLE character_dungeon_growth_receipts");
                if (oldVersion == 1)
                    Execute(db, "ALTER TABLE account_wallets DROP COLUMN rental_points");
                if (oldVersion < 28) RemoveEmotionSchema(db);
                if (oldVersion < 27) RemoveHotkeySchema(db);
                if (oldVersion < 26) RemoveTutorialSchema(db);
                if (oldVersion < 25) RemoveTitleSchema(db);
                if (oldVersion < 29) RemoveAdventureSchema(db);
                if (oldVersion < 30) RemoveAdventureRepresentatives(db);
                if (oldVersion < 31) RemoveAdventureExpeditions(db);
                if (oldVersion < 32) RemoveAdventurePurchases(db);
                // v69/v71只追加列，v72只追加配置表；旧库必须确实缺列缺表，否则真实ALTER/CREATE会被跳过。
                RemoveAdventureCapsuleWeek(db);
                RemoveAdventureActivity(db);
                RemoveAdventureWeekly(db);
                if (oldVersion < 70) RemoveAdventureLedger(db);
                RewindRaidLifecycle(db);
                // 通用旧夹具helper会删改任务类型；正式179保留V127，180起必须保留完整V180。
                if (oldVersion >= 179)
                    ChangeReceiptDefinition(db, "character_quest_completion_receipts", _ => oldVersion < 180
                        ? SqliteMigrations.QuestCompletionSchemaV127 : SqliteMigrations.QuestCompletionSchemaV180);
                Execute(db, $"PRAGMA user_version={oldVersion}");
                if (oldVersion == 13)
                    AddDropDependencyFixture(db);
                Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_dungeon_state'") is long ownerTables && ownerTables == (oldVersion >= 8 ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_dungeon_growth_receipts'") is long growthTables &&
                    growthTables == (oldVersion >= 3 ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='rental_points'") is long columns &&
                    columns == (oldVersion == 1 ? 0 : 1), "真实旧版模拟按v1/v2/v3保留已发布结构，移除后续副本owner表");
            }
            rewindMs += Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
            phaseStarted = Stopwatch.GetTimestamp();
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
                Check(Query(db, "SELECT count(*) FROM character_ekern_cycle") is 0L,
                    "艾肯迁移只建立空循环进度，不从旧任务或入场记录补造通关");
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
                Check(Query(db, "SELECT premium_contracts FROM account_wallets WHERE account='migrated'") is "{}",
                    "真实旧版缺少契约列时升级为空账本，不补造历史契约");
                Check(Query(db, "SELECT revision||':'||level||':'||experience||':'||gold FROM characters WHERE id=1") is "7:2:60:123" &&
                    Query(db, "SELECT group_concat(context||':'||sp,',') FROM (SELECT context,sp FROM character_skill_points ORDER BY context)") is "0:12,1:34" &&
                    Query(db, "SELECT quest_id||':'||condition_state FROM character_active_main_quests WHERE character_id=1") is "3145:1" &&
                    Query(db, "SELECT revision FROM account_wallets WHERE account='migrated'") is 5L,
                    "迁移不补经验、两树SP或任务，不修改角色与钱包revision");
                string receipt = "INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES(1,'migrated','event','instance',3145,1,1,76121,256,1,1,72,'rule','hash',1,3,2,2,60,132,8)";
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
                    Check(Query(db, "SELECT resource_source FROM character_dungeon_drop_batches") is "原批次" &&
                        Query(db, "SELECT core FROM character_dungeon_drop_objects") is "{}" &&
                        Query(db, "SELECT after_core FROM character_dungeon_drop_pickups") is "{}" &&
                        Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN ('drop_dependency_index','drop_dependency_guard')") is 2L,
                        "普通账本重建保留外部完整集合三层引用、全部行及附属索引触发器");
                    Reject<SqliteException>(() => Execute(db, "UPDATE character_dungeon_drop_objects SET event_id='missing'"),
                        "集合外键在重建及重命名后仍拒绝不存在的原成长来源");
                    Reject<SqliteException>(() => Execute(db, "INSERT INTO character_dungeon_drop_objects SELECT character_id,event_id,instance_id,1,513,NULL,99,100,200,0 FROM character_dungeon_drop_batches WHERE event_id='legacy'"),
                        "集合附属触发器在重建后仍生效");
                }
            }
            string migrated = Snapshot(path);
            _ = new CharacterRepository(path);
            _ = new AuthenticationRepository(path);
            Check(Snapshot(path) == migrated, "真实迁移重开不重建账本或重复迁移");
            verifyMs += Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;
            completedVersions++;
        }
        Console.WriteLine(FormattableString.Invariant($"[schema-released-preparation] mode={(reuseEmpty ? "private-template" : "fresh")} versions={completedVersions} setup_ms={setupMs:F1} prepare_ms={prepareMs:F1} rewind_ms={rewindMs:F1} migration_checks_ms={verifyMs:F1}"));

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
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=1");
            // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
            DropLaterTables(db);
            Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
        }
        string before = Snapshot(rollback);
        Reject<SqliteException>(() => new CharacterRepository(rollback), "真实v2与v3执行后必需列检查失败回滚整链");
        Check(Snapshot(rollback) == before, "真实迁移失败不遗留租赁列、成长账本或版本升级");
        string clearRollback = Path.Combine(root, "clear-upgrade-rollback.db");
        _ = new CharacterRepository(clearRollback);
        using (var db = Open(clearRollback))
        {
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE character_dungeon_settlement_receipts; DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=9; CREATE TABLE character_dungeon_settlement_receipts(collision INTEGER)");
            // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
            DropLaterTables(db);
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,experience,gold) VALUES('keep',0,'ClearKeep',11,38,1,450,234,0,321,456)");
        }
        string clearBefore = Snapshot(clearRollback);
        Reject<SqliteException>(() => new CharacterRepository(clearRollback), "v9通关账本迁移撞表时拒绝启动");
        Check(Snapshot(clearRollback) == clearBefore, "v10迁移失败保留v9版本、结构与原角色资产");
        string questRollback = Path.Combine(root, "quest-upgrade-rollback.db");
        _ = new CharacterRepository(questRollback);
        using (var db = Open(questRollback))
        {
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; PRAGMA user_version=12");
            // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
            DropLaterTables(db);
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

    private static void VerifyPremiumContractsMigration(string root)
    {
        var core = new ItemCore(1106, ItemKind.Consumable, 3) { Marker16 = 77, SortLockFlag = 1 };
        const long gmExpiry = 1_800_000_000;
        const long legacyExpiry = 1_900_000_000;
        foreach (bool legacyColumn in new[] { false, true })
        {
            string path = Path.Combine(root, legacyColumn ? "premium-local-v34.db" : "premium-master-v34.db");
            using (var source = Open(Path.Combine(root, "released-v34.db")))
            using (var target = Open(path))
                source.BackupDatabase(target);
            new AuthenticationService(new AuthenticationRepository(path)).Create("migrated", "fixture-password");
            using (var db = Open(path))
            {
                RemoveCardClaimSchema(db);
                Execute(db, "UPDATE account_wallets SET seria_luck=7 WHERE account='migrated'");
                Execute(db, $"INSERT INTO account_currency_balances VALUES('migrated',{(int)CurrencyKind.Cera},321)");
                Execute(db, $"INSERT INTO account_expert_contracts VALUES('migrated',{gmExpiry})");
                using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(12345,1,'migrated',$core)";
                command.Parameters.AddWithValue("$core", JsonSerializer.Serialize(core));
                command.ExecuteNonQuery();
                Execute(db, "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'migrated',0,65,12345)");
                if (legacyColumn)
                {
                    Execute(db, $"UPDATE account_wallets SET premium_contracts='{{\"27\":{legacyExpiry}}}' WHERE account='migrated'");
                    Execute(db, "DROP TABLE character_quest_special_reward_claims; DROP TABLE character_quest_effect_receipts");
                }
                else
                {
                    Execute(db, "ALTER TABLE account_wallets DROP COLUMN premium_contracts");
                    Execute(db, """
                        INSERT INTO character_quest_effect_receipts VALUES(1,'00112233445566778899aabbccddeeff',6,7,'[100]','[]');
                        INSERT INTO character_quest_special_reward_claims VALUES(1,100,'00112233445566778899aabbccddeeff');
                        """);
                }
                RewindRaidLifecycle(db);
                Execute(db, "PRAGMA user_version=34");
                Check(Query(db, "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='premium_contracts'") is long columns &&
                    columns == (legacyColumn ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')") is long effects &&
                    effects == (legacyColumn ? 0 : 2), "v34夹具分别覆盖主线任务账本及本分支契约账本的明确形状");
            }
            {
                string failed = Path.Combine(root, legacyColumn ? "premium-local-v35-rollback.db" : "premium-master-v35-rollback.db");
                using (var source = Open(path))
                using (var target = Open(failed))
                {
                    source.BackupDatabase(target);
                    Execute(target, "ALTER TABLE accounts DROP COLUMN disabled");
                }
                string before = Snapshot(failed);
                Reject<SqliteException>(() => new CharacterRepository(failed), "v35承接旧结构后末段必需列校验失败拒绝启动");
                Check(Snapshot(failed) == before, "v35晚期失败回滚新增DDL、版本、钱包及物品UID");
                using var failedDb = Open(failed);
                Check(Query(failedDb, "PRAGMA user_version") is 34L &&
                    Query(failedDb, "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='premium_contracts'") is long columns &&
                    columns == (legacyColumn ? 1 : 0) &&
                    Query(failedDb, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')") is long effects &&
                    effects == (legacyColumn ? 0 : 2), "失败库继续保持各自明确的v34原结构");
            }
            if (legacyColumn)
            {
                foreach (bool partialEffects in new[] { false, true })
                {
                    string damaged = Path.Combine(root, partialEffects ? "premium-v34-partial-effects.db" : "premium-v34-unknown-shape.db");
                    using (var source = Open(path))
                    using (var target = Open(damaged))
                    {
                        source.BackupDatabase(target);
                        if (partialEffects)
                        {
                            Execute(target, SqliteMigrations.QuestEffectsSchema);
                            Execute(target, "DROP TABLE character_quest_special_reward_claims");
                        }
                        else
                            Execute(target, "ALTER TABLE account_wallets DROP COLUMN premium_contracts");
                    }
                    string damagedBefore = Snapshot(damaged);
                    RejectDatabase(() => new CharacterRepository(damaged));
                    Check(Snapshot(damaged) == damagedBefore, "未知v34缺列或部分任务表不能冒充本分支契约版并被自动修补");
                }
            }
            foreach (var (name, declaration, value) in new[]
            {
                ("integer", "INTEGER NOT NULL DEFAULT 0", "0"),
                ("nullable", "TEXT DEFAULT '{}'", "'{}'"),
                ("default-array", "TEXT NOT NULL DEFAULT '[]'", "'{}'"),
                ("value-array", "TEXT NOT NULL DEFAULT '{}'", "'[]'")
            })
            {
                string damaged = Path.Combine(root, $"premium-v34-{legacyColumn}-column-{name}.db");
                using (var source = Open(path))
                using (var target = Open(damaged))
                {
                    source.BackupDatabase(target);
                    if (legacyColumn) Execute(target, "ALTER TABLE account_wallets DROP COLUMN premium_contracts");
                    Execute(target, "ALTER TABLE account_wallets ADD COLUMN premium_contracts " + declaration);
                    Execute(target, "UPDATE account_wallets SET premium_contracts=" + value);
                }
                string damagedBefore = Snapshot(damaged);
                RejectDatabase(() => new CharacterRepository(damaged));
                Check(Snapshot(damaged) == damagedBefore, "两种v34均拒绝同名但结构或JSON对象不兼容的契约列，失败完整回滚");
            }
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("migrated", () => { });
            var character = repository.List(owner).Single();
            Check(character.Revision == 7 && character.Inventory.Find(new(InventoryListType.Main, 65)) is { Uid: 12345 } item && item.Core == core &&
                character.Inventory.AccountWallet.Revision == 5 && character.Inventory.AccountWallet.RentalPoints == 17 &&
                character.Inventory.AccountWallet.SeriaLuck == 7 && character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 321 &&
                character.Inventory.AccountWallet.ContractExpiry(27) == (legacyColumn ? legacyExpiry : 0),
                "两种明确v34升级保留角色、钱包、幸运值、旧契约和完整物品UID，不提前续期");
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT expires_at FROM account_expert_contracts WHERE account='migrated'") is gmExpiry &&
                    Query(db, "SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'") is 12345L &&
                    Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "旧GM达人契约和物品分配序号保留，结构版本准确升级");
                Check(Query(db, "SELECT count(*) FROM character_quest_effect_receipts") is long receipts && receipts == (legacyColumn ? 0 : 1) &&
                    Query(db, "SELECT count(*) FROM character_quest_special_reward_claims") is long claims && claims == (legacyColumn ? 0 : 1) &&
                    (legacyColumn || Query(db, "SELECT claimed_quests||':'||changed_items FROM character_quest_effect_receipts") is "[100]:[]" &&
                        Query(db, "SELECT quest_id FROM character_quest_special_reward_claims") is 100L),
                    "主线v34任务效果及资格历史完整保留，本分支v34仅补空账本不补发奖励");
            }
            string upgraded = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == upgraded, "契约升级重开幂等，不重发物品或更改到期时间");
            var after = character.Inventory with { AccountWallet = character.Inventory.AccountWallet.ActivateContract(22, 60,
                DateTimeOffset.FromUnixTimeSeconds(gmExpiry)) };
            repository.CommitInventory(owner, character, new(character.Inventory, after, "premium_migration_test", new(character)));
            var recovered = new CharacterRepository(path).List(owner).Single();
            Check(recovered.Revision == 8 && recovered.Inventory.AccountWallet.Revision == 6 &&
                recovered.Inventory.AccountWallet.ContractExpiry(22) == gmExpiry + 60 &&
                recovered.Inventory.AccountWallet.ContractExpiry(27) == (legacyColumn ? legacyExpiry : 0) &&
                recovered.Inventory.AccountWallet.SeriaLuck == 7 && recovered.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 321 &&
                recovered.Inventory.Find(new(InventoryListType.Main, 65)) is { Uid: 12345 } saved && saved.Core == core,
                "升级后新契约沿库存事务可写并重开恢复，旧资产及期限保持");
        }
    }

    private static void VerifyTitleMigration(string root)
    {
        foreach (bool fail in new[] { false, true })
        {
            string path = Path.Combine(root, "title-v20-" + fail + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveEmotionSchema(db);
                RemoveHotkeySchema(db);
                RemoveTutorialSchema(db);
                RemoveTitleSchema(db);
                RewindRaidLifecycle(db);
                Execute(db, """
                    INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold) VALUES(1,'title-legacy',0,'TitleLegacy',9,38,1,450,234,0,123);
                    INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(87,1,'title-legacy','{"ItemId":26596,"Kind":1,"Value":123,"EnchantCardId":3185}');
                    INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'title-legacy',0,9,87);
                    DROP INDEX IF EXISTS auction_listings_expiry; DROP INDEX IF EXISTS auction_listings_seller; DROP INDEX IF EXISTS auction_listings_template; DROP TABLE IF EXISTS auction_listings; DROP INDEX IF EXISTS guild_members_grade; DROP TABLE IF EXISTS guild_members; DROP TABLE IF EXISTS guilds; PRAGMA user_version=24;
                    """);
                if (fail) Execute(db, "CREATE TABLE character_title_progress(collision INTEGER)");
            }
            string before = Snapshot(path);
            if (fail)
            {
                Reject<SqliteException>(() => new CharacterRepository(path), "称号进度晚期撞表回滚整个v25增量");
                Check(Snapshot(path) == before, "v25失败不留下部分位置表或触发器，也不改变原UID和版本");
            }
            else
            {
                _ = new CharacterRepository(path);
                using var db = Open(path);
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_title_progress") is 0L && Query(db, "SELECT count(*) FROM character_title_book_slots") is 0L,
                    "v24只新增称号空表和互斥约束，不按等级补发历史称号");
                Check(Query(db, "SELECT revision||':'||gold FROM characters") is "9:123" &&
                    Query(db, "SELECT item_uid FROM character_inventory_slots") is 87L &&
                    Query(db, "SELECT json_extract(item_core,'$.EnchantCardId') FROM character_inventory_items") is 3185L,
                    "称号增量不重建旧背包表，原UID、位置、属性与角色revision完整保留");
                string upgraded = Snapshot(path);
                _ = new CharacterRepository(path);
                Check(Snapshot(path) == upgraded, "重开库不再执行称号增量");
            }
        }
    }

    private static void VerifyCollectBoxMigrations(string root)
    {
        string v50 = Path.Combine(root, "collectbox-v50.db");
        _ = new CharacterRepository(v50);
        using (var db = Open(v50))
        {
            DropQuestStageSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                VALUES(1,'collect-v50',0,'CollectV50',7,38,1,450,234,0,321);
                DROP TABLE character_collectbox_expirations;
                DROP TABLE character_collectbox_slots;
                DROP TABLE character_collectbox_state;
                PRAGMA user_version=50;
                """);
        }
        _ = new CharacterRepository(v50);
        using (var db = Open(v50))
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "SELECT revision||':'||gold FROM characters WHERE id=1") is "7:321" &&
                Query(db, "SELECT count(*) FROM character_collectbox_state") is 0L &&
                Query(db, "SELECT count(*) FROM character_collectbox_expirations") is 0L,
                "v50升级只新增v51选择/槽位与v52期限空表，保留旧角色资产");

        string v51 = Path.Combine(root, "collectbox-v51.db");
        _ = new CharacterRepository(v51);
        using (var db = Open(v51))
        {
            DropQuestStageSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction)
                VALUES(1,'collect-v51',0,'CollectV51',9,38,1,450,234,0);
                INSERT INTO character_collectbox_state VALUES(1,'collect-v51',2);
                INSERT INTO character_collectbox_slots VALUES(1,'collect-v51',2,3,104);
                DROP TABLE character_collectbox_expirations;
                PRAGMA user_version=51;
                """);
        }
        _ = new CharacterRepository(v51);
        using (var db = Open(v51))
        {
            Check(Query(db, "SELECT selected_box FROM character_collectbox_state WHERE character_id=1") is 2L &&
                Query(db, "SELECT box_index||':'||slot_index||':'||item_id FROM character_collectbox_slots") is "2:3:104" &&
                Query(db, "SELECT count(*) FROM character_collectbox_expirations") is 0L,
                "v52追加期限表不重写v51选择与物理模板槽");
            Execute(db, "DELETE FROM characters WHERE id=1");
            Check(Query(db, "SELECT count(*) FROM character_collectbox_state") is 0L &&
                Query(db, "SELECT count(*) FROM character_collectbox_slots") is 0L &&
                Query(db, "SELECT count(*) FROM character_collectbox_expirations") is 0L,
                "角色删除由新表ON DELETE CASCADE清理全部收集箱状态");
        }

        string v50Failure = Path.Combine(root, "collectbox-v50-failure.db");
        _ = new CharacterRepository(v50Failure);
        using (var db = Open(v50Failure))
        {
            DropQuestStageSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction)
                VALUES(1,'collect-fail',0,'CollectFail',11,38,1,450,234,0);
                DROP TABLE character_collectbox_expirations;
                DROP TABLE character_collectbox_slots;
                DROP TABLE character_collectbox_state;
                CREATE VIEW character_collectbox_slots AS SELECT id AS character_id FROM characters;
                PRAGMA user_version=50;
                """);
        }
        string before = Snapshot(v50Failure);
        Reject<SqliteException>(() => new CharacterRepository(v50Failure), "v51第二张表撞名");
        using (var db = Open(v50Failure))
            Check(Snapshot(v50Failure) == before && Query(db, "PRAGMA user_version") is 50L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_collectbox_state'") is 0L &&
                Query(db, "SELECT revision FROM characters WHERE id=1") is 11L,
                "v51迁移失败回滚前置DDL并保留旧角色与版本");

        string v51Failure = Path.Combine(root, "collectbox-v51-failure.db");
        _ = new CharacterRepository(v51Failure);
        using (var db = Open(v51Failure))
        {
            DropQuestStageSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction)
                VALUES(1,'expiry-fail',0,'ExpiryFail',13,38,1,450,234,0);
                INSERT INTO character_collectbox_state VALUES(1,'expiry-fail',2);
                INSERT INTO character_collectbox_slots VALUES(1,'expiry-fail',2,3,104);
                DROP TABLE character_collectbox_expirations;
                CREATE VIEW character_collectbox_expirations AS SELECT id AS character_id FROM characters;
                PRAGMA user_version=51;
                """);
        }
        before = Snapshot(v51Failure);
        Reject<SqliteException>(() => new CharacterRepository(v51Failure), "v52期限表撞名");
        Check(Snapshot(v51Failure) == before, "v52迁移失败保留v51选择、模板槽、角色数据和版本");
    }

    private static void VerifyQuestStageMigrations(string root)
    {
        foreach (bool fail in new[] { false,true })
        {
            string path = Path.Combine(root,"quest-stage-v52-" + fail + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                DropQuestStageSchema(db);
                RewindRaidLifecycle(db);
                Execute(db,"PRAGMA user_version=52; INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('stage-old',0,'StageOld',7,38,1,450,234,0,321)");
                if (fail) Execute(db,"CREATE VIEW character_quest_stage_claims AS SELECT 1 AS collision");
            }
            string before = Snapshot(path);
            if (fail)
            {
                Reject<SqliteException>(() => new CharacterRepository(path),"v53第二表撞名整笔迁移回滚");
                Check(Snapshot(path) == before,"v53失败不留witness半表、不改旧资产/版本/结构");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db,"PRAGMA user_version") is long stageVersion && stageVersion == SqliteMigrations.CurrentVersion && Query(db,"SELECT revision||':'||gold FROM characters") is "7:321" &&
                    Query(db,"SELECT count(*) FROM character_quest_stage_witnesses") is 0L && Query(db,"SELECT count(*) FROM character_quest_stage_claims") is 0L &&
                    Query(db,"SELECT count(*) FROM pragma_index_list('character_quest_stage_claims') WHERE [unique]=1") is 2L,
                    "v52追加空阶段旁表与双唯一，不猜旧来源或改旧角色");
            string upgraded = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == upgraded,"v53重开逐表字节保持，不重跑迁移");
            using (var db = Open(path)) Execute(db,$"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
            string newer = Snapshot(path);
            Reject<InvalidDataException>(() => new CharacterRepository(path),"阶段仓储拒绝较新版本");
            Check(Snapshot(path) == newer,"较新版本拒绝不写库");
        }
    }

    private static void VerifySkillSwitchInventoryMigration(string root)
    {
        string v53 = Path.Combine(root, "skill-switch-v53.db");
        _ = new CharacterRepository(v53);
        using (var db = Open(v53))
        {
            RewindDailyBoosterUsage(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                VALUES(1,'skill-switch-v53',0,'SkillSwitchV53',17,38,1,450,234,0,456);
                INSERT INTO character_inventory_items(item_uid,character_id,account,item_core)
                VALUES(12345,1,'skill-switch-v53','{"ItemId":1001,"Kind":1,"Value":1}');
                INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid)
                VALUES(1,'skill-switch-v53',0,9,12345);
                DROP TABLE character_skill_switch_inventory_items;
                DROP TABLE character_skill_switch_inventory;
                PRAGMA user_version=53;
                """);
        }
        _ = new CharacterRepository(v53);
        using (var db = Open(v53))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "SELECT revision||':'||gold FROM characters WHERE id=1") is "17:456" &&
                Query(db, "SELECT item_uid FROM character_inventory_slots WHERE character_id=1") is 12345L &&
                Query(db, "SELECT count(*) FROM character_skill_switch_inventory") is 0L,
                "v53升级只新增空Buff强化表，保留旧角色与库存实例");
            Execute(db, """
                INSERT INTO character_skill_switch_inventory(character_id,account,skill_id)
                VALUES(1,'skill-switch-v53',77);
                INSERT INTO character_skill_switch_inventory_items(character_id,equipment_part,item_uid,item_id)
                VALUES(1,4,12345,1001);
                """);
            Reject<SqliteException>(() => Execute(db, """
                INSERT INTO character_skill_switch_inventory_items(character_id,equipment_part,item_uid,item_id)
                VALUES(1,36,12345,1001)
                """), "Buff强化持久化拒绝越界部位");
            Reject<SqliteException>(() => Execute(db, """
                INSERT INTO character_skill_switch_inventory_items(character_id,equipment_part,item_uid,item_id)
                VALUES(1,5,12345,1001)
                """), "Buff强化持久化拒绝同角色重复实例绑定");
            Execute(db, "DELETE FROM character_inventory_items WHERE item_uid=12345");
            Check(Query(db, "SELECT count(*) FROM character_skill_switch_inventory_items") is 0L &&
                Query(db, "SELECT skill_id FROM character_skill_switch_inventory WHERE character_id=1") is 77L,
                "删除库存实例级联清理绑定但保留指定技能");
            Execute(db, """
                INSERT INTO character_inventory_items(item_uid,character_id,account,item_core)
                VALUES(12345,1,'skill-switch-v53','{"ItemId":1001,"Kind":1,"Value":1}');
                INSERT INTO character_skill_switch_inventory_items(character_id,equipment_part,item_uid,item_id)
                VALUES(1,4,12345,1001);
                """);
            Reject<SqliteException>(() => Execute(db, "DELETE FROM characters WHERE id=1"),
                "未清理私人物品时，既有库存外键仍拒绝裸删角色");
            Check(Query(db, "SELECT item_uid FROM character_inventory_items WHERE character_id=1") is 12345L &&
                Query(db, "SELECT count(*) FROM character_skill_switch_inventory_items") is 1L &&
                Query(db, "SELECT skill_id FROM character_skill_switch_inventory WHERE character_id=1") is 77L,
                "裸删角色失败回滚级联操作，保留库存及Buff强化绑定");
            Execute(db, """
                UPDATE character_inventory_items SET character_id=NULL WHERE item_uid=12345;
                DELETE FROM characters WHERE id=1;
                """);
            Check(Query(db, "SELECT count(*) FROM character_skill_switch_inventory") is 0L &&
                Query(db, "SELECT count(*) FROM character_skill_switch_inventory_items") is 0L &&
                Query(db, "SELECT count(*) FROM character_inventory_items WHERE item_uid=12345 AND character_id IS NULL") is 1L,
                "删除角色级联清理Buff强化指定技能与全部绑定，保留账号实例");
        }

        string failure = Path.Combine(root, "skill-switch-v53-failure.db");
        _ = new CharacterRepository(failure);
        using (var db = Open(failure))
        {
            RewindDailyBoosterUsage(db);
            RewindRaidLifecycle(db);
            Execute(db, """
                INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction)
                VALUES(1,'skill-switch-fail',0,'SkillSwitchFail',19,38,1,450,234,0);
                DROP TABLE character_skill_switch_inventory_items;
                DROP TABLE character_skill_switch_inventory;
                CREATE VIEW character_skill_switch_inventory_items AS SELECT id AS character_id FROM characters;
                PRAGMA user_version=53;
                """);
        }
        string before = Snapshot(failure);
        Reject<SqliteException>(() => new CharacterRepository(failure), "v54第二张Buff强化表撞名");
        using (var db = Open(failure))
            Check(Snapshot(failure) == before && Query(db, "PRAGMA user_version") is 53L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_skill_switch_inventory'") is 0L &&
                Query(db, "SELECT revision FROM characters WHERE id=1") is 19L,
                "v54迁移失败回滚前置DDL并保留旧角色、版本与冲突现场");
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
        Execute(db,"DROP TABLE IF EXISTS character_fatigue_reset_days; DROP TABLE IF EXISTS character_server_item_usage; DROP TABLE IF EXISTS account_server_item_refills");
        RemoveEmotionSchema(db);
        RemoveHotkeySchema(db);
        RemoveTutorialSchema(db);
        RemoveTitleSchema(db);
        RewindDropCollection(db);
        if (!keepFatigue)
            Execute(db, "DROP TABLE IF EXISTS character_dungeon_fatigue_receipts; DROP TABLE IF EXISTS character_fatigue");
        // 已发布v13没有拍卖行挂牌表；回退到该版本时一并移除，避免真实v14/v17库带表执行后续DDL。
        DropLaterTables(db);
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
        RemoveEmotionSchema(db);
        RemoveHotkeySchema(db);
        RemoveTutorialSchema(db);
        RemoveTitleSchema(db);
        RewindRaidLifecycle(db);
        Execute(db, "PRAGMA user_version=13; PRAGMA foreign_keys=ON");
        // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
        DropLaterTables(db);
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
        RemoveEmotionSchema(db);
        RemoveHotkeySchema(db);
        RemoveTutorialSchema(db);
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

    private static void VerifyAccountQuestSourceMigration(string root)
    {
        string path = Path.Combine(root, "account-quest-source-v23.db");
        _ = new CharacterRepository(path);
        string originalRow;
        using (var db = Open(path))
        {
            Execute(db, """
                INSERT INTO accounts VALUES('quest-source',X'01',X'02',1,0,7);
                INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction)
                  VALUES(41,'quest-source',3,'Source',9,38,1,450,234,0);
                INSERT INTO account_completed_quests(rowid,account,quest_id,character_id,acceptance_id,daily_period,answer)
                  VALUES(87,'quest-source',29998,41,'1234567890abcdef1234567890abcdef',20261009,2);
                ALTER TABLE account_completed_quests ADD COLUMN source_marker TEXT NOT NULL DEFAULT 'extension';
                CREATE INDEX account_quest_source_index ON account_completed_quests(character_id,source_marker);
                CREATE TRIGGER account_quest_source_guard BEFORE INSERT ON account_completed_quests
                  WHEN NEW.quest_id=29999 BEGIN SELECT RAISE(ABORT,'source guard'); END;
                """);
            RewindAccountQuestSource(db);
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RemoveTitleSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "PRAGMA user_version=23");
            // 挂牌表与公会两表只由 v37/v38 建出：回退到更早版本的夹具必须把它们一起移除。
            DropLaterTables(db);
            originalRow = Rows(db, "account_completed_quests", ColumnNames(db, "account_completed_quests"));
            Check(Query(db, "SELECT \"notnull\" FROM pragma_table_info('account_completed_quests') WHERE name='character_id'") is 1L,
                "v23夹具保留来源角色非空约束");
        }
        foreach (bool lateFailure in new[] { false, true })
        {
            string failed = Path.Combine(root, "account-source-failure-" + lateFailure + ".db");
            using (var from = Open(path))
            using (var to = Open(failed))
            {
                from.BackupDatabase(to);
                Execute(to, lateFailure ? "ALTER TABLE accounts DROP COLUMN disabled" :
                    "CREATE VIEW account_completed_quests_source AS SELECT 1");
            }
            string before = Snapshot(failed);
            RejectDatabase(() => new CharacterRepository(failed));
            Check(Snapshot(failed) == before, "来源迁移早期DDL或末段结构失败回滚全部原行、DDL、索引、触发器与版本");
        }
        foreach (string shape in new[] { "type", "nullable", "primary", "source-check", "foreign-key", "dependent" })
        {
            string rejected = Path.Combine(root, "account-source-unknown-" + shape + ".db");
            using (var from = Open(path))
            using (var to = Open(rejected))
            {
                from.BackupDatabase(to);
                if (shape == "dependent") Execute(to, """
                    CREATE TABLE account_source_extension(account TEXT,quest_id INTEGER,
                      FOREIGN KEY(account,quest_id) REFERENCES account_completed_quests(account,quest_id) ON DELETE CASCADE);
                    INSERT INTO account_source_extension VALUES('quest-source',29998);
                    """);
                else ChangeReceiptDefinition(to, "account_completed_quests", sql => shape switch
                {
                    "type" => sql.Replace("character_id INTEGER NOT NULL,", "character_id TEXT NOT NULL,", StringComparison.Ordinal),
                    "nullable" => sql.Replace("account TEXT NOT NULL REFERENCES", "account TEXT REFERENCES", StringComparison.Ordinal),
                    "primary" => sql.Replace("PRIMARY KEY(account,quest_id)", "PRIMARY KEY(quest_id,account)", StringComparison.Ordinal),
                    "source-check" => sql.Replace("PRIMARY KEY(account,quest_id),", "CHECK(character_id IS NOT NULL),PRIMARY KEY(account,quest_id),", StringComparison.Ordinal),
                    "foreign-key" => sql.Replace("REFERENCES characters(id,account)", "REFERENCES characters(id,account) ON DELETE CASCADE", StringComparison.Ordinal),
                    _ => throw new InvalidOperationException("未知来源测试形状")
                });
            }
            string before = Snapshot(rejected);
            Reject<InvalidDataException>(() => new CharacterRepository(rejected), "未知来源定义或外键扩展拒绝自动重建");
            Check(Snapshot(rejected) == before, "未知扩展拒绝后不级联删除子行或改写来源、结构与版本");
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "SELECT \"notnull\" FROM pragma_table_info('account_completed_quests') WHERE name='character_id'") is 0L &&
                Rows(db, "account_completed_quests", ColumnNames(db, "account_completed_quests")) == originalRow,
                "v24仅放宽来源为空并保留原rowid、来源、回答、周期和接取身份");
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN('account_quest_source_index','account_quest_source_guard')") is 2L,
                "来源迁移保留原索引和触发器");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO account_completed_quests(account,quest_id,character_id,acceptance_id,daily_period,answer) VALUES('quest-source',29999,41,'abcdef1234567890abcdef1234567890',0,NULL)"),
                "来源迁移后历史触发器仍有效");
            Execute(db, "UPDATE account_completed_quests SET character_id=NULL; DELETE FROM characters WHERE id=41");
            Check(Query(db, "SELECT character_id IS NULL FROM account_completed_quests") is 1L &&
                Query(db, "SELECT acceptance_id||':'||daily_period||':'||answer FROM account_completed_quests") is "1234567890abcdef1234567890abcdef:20261009:2" &&
                Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "删除最后来源角色仍保留账号任务事实且外键完整");
        }
        string committed = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == committed, "来源可空迁移重开不改写或补发历史任务");
    }

    internal static void RewindAccountQuestSource(SqliteConnection db)
    {
        if (Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='account_completed_quests'") is 1L)
            ChangeReceiptDefinition(db, "account_completed_quests",
                sql => sql.Replace("character_id INTEGER,", "character_id INTEGER NOT NULL,", StringComparison.Ordinal));
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        // 临时迁移夹具只验结构与数据，不验掉电持久性：关闭逐条自动提交的刷盘，回退脚本上百条 DDL 不再各等一次 fsync。
        // 被测迁移仍走仓储自己的连接与同步级别。
        using (var command = db.CreateCommand()) { command.CommandText = "PRAGMA synchronous=OFF"; command.ExecuteNonQuery(); }
        return db;
    }

    internal static void RemoveEmotionSchema(SqliteConnection db)
    {
        RemoveAdventureSchema(db);
        if (Query(db, "SELECT count(*) FROM pragma_table_info('characters') WHERE name='emotion'") is 1L)
            Execute(db, "ALTER TABLE characters DROP COLUMN emotion");
    }

    internal static void RemoveHotkeySchema(SqliteConnection db) => Execute(db, "DROP TABLE IF EXISTS account_hotkey_settings");

    internal static void RemoveTutorialSchema(SqliteConnection db) => Execute(db,
        "DROP TABLE IF EXISTS character_tutorial_receipts; DROP TABLE IF EXISTS character_tutorial_state");

    private static void VerifyTutorialSchema(string root)
    {
        string path = Path.Combine(root, "tutorial-v25.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM character_tutorial_state") is 0L &&
                Query(db, "SELECT count(*) FROM character_tutorial_receipts") is 0L, "新库创建空训练状态和回执，不携带玩家数据");
            RemoveEmotionSchema(db);
            RemoveHotkeySchema(db);
            RemoveTutorialSchema(db);
            RewindRaidLifecycle(db);
            Execute(db, "PRAGMA user_version=25");
            DropLaterTables(db);
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,gold) VALUES('old',0,'OldLevel1',7,38,1,450,234,0,1,123)");
            Execute(db, "INSERT INTO character_title_progress VALUES(1,'old',6501,1,2,3,0); INSERT INTO character_title_sections VALUES(1,'old',0,1)");
            Execute(db, "CREATE VIEW character_tutorial_receipts AS SELECT id FROM characters");
        }
        string previous = Snapshot(path);
        Reject<SqliteException>(() => new CharacterRepository(path), "训练第二张表失败回滚第一张表和版本");
        Check(Snapshot(path) == previous, "v26失败保留真实v25、角色资产和revision");
        using (var db = Open(path)) Execute(db, "DROP VIEW character_tutorial_receipts");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "SELECT revision||':'||level||':'||gold FROM characters") is "7:1:123" &&
                Query(db, "SELECT count(*) FROM character_tutorial_state") is 0L, "v25增量新增训练两表，存量Lv1不自动补训练标记");
            Check(Query(db, "SELECT remaining1||':'||remaining2||':'||remaining3 FROM character_title_progress") is "1:2:3" &&
                Query(db, "SELECT revision FROM character_title_sections") is 1L, "v26迁移保留v25称号进度和章节奖励记录");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO character_tutorial_state VALUES(1,'another',7115,0,0)"), "训练状态不能归属另一账号");
            Reject<SqliteException>(() => Execute(db, "INSERT INTO character_tutorial_state VALUES(1,'old',7115,2,0)"), "训练状态只接受布尔完成标记");
            Check(Query(db, "PRAGMA integrity_check") is "ok" &&
                Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L, "训练迁移完整性和外键正确");
        }
        string after = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == after, "训练增量升级重开幂等，不修改旧角色");
    }
    private static void RewindDropCollection(SqliteConnection db)
    {
        RemoveEmotionSchema(db);
        RemoveHotkeySchema(db);
        RemoveTutorialSchema(db);
        RewindExperienceCaps(db);
        RewindAccountQuestSource(db);
        Execute(db,"DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE IF EXISTS character_dungeon_card_rewards; DROP TABLE IF EXISTS character_dungeon_drop_pickups; DROP TABLE IF EXISTS character_dungeon_drop_objects; DROP TABLE IF EXISTS character_dungeon_drop_batches");
    }

    // 老版本夹具恢复当时的严格到账约束，不能仅把v23的版本号改低。
    internal static void RewindExperienceCaps(SqliteConnection db)
    {
        RewindDungeonSceneSettlement(db);
        bool foreignKeys = Query(db,"PRAGMA foreign_keys") is 1L;
        Execute(db,"PRAGMA foreign_keys=OFF");
        try
        {
            foreach (var (table, amount) in new[] { ("character_dungeon_growth_receipts","amount"),
                ("character_dungeon_settlement_receipts","total_experience"),("character_quest_completion_receipts","experience_amount") })
                if (Query(db,"SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='"+table+"'") is 1L)
                    ChangeReceiptDefinition(db,table,sql => sql.Replace(
                        $"CHECK(after_experience>=before_experience AND after_experience<=before_experience+{amount})",
                        $"CHECK(after_experience=before_experience+{amount})",StringComparison.Ordinal));
        }
        finally { if (foreignKeys) Execute(db,"PRAGMA foreign_keys=ON"); }
    }

    private static void RewindPremiumCards(SqliteConnection db) => Execute(db,
        "DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships");

    // 使用真实集合DDL与明确合成行，验证旧版已有集合随成长父表迁移完整保留。
    private static void AddDropDependencyFixture(SqliteConnection db)
    {
        RewindObjectKeys(db);
        using (var tx = db.BeginTransaction())
        {
            DungeonDropCollectionSchema.Upgrade(db, tx);
            tx.Commit();
        }
        // 集合依赖夹具使用真实旧版拾取定义，不能保留v162列及碎片分支。
        const string pickups = "character_dungeon_drop_pickups";
        string[] columns = ColumnNames(db, pickups).Where(name => name is not "\"receipt_format\"" and not "\"destination_list\"" and not "\"epic_piece\"" and not "\"account_material\"").ToArray();
        ChangeReceiptDefinition(db, pickups, _ => DungeonDropCollectionSchema.PreviousPickupDefinition, columns);
        Execute(db, """
            CREATE INDEX drop_dependency_index ON character_dungeon_drop_objects(core);
            CREATE TRIGGER drop_dependency_guard BEFORE INSERT ON character_dungeon_drop_objects
              WHEN NEW.gold_amount=99 BEGIN SELECT RAISE(ABORT,'集合测试触发器'); END;
            INSERT INTO character_dungeon_drop_batches(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,object_count)
              SELECT character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,'legacy-rule','原批次',1
              FROM character_dungeon_growth_receipts WHERE character_id=1 AND event_id='legacy';
            INSERT INTO character_dungeon_drop_objects VALUES(1,'legacy',0,512,'{}',0,100,200,0);
            INSERT INTO character_dungeon_drop_pickups(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision)
              SELECT character_id,account,event_id,512,instance_id,task_attempt,epoch,177,1,'{}',NULL,1,NULL,NULL,8
              FROM character_dungeon_drop_batches WHERE character_id=1 AND event_id='legacy';
            """);
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

    // 夹具在独立连接上还原旧版本结构与版本号，不进入正常初始化路径。
    private static void ExecuteOn(string path, Action<SqliteConnection> change)
    {
        using var db = Open(path);
        change(db);
    }

    private static object? QueryOn(string path, string sql)
    {
        using var db = Open(path);
        return Query(db, sql);
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
