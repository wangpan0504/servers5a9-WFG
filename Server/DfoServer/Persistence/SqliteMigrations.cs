using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal readonly record struct SqliteMigration(int Version, string Name, Action<SqliteConnection, SqliteTransaction> Upgrade);

internal static class SqliteMigrations
{
    public const string BaselineId = "s5a9-database-v1";
    public const int BaselineVersion = 1;
    // 首次发布直接建立完整 v1。后续结构变化从 v2 起连续追加，并同步更新完整 SQL。
    private static readonly SqliteMigration[] Steps =
    [
        new(2, "account_wallet_rental_points", static (db, tx) =>
            Execute(db, tx, "ALTER TABLE account_wallets ADD COLUMN rental_points INTEGER NOT NULL DEFAULT 0 CHECK(rental_points BETWEEN 0 AND 2147483647)")),
        new(3, "副本成长事件账本", static (db, tx) => Execute(db, tx, DungeonGrowthSchema)),
        new(4, "mailbox", static (db, tx) =>
        {
            foreach (string statement in MailboxSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, statement);
        }),
        new(5, "成长奖励资源来源", static (db, tx) =>
            Execute(db, tx, "ALTER TABLE character_dungeon_growth_receipts RENAME COLUMN resource_sha256 TO resource_source")),
        new(6, "客户端设置", static (db, tx) =>
        {
            foreach (string statement in ClientSettingsSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, statement);
        }),
        new(7, "角色主设置快照", static (db, tx) => Execute(db, tx, CharacterGameSettingsSchema)),
        new(8, "角色副本身份及成长附属owner", static (db, tx) => Execute(db, tx, DungeonOwnerSchema)),
        new(9, "副本冻结掉落与拾取账本", static (db, tx) =>
        {
            Execute(db, tx, DungeonLootSchema);
            Execute(db, tx, DungeonPickupSchema);
        }),
        new(10, "副本通关经验与冻结报告账本", static (db, tx) => Execute(db, tx, DungeonSettlementSchema)),
        new(11, "技能额外SP与达人契约来源", static (db, tx) =>
        {
            Execute(db, tx, SkillPointSourceSchema);
            Execute(db, tx, ExpertContractSchema);
        }),
        new(12, "TP来源、技能指令和连招", static (db, tx) =>
        {
            foreach (string sql in SkillMetadataSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, sql);
        }),
        new(13, "任务接取身份、清图及完整领奖账本", static (db, tx) =>
        {
            Execute(db, tx, "ALTER TABLE character_active_main_quests ADD COLUMN acceptance_id TEXT NOT NULL DEFAULT '00000000000000000000000000000000' CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*')");
            // 为已有接取建立稳定身份，不修改原条件、不补造清图或发奖。
            Execute(db, tx, "UPDATE character_active_main_quests SET acceptance_id=lower(hex(randomblob(16)))");
            Execute(db, tx, "CREATE UNIQUE INDEX character_active_quest_acceptance ON character_active_main_quests(character_id,acceptance_id) WHERE acceptance_id<>'00000000000000000000000000000000'");
            Execute(db, tx, QuestClearSchema);
            Execute(db, tx, QuestCompletionSchema);
        }),
        // PR12保留了v13的完整领奖表；v14已在现场发布，兼容升级只确认原表，不再重复建表。
        new(14, "已发布完整领奖结构兼容", static (db, tx) =>
            Execute(db, tx, "SELECT character_id,acceptance_id,event_id,rewards,revision FROM character_quest_completion_receipts LIMIT 0")),
        new(15, "已发布疲劳owner兼容", static (db, tx) =>
        {
            Execute(db, tx, FatigueSchema);
            // 接管此前对正式owner实际固定投影的0/156，未知旧角色不补记录、不推进revision。
            Execute(db, tx, "INSERT INTO character_fatigue SELECT c.id,c.account,0,156,0,156,0,0 FROM characters c JOIN character_dungeon_state d ON d.character_id=c.id AND d.account=c.account");
        }),
        new(16, "普通副本独立身份与房间账本", UpgradeNormalDungeonReceipts),
        new(17, "任务系统与已发布普通副本兼容", UpgradeQuestSystem),
        new(18, "任务回执与任务券完整结构", CompleteQuestSystemV18),
        new(19, "补齐旧副本角色的疲劳初始状态", BackfillDungeonFatigue),
        new(20, "完整地面掉落集合与旧掉落分支v18及v19兼容", static (db, tx) =>
        {
            // 旧掉落v19尚未执行master的疲劳v19；按同一资格补齐，已有值和历史回执保持原样。
            BackfillDungeonFatigue(db, tx);
            // 已部署掉落分支v18尚未执行master的任务v18；精确接管已知结构且保留原回执。
            CompleteQuestSystemV18(db, tx);
            DungeonDropCollectionSchema.Upgrade(db, tx);
        }),
        new(21, "普通免费翻牌奖励与永久邮件回执", static (db, tx) => Execute(db, tx, DungeonCardRewardSchema.Sql)),
        new(22, "付费翻牌、黑钻及黄金牌每日权益", DungeonPremiumCardSchema.Create),
        new(23, "成长经验封顶与实际到账回执", ExperienceCapSchema.Upgrade),
        new(24, "删除角色时保留账号任务完成事实", AccountQuestSourceSchema.Upgrade),
        new(25, "称号簿实体位置、三计数进度与章节奖励", static (db, tx) => Execute(db, tx, TitleSchema)),
        new(26, "新角色训练状态及原子检查点", static (db, tx) => Execute(db, tx, TutorialSchema)),
        new(27, "账号分配置键位", static (db, tx) => Execute(db, tx, HotkeySettingsSchema)),
        new(28, "角色基础心情", static (db, tx) =>
            Execute(db, tx, "ALTER TABLE characters ADD COLUMN emotion INTEGER NOT NULL DEFAULT 0 CHECK(emotion BETWEEN 0 AND 32767)")),
        new(29, "账号冒险团基础进度与名称", static (db, tx) => Execute(db, tx, AdventureSchema)),
        new(30, "冒险团代表职业选择", static (db, tx) => Execute(db, tx, AdventureRepresentativeSchema)),
        new(31, "账号远征及冻结成员", static (db, tx) => Execute(db, tx, AdventureExpeditionSchema)),
        new(32, "冒险团商店账号限购", static (db, tx) => Execute(db, tx, AdventurePurchasesSchema)),
        new(33, "账号赛丽亚幸运值", AddSeriaLuckColumn),
        new(34, "任务跳过实际效果账本", AddQuestEffects),
        new(35, "账号契约账本及已知旧分支v34兼容", UpgradePremiumContracts),
        new(36, "翻牌领取、活动与统一会员契约来源", DungeonCardClaimSchema.Upgrade),
        // 拍卖行挂牌表与保证金列合成同一步（本步在任何已发布版本里都不存在，编号在合并上游后重新占用 37）：
        // 一步建出含 `deposit_gold` 的完整表，不再有「先建表、后加列」的两步，也就没有中途半成品的窗口。
        new(37, "拍卖行挂牌与保证金", static (db, tx) =>
        {
            foreach (string statement in AuctionListingSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Execute(db, tx, statement);
        }),
    ];

    // 拍卖行挂牌跨账号可见，不归属单个角色；卖家为机器人时 seller_character_id 为 NULL，
    // 因此本表不建立指向 characters 的外键。item_entry 是物品条目的字节快照，长度与内容由物品条目写入方负责，本表只保存字节。
    // 本结构由 v37 一步建出（含 deposit_gold），与 `Sqlite/game_schema.sql` 的当前结构逐列一致。
    // 用 `IF NOT EXISTS`（与 v34/v36 的同款做法）：真正的老库（< v37）没有这张表，这里建出来；
    // 而「拿当前结构库改版本号回退」的迁移夹具、以及从 `game_schema.sql` 直接重建的开发库可能已经有了它，
    // 重复建表会撞名。列是否齐全由 `ValidateSchema` 的必需列检查兜底，不靠 CREATE 报错。
    internal const string AuctionListingSchema = """
        -- state: 0=在售 1=已成交 2=已下架 3=已过期；buyout_price 为 -1 表示无一口价。
        CREATE TABLE IF NOT EXISTS auction_listings(
          listing_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(listing_id BETWEEN 1 AND 2147483647),
          seller_character_id INTEGER,
          seller_name TEXT NOT NULL,
          item_template_id INTEGER NOT NULL CHECK(item_template_id BETWEEN 0 AND 2147483647),
          item_kind INTEGER NOT NULL CHECK(item_kind BETWEEN 0 AND 255),
          item_count INTEGER NOT NULL CHECK(item_count BETWEEN 1 AND 2147483647),
          item_entry BLOB NOT NULL CHECK(typeof(item_entry)='blob' AND length(item_entry)>0),
          starting_price INTEGER NOT NULL CHECK(starting_price BETWEEN 0 AND 2147483647),
          buyout_price INTEGER NOT NULL CHECK(buyout_price BETWEEN -1 AND 2147483647),
          current_bid INTEGER NOT NULL DEFAULT 0 CHECK(current_bid BETWEEN 0 AND 2147483647),
          current_bidder_character_id INTEGER,
          state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 3),
          posted_at_unix INTEGER NOT NULL CHECK(posted_at_unix>=0),
          expires_at_unix INTEGER NOT NULL CHECK(expires_at_unix>=posted_at_unix),
          settled_at_unix INTEGER,
          -- deposit_gold: 挂牌阶段收取的保证金（金币，逐行记录）。取消上架不退还、到期未售按此金额原额退还。
          deposit_gold INTEGER NOT NULL DEFAULT 0 CHECK(deposit_gold BETWEEN 0 AND 2147483647)
        );
        CREATE INDEX IF NOT EXISTS auction_listings_expiry ON auction_listings(state,expires_at_unix);
        CREATE INDEX IF NOT EXISTS auction_listings_seller ON auction_listings(seller_character_id,state);
        CREATE INDEX IF NOT EXISTS auction_listings_template ON auction_listings(state,item_template_id,listing_id);
        """;

    internal const string QuestEffectsSchema = """
        CREATE TABLE IF NOT EXISTS character_quest_effect_receipts(
          character_id INTEGER NOT NULL REFERENCES characters(id),operation_id TEXT NOT NULL CHECK(length(operation_id)=32),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          claimed_quests TEXT NOT NULL CHECK(json_valid(claimed_quests) AND json_type(claimed_quests)='array'),
          changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          PRIMARY KEY(character_id,operation_id));
        CREATE TABLE IF NOT EXISTS character_quest_special_reward_claims(
          character_id INTEGER NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),operation_id TEXT NOT NULL,
          PRIMARY KEY(character_id,quest_id),
          FOREIGN KEY(character_id,operation_id) REFERENCES character_quest_effect_receipts(character_id,operation_id));
        """;

    internal const string HotkeySettingsSchema = """
        CREATE TABLE account_hotkey_settings(
          account TEXT NOT NULL REFERENCES accounts(account_id),profile INTEGER NOT NULL CHECK(profile IN (0,1)),
          hotkeys BLOB NOT NULL CHECK(typeof(hotkeys)='blob' AND length(hotkeys) BETWEEN 1 AND 1399),
          PRIMARY KEY(account,profile));
        """;

    internal const string TutorialSchema = """
        CREATE TABLE character_tutorial_state(
          character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
          completed INTEGER NOT NULL CHECK(completed IN(0,1)),reward_received INTEGER NOT NULL CHECK(reward_received IN(0,1)),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        CREATE TABLE character_tutorial_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL CHECK(length(event_id)=32),
          kind TEXT NOT NULL CHECK(kind IN('death','settlement','reward','skip','clear')),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>=0),
          epoch INTEGER NOT NULL CHECK(epoch>=0),map_id INTEGER NOT NULL CHECK(map_id>=0),subject INTEGER NOT NULL CHECK(subject>=0),
          experience INTEGER NOT NULL CHECK(experience BETWEEN 0 AND 4294967295),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          plan_snapshot TEXT NOT NULL CHECK(length(plan_snapshot)>0),
          PRIMARY KEY(character_id,event_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(character_id) REFERENCES character_tutorial_state(character_id));
        """;

    private static void BackfillDungeonFatigue(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, """
            INSERT INTO character_fatigue(character_id,account,used,maximum,used_maximum,remaining,context_auxiliary,selection_auxiliary)
            SELECT c.id,c.account,0,156,0,156,0,0 FROM characters c
            JOIN character_dungeon_state d ON d.character_id=c.id AND d.account=c.account
            WHERE NOT EXISTS(SELECT 1 FROM character_fatigue f WHERE f.character_id=c.id)
              AND NOT EXISTS(SELECT 1 FROM character_dungeon_fatigue_receipts r WHERE r.character_id=c.id)
            """);

    internal const string AdventurePurchasesSchema = """
        CREATE TABLE account_adventure_purchases(
          account TEXT NOT NULL REFERENCES account_adventures(account),item_id INTEGER NOT NULL CHECK(item_id>0),
          count INTEGER NOT NULL CHECK(count BETWEEN 1 AND 4294967295),PRIMARY KEY(account,item_id));
        """;

    internal const string AdventureExpeditionSchema = """
        CREATE TABLE account_adventure_expeditions(
          account TEXT NOT NULL REFERENCES account_adventures(account),area INTEGER NOT NULL CHECK(area BETWEEN 1 AND 255),
          run_id TEXT NOT NULL UNIQUE CHECK(length(run_id)=32),started INTEGER NOT NULL CHECK(started>=0),
          ends INTEGER NOT NULL CHECK(ends>started),reward INTEGER NOT NULL CHECK(reward BETWEEN 0 AND 4294967295),
          extra_reward INTEGER NOT NULL CHECK(extra_reward BETWEEN 0 AND 4294967295),
          attributes TEXT NOT NULL CHECK(json_valid(attributes)),PRIMARY KEY(account,area));
        CREATE TABLE account_adventure_expedition_members(
          account TEXT NOT NULL,area INTEGER NOT NULL,ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 3),
          character_id INTEGER NOT NULL,member_snapshot TEXT NOT NULL CHECK(json_valid(member_snapshot)),
          PRIMARY KEY(account,area,ordinal),UNIQUE(account,character_id),
          FOREIGN KEY(account,area) REFERENCES account_adventure_expeditions(account,area) ON DELETE CASCADE,
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        CREATE TABLE account_adventure_receipts(
          account TEXT NOT NULL REFERENCES account_adventures(account),event_id TEXT NOT NULL,
          character_id INTEGER,action TEXT NOT NULL,amount INTEGER NOT NULL CHECK(amount>=0),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision>before_revision),
          PRIMARY KEY(account,event_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    internal const string AdventureRepresentativeSchema = """
        CREATE TABLE account_adventure_representatives(
          account TEXT NOT NULL REFERENCES account_adventures(account),job INTEGER NOT NULL CHECK(job BETWEEN 0 AND 15),
          grow INTEGER NOT NULL CHECK(grow BETWEEN 1 AND 15),character_id INTEGER,
          PRIMARY KEY(account,job,grow),FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
        """;

    internal const string AdventureSchema = """
        CREATE TABLE account_adventures(
          account TEXT PRIMARY KEY REFERENCES accounts(account_id),revision INTEGER NOT NULL CHECK(revision>=0),
          name TEXT NOT NULL CHECK(length(name)>0),created_date INTEGER NOT NULL CHECK(created_date BETWEEN 19000101 AND 99991231),
          level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 255),experience INTEGER NOT NULL CHECK(experience>=0),
          remainder TEXT NOT NULL,capsule_experience INTEGER NOT NULL CHECK(capsule_experience>=0),
          brave_experience INTEGER NOT NULL CHECK(brave_experience>=0),points BLOB NOT NULL CHECK(length(points)=20),
          month INTEGER NOT NULL CHECK(month BETWEEN 190001 AND 999912));
        """;

    internal const string TitleSchema = """
        CREATE TABLE character_title_book_slots(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            list_type INTEGER NOT NULL CHECK(list_type BETWEEN 18 AND 22),
            slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 255),
            item_uid INTEGER NOT NULL UNIQUE,
            PRIMARY KEY(character_id,list_type,slot_index),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
            FOREIGN KEY(account,character_id,item_uid) REFERENCES character_inventory_items(account,owner_character_id,item_uid) ON DELETE CASCADE
        );
        CREATE TABLE character_title_progress(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
            remaining1 INTEGER NOT NULL CHECK(remaining1 BETWEEN 0 AND 65535),
            remaining2 INTEGER NOT NULL CHECK(remaining2 BETWEEN 0 AND 65535),
            remaining3 INTEGER NOT NULL CHECK(remaining3 BETWEEN 0 AND 65535),
            completed_revision INTEGER NOT NULL DEFAULT 0 CHECK(completed_revision>=0),
            PRIMARY KEY(character_id,quest_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
            CHECK((remaining1+remaining2+remaining3=0 AND completed_revision>0) OR (remaining1+remaining2+remaining3>0 AND completed_revision=0))
        );
        CREATE TABLE character_title_sections(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            section_key INTEGER NOT NULL CHECK(section_key BETWEEN 0 AND 1279),
            revision INTEGER NOT NULL CHECK(revision>0),
            PRIMARY KEY(character_id,section_key),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        CREATE TRIGGER title_book_exclusive_insert BEFORE INSERT ON character_title_book_slots
        WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
             EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'title item already has a position'); END;
        CREATE TRIGGER title_book_exclusive_update BEFORE UPDATE ON character_title_book_slots
        WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
             EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'title item already has a position'); END;
        CREATE TRIGGER inventory_not_title_insert BEFORE INSERT ON character_inventory_slots
        WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
        CREATE TRIGGER inventory_not_title_update BEFORE UPDATE ON character_inventory_slots
        WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
        CREATE TRIGGER mail_not_title_insert BEFORE INSERT ON mailbox_attachments
        WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
        CREATE TRIGGER mail_not_title_update BEFORE UPDATE ON mailbox_attachments
        WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
        """;

    internal const string FatigueSchema = """
        CREATE TABLE character_fatigue(
          character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,
          used INTEGER NOT NULL CHECK(used BETWEEN 0 AND maximum),
          maximum INTEGER NOT NULL CHECK(maximum BETWEEN 1 AND 65535),
          used_maximum INTEGER NOT NULL CHECK(used_maximum BETWEEN 0 AND 65535),
          remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND maximum),
          context_auxiliary INTEGER NOT NULL CHECK(context_auxiliary BETWEEN 0 AND 65535),
          selection_auxiliary INTEGER NOT NULL CHECK(selection_auxiliary BETWEEN 0 AND 65535),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        CREATE TABLE character_dungeon_fatigue_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),map_id INTEGER NOT NULL CHECK(map_id BETWEEN 76121 AND 76126),
          epoch INTEGER NOT NULL CHECK(epoch>0),generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          before_state TEXT NOT NULL CHECK(json_valid(before_state) AND json_type(before_state)='object'),
          after_state TEXT NOT NULL CHECK(json_valid(after_state) AND json_type(after_state)='object'),
          PRIMARY KEY(character_id,instance_id,map_id),UNIQUE(character_id,task_attempt,map_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    static void UpgradeNormalDungeonReceipts(SqliteConnection db, SqliteTransaction tx)
    {
        // 成长的子账本须一起保留并重建外键，不能关外键、删历史奖励或修改已发布迁移。
        var tables = new List<string> { "character_dungeon_growth_receipts", "character_dungeon_loot_receipts",
            "character_dungeon_pickup_receipts", "character_quest_clear_receipts", "character_quest_completion_receipts",
            "character_dungeon_settlement_receipts", "character_dungeon_fatigue_receipts" };
        // 集合账本由掉落模块登记；已存在时必须连同成长父表重建，保持原字段、数据及外键。
        using (var query = db.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = """
                SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN
                  ('character_dungeon_drop_batches','character_dungeon_drop_objects','character_dungeon_drop_pickups')
                """;
            int count = Convert.ToInt32(query.ExecuteScalar());
            if (count is not (0 or 3))
                throw new InvalidDataException("普通副本升级需要完整的已有掉落集合外键依赖");
            if (count == 3)
                tables.AddRange(["character_dungeon_drop_batches", "character_dungeon_drop_objects", "character_dungeon_drop_pickups"]);
            // 已发布任务v14也引用成长回执；有历史任务掉落时须先重建子表再替换父表。
            query.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='character_quest_drop_receipts'";
            if (Convert.ToInt32(query.ExecuteScalar()) == 1)
                tables.Add("character_quest_drop_receipts");
        }
        var schemas = new Dictionary<string, string>();
        var secondary = new List<string>();
        using (var query = db.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE sql IS NOT NULL";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (!tables.Contains(reader.GetString(2))) continue;
                if (reader.GetString(0) == "table") schemas.Add(reader.GetString(1), reader.GetString(3));
                else if (reader.GetString(0) is "index" or "trigger") secondary.Add(reader.GetString(3));
            }
        }
        foreach (string table in tables)
        {
            if (!schemas.TryGetValue(table, out string? sql))
                throw new InvalidDataException("普通副本迁移缺少原账本：" + table);
            if (table is "character_dungeon_growth_receipts" or "character_dungeon_settlement_receipts")
            {
                if (!sql.Contains("CHECK(quest_id>0)", StringComparison.Ordinal) && !sql.Contains("CHECK(quest_id>=0)", StringComparison.Ordinal))
                    throw new InvalidDataException("普通副本迁移的原任务身份约束不匹配");
                sql = sql.Replace("CHECK(quest_id>0)", "CHECK(quest_id>=0)", StringComparison.Ordinal);
            }
            if (table == "character_dungeon_fatigue_receipts")
            {
                if (!sql.Contains("CHECK(map_id BETWEEN 76121 AND 76126)", StringComparison.Ordinal) && !sql.Contains("CHECK(map_id>0)", StringComparison.Ordinal))
                    throw new InvalidDataException("普通副本迁移的原地图约束不匹配");
                sql = sql.Replace("CHECK(map_id BETWEEN 76121 AND 76126)", "CHECK(map_id>0)", StringComparison.Ordinal);
            }
            foreach (string name in tables)
                sql = sql.Replace(name, name + "_normal", StringComparison.Ordinal);
            Execute(db, tx, sql);
            using var columnsQuery = db.CreateCommand();
            columnsQuery.Transaction = tx;
            columnsQuery.CommandText = $"PRAGMA table_info({table})";
            var columns = new List<string>();
            using (var reader = columnsQuery.ExecuteReader())
                while (reader.Read()) columns.Add('"' + reader.GetString(1).Replace("\"", "\"\"") + '"');
            Execute(db, tx, $"INSERT INTO {table}_normal(rowid,{string.Join(',', columns)}) SELECT rowid,* FROM {table}");
        }
        foreach (string table in Enumerable.Reverse(tables)) Execute(db, tx, $"DROP TABLE {table}");
        foreach (string table in tables) Execute(db, tx, $"ALTER TABLE {table}_normal RENAME TO {table}");
        foreach (string sql in secondary) Execute(db, tx, sql);
        using var integrity = db.CreateCommand();
        integrity.Transaction = tx;
        integrity.CommandText = "PRAGMA foreign_key_check";
        using var failures = integrity.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("普通副本迁移破坏原账本外键");
    }

    static void UpgradeQuestSystem(SqliteConnection db, SqliteTransaction tx)
    {
        // master已发布的任务v14与普通分支的v14..v16结构不同，按实际字段辨认；两路均升级到17。
        using (var existing = db.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = """
                SELECT (SELECT count(*) FROM pragma_table_info('characters') WHERE name IN ('quest_cycle_day','equipment_slot_flags')) +
                  (SELECT count(*) FROM pragma_table_info('character_completed_quests') WHERE name IN ('daily_period','answer'))
                """;
            int columns = Convert.ToInt32(existing.ExecuteScalar());
            if (columns == 4) return;
            if (columns != 0) throw new InvalidDataException("已有任务结构不完整，拒绝兼容升级");
        }
        // 直接从已发布v13建立本版最终结构；已有账本最多重建一次，不改历史规则或回执内容。
        Execute(db, tx, "PRAGMA defer_foreign_keys=ON");
        Execute(db, tx, "ALTER TABLE characters ADD COLUMN quest_cycle_day INTEGER NOT NULL DEFAULT 0 CHECK(quest_cycle_day=0 OR quest_cycle_day BETWEEN 19000101 AND 99991231)");
        Execute(db, tx, "ALTER TABLE characters ADD COLUMN equipment_slot_flags INTEGER NOT NULL DEFAULT 0 CHECK(equipment_slot_flags BETWEEN 0 AND 255)");
        Execute(db, tx, "ALTER TABLE character_completed_quests ADD COLUMN daily_period INTEGER NOT NULL DEFAULT 0 CHECK(daily_period=0 OR daily_period BETWEEN 19000101 AND 99991231)");
        Execute(db, tx, "ALTER TABLE character_completed_quests ADD COLUMN answer INTEGER CHECK(answer BETWEEN 0 AND 254)");
        RebuildReceipt(db, tx, "character_quest_clear_receipts", QuestClearSchemaV14);
        RebuildReceipt(db, tx, "character_quest_completion_receipts", QuestCompletionSchemaV14, "clear_acceptance_id", "acceptance_id");
        Execute(db, tx, QuestLifecycleSchema);
        Execute(db, tx, AccountQuestSchema);
        Execute(db, tx, QuestDropSchema);
        Execute(db, tx, QuestSkipSchema);
        using var check = db.CreateCommand(); check.Transaction = tx; check.CommandText = "PRAGMA foreign_key_check";
        using var rows = check.ExecuteReader();
        if (rows.Read()) throw new InvalidDataException("任务系统迁移后的历史外键不完整");
    }

    static void RebuildReceipt(SqliteConnection db, SqliteTransaction tx, string table, string schema,
        string? addedColumn = null, string? addedValue = null)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        var columns = new List<string>(); var dependencies = new List<string>();
        command.CommandText = "PRAGMA table_info(" + table + ")";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add('"' + reader.GetString(1).Replace("\"", "\"\"") + '"');
        if (columns.Count == 0) throw new InvalidDataException("缺少已发布任务或副本账本: " + table);
        command.Parameters.AddWithValue("$table", table);
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name=$table AND type IN ('index','trigger') AND sql IS NOT NULL ORDER BY name";
        using (var reader = command.ExecuteReader()) while (reader.Read()) dependencies.Add(reader.GetString(0));
        string backup = table + "_quest_migration";
        Execute(db, tx, "CREATE TEMP TABLE " + backup + " AS SELECT rowid AS migration_rowid,* FROM " + table);
        Execute(db, tx, "DROP TABLE " + table);
        Execute(db, tx, schema);
        string selected = string.Join(',', columns);
        Execute(db, tx, "INSERT INTO " + table + "(rowid," + selected + (addedColumn is null ? "" : "," + addedColumn) + ") SELECT migration_rowid," +
            selected + (addedValue is null ? "" : "," + addedValue) + " FROM temp." + backup);
        Execute(db, tx, "DROP TABLE temp." + backup);
        // 历史行恢复之后才重建触发器，避免重放已提交事件。
        foreach (string dependency in dependencies) Execute(db, tx, dependency);
    }

    private const string QuestSkipSchema = """
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

    private const string QuestSkipSchemaV18 = """
        CREATE TABLE character_quest_skip_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          parent_key INTEGER CHECK(parent_key BETWEEN 0 AND 2147483647),question_quest INTEGER CHECK(question_quest BETWEEN 1 AND 29999),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot) AND json_type(plan_snapshot)='object'),
          operation_id TEXT NOT NULL CHECK(length(operation_id)=32 AND operation_id NOT GLOB '*[^0-9a-f]*' AND operation_id<>'00000000000000000000000000000000'),
          operation_kind TEXT NOT NULL CHECK(operation_kind IN('QuickClear','Manual','Ticket')),
          CHECK((operation_kind='QuickClear' AND acceptance_id IS NOT NULL AND operation_id=acceptance_id AND parent_key IS NOT NULL AND question_quest IS NOT NULL)
            OR (operation_kind IN('Manual','Ticket') AND acceptance_id IS NULL AND parent_key IS NULL AND question_quest IS NULL)),
          PRIMARY KEY(character_id,operation_id),UNIQUE(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    // v18统一接管已发布master及旧任务分支的已知回执结构，不重写历史规则或奖励。
    static void CompleteQuestSystemV18(SqliteConnection db, SqliteTransaction tx)
    {
        const string completion = "character_quest_completion_receipts", skip = "character_quest_skip_receipts";
        bool updateCompletion = NeedsUpdate(completion, QuestCompletionSchemaV18, QuestCompletionSchemaV14,
            QuestCompletionSchemaV18.Replace("44,49,64", "44,64"));
        bool updateSkip = NeedsUpdate(skip, QuestSkipSchemaV18, QuestSkipSchema);
        if (!updateCompletion && !updateSkip) return;
        Execute(db, tx, "PRAGMA defer_foreign_keys=ON");
        if (updateCompletion) RebuildReceipt(db, tx, completion, QuestCompletionSchemaV18);
        if (updateSkip) RebuildReceipt(db, tx, skip, QuestSkipSchemaV18, "operation_id,operation_kind", "acceptance_id,'QuickClear'");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("v18任务账本保全对齐后的外键不完整");

        bool NeedsUpdate(string table, string current, params string[] legacy)
        {
            using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table", table);
            if (query.ExecuteScalar() is not string actual)
                throw new InvalidDataException("v18缺少任务账本: " + table);
            string canonical = NormalizeTable(actual, table);
            if (canonical == NormalizeTable(current, table)) return false;
            if (legacy.Any(sql => canonical == NormalizeTable(sql, table))) return true;
            throw new InvalidDataException("v18任务账本不是已确认的新旧结构: " + table);
        }
    }

    // 旧任务分支v14保留了已发布疲劳表，原v15步骤只在两表确实缺失时建表。
    static bool HasPublishedFatigue(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_fatigue','character_dungeon_fatigue_receipts')";
        int count = Convert.ToInt32(query.ExecuteScalar());
        if (count == 0) return false;
        if (count != 2) throw new InvalidDataException("已有疲劳owner结构不完整，拒绝兼容升级");
        foreach (string expected in FatigueSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string table = expected.StartsWith("CREATE TABLE character_fatigue(", StringComparison.Ordinal)
                ? "character_fatigue" : "character_dungeon_fatigue_receipts";
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.Clear(); query.Parameters.AddWithValue("$table", table);
            if (query.ExecuteScalar() is not string actual ||
                (NormalizeTable(actual, table) != NormalizeTable(expected, table) &&
                 NormalizeTable(actual, table) != NormalizeTable(expected.Replace("CHECK(map_id BETWEEN 76121 AND 76126)", "CHECK(map_id>0)"), table)))
                throw new InvalidDataException("已有疲劳owner不是已发布结构: " + table);
        }
        return true;
    }

    // SQLite重命名表会为表名及外键引用补双引号，仍按相同已知声明比较。
    static string NormalizeTable(string sql, string table) => string.Concat(sql.Where(c => !char.IsWhiteSpace(c)))
        .TrimEnd(';').Replace("\"" + table + "\"", table)
        .Replace("\"character_quest_clear_receipts\"", "character_quest_clear_receipts")
        .Replace("\"characters\"", "characters");

    private const string QuestCompletionSchemaV18 = """
        CREATE TABLE character_quest_completion_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),event_id TEXT,instance_id TEXT,
          task_attempt INTEGER CHECK(task_attempt>0),epoch INTEGER CHECK(epoch>0),
          generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          resource_source TEXT NOT NULL,quest_source TEXT NOT NULL,
          native_index INTEGER NOT NULL CHECK(native_index>=0),base_experience INTEGER NOT NULL CHECK(base_experience>=0),
          raw_additional_percent INTEGER NOT NULL,difficulty_key TEXT NOT NULL,difficulty_weight INTEGER NOT NULL CHECK(difficulty_weight>=0),
          experience_amount INTEGER NOT NULL CHECK(experience_amount BETWEEN 0 AND 2147483647),
          gold_amount INTEGER NOT NULL CHECK(gold_amount BETWEEN 0 AND 2147483647),
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+experience_amount),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array' AND json_array_length(rewards)<=255),
          clear_acceptance_id TEXT,condition_type INTEGER NOT NULL DEFAULT 3 CHECK(condition_type IN(0,1,2,3,4,6,12,25,31,39,40,42,44,49,64,65,67,70)),
          selector INTEGER NOT NULL DEFAULT 65535 CHECK(selector BETWEEN 0 AND 65535),multiplier INTEGER NOT NULL DEFAULT 1 CHECK(multiplier BETWEEN 1 AND 65535),
          consumed TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(consumed) AND json_type(consumed)='array' AND json_array_length(consumed)<=255),
          changed_items TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          plan_snapshot TEXT CHECK(plan_snapshot IS NULL OR (json_valid(plan_snapshot) AND json_type(plan_snapshot)='object')),
          CHECK((condition_type IN(1,3) AND clear_acceptance_id=acceptance_id AND event_id IS NOT NULL AND instance_id IS NOT NULL AND task_attempt IS NOT NULL AND epoch IS NOT NULL)
            OR (condition_type IN(0,2,4,6,12,25,31,39,40,42,44,49,64,65,67,70) AND clear_acceptance_id IS NULL AND event_id IS NULL AND instance_id IS NULL AND task_attempt IS NULL AND epoch IS NULL AND plan_snapshot IS NOT NULL)),
          PRIMARY KEY(character_id,acceptance_id),
          FOREIGN KEY(character_id,clear_acceptance_id) REFERENCES character_quest_clear_receipts(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    private const string QuestLifecycleSchema = """
        CREATE TABLE character_quest_lifecycle_receipts(
          character_id INTEGER NOT NULL, account TEXT NOT NULL, operation_id TEXT NOT NULL CHECK(length(operation_id)=32 AND operation_id NOT GLOB '*[^0-9a-f]*' AND operation_id<>'00000000000000000000000000000000'),
          action TEXT NOT NULL CHECK(action IN ('Accept','Giveup','Condition')),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0), revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          before_active TEXT NOT NULL CHECK(json_valid(before_active) AND json_type(before_active)='array'),
          after_active TEXT NOT NULL CHECK(json_valid(after_active) AND json_type(after_active)='array'),
          changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          scene_event TEXT CHECK(scene_event IS NULL OR json_valid(scene_event)),
          before_completed TEXT CHECK(before_completed IS NULL OR (json_valid(before_completed) AND json_type(before_completed)='array')),
          after_completed TEXT CHECK(after_completed IS NULL OR (json_valid(after_completed) AND json_type(after_completed)='array')),
          PRIMARY KEY(character_id,operation_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private const string QuestDropSchema = """
        CREATE TABLE character_quest_drop_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot)),
          changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          PRIMARY KEY(character_id,event_id),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private const string AccountQuestSchema = """
        CREATE TABLE account_completed_quests(
          account TEXT NOT NULL REFERENCES accounts(account_id),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
          character_id INTEGER NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          daily_period INTEGER NOT NULL DEFAULT 0 CHECK(daily_period=0 OR daily_period BETWEEN 19000101 AND 99991231),
          answer INTEGER CHECK(answer BETWEEN 0 AND 254),
          PRIMARY KEY(account,quest_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private const string QuestClearSchemaV14 = """
        CREATE TABLE character_quest_clear_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
          quest_slot INTEGER NOT NULL CHECK(quest_slot BETWEEN 0 AND 29),
          event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          dungeon_id INTEGER NOT NULL,map_id INTEGER NOT NULL,
          generation INTEGER NOT NULL CHECK(generation>0),revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,acceptance_id),

          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    private const string QuestCompletionSchemaV14 = """
        CREATE TABLE character_quest_completion_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),event_id TEXT,instance_id TEXT,
          task_attempt INTEGER CHECK(task_attempt>0),epoch INTEGER CHECK(epoch>0),
          generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          resource_source TEXT NOT NULL,quest_source TEXT NOT NULL,
          native_index INTEGER NOT NULL CHECK(native_index>=0),base_experience INTEGER NOT NULL CHECK(base_experience>=0),
          raw_additional_percent INTEGER NOT NULL,difficulty_key TEXT NOT NULL,difficulty_weight INTEGER NOT NULL CHECK(difficulty_weight>=0),
          experience_amount INTEGER NOT NULL CHECK(experience_amount BETWEEN 0 AND 2147483647),
          gold_amount INTEGER NOT NULL CHECK(gold_amount BETWEEN 0 AND 2147483647),
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+experience_amount),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array' AND json_array_length(rewards)<=255),
          clear_acceptance_id TEXT,condition_type INTEGER NOT NULL DEFAULT 3 CHECK(condition_type IN(0,1,2,3,4,6,12,25,31,39,40,42,44,64,65,67)),
          selector INTEGER NOT NULL DEFAULT 65535 CHECK(selector BETWEEN 0 AND 65535),multiplier INTEGER NOT NULL DEFAULT 1 CHECK(multiplier BETWEEN 1 AND 65535),
          consumed TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(consumed) AND json_type(consumed)='array' AND json_array_length(consumed)<=255),
          changed_items TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          plan_snapshot TEXT CHECK(plan_snapshot IS NULL OR (json_valid(plan_snapshot) AND json_type(plan_snapshot)='object')),
          CHECK((condition_type IN(1,3) AND clear_acceptance_id=acceptance_id AND event_id IS NOT NULL AND instance_id IS NOT NULL AND task_attempt IS NOT NULL AND epoch IS NOT NULL)
            OR (condition_type IN(0,2,4,6,12,25,31,39,40,42,44,64,65,67) AND clear_acceptance_id IS NULL AND event_id IS NULL AND instance_id IS NULL AND task_attempt IS NULL AND epoch IS NULL AND plan_snapshot IS NOT NULL)),
          PRIMARY KEY(character_id,acceptance_id),
          FOREIGN KEY(character_id,clear_acceptance_id) REFERENCES character_quest_clear_receipts(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    // 只新增事实账本，不补发存量角色经验或点数。
    private const string DungeonGrowthSchema = """
        CREATE TABLE character_dungeon_growth_receipts(
        character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
        instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id>0),
        task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
        map_id INTEGER NOT NULL,monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 0 AND 65535),
        combat_sequence INTEGER NOT NULL CHECK(combat_sequence>0),
        generation INTEGER NOT NULL CHECK(generation>0),amount INTEGER NOT NULL CHECK(amount>0),
        rule_id TEXT NOT NULL,resource_sha256 TEXT NOT NULL,
        monster_id INTEGER NOT NULL,monster_level INTEGER NOT NULL CHECK(monster_level BETWEEN 1 AND 255),
        before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
        after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
        before_experience INTEGER NOT NULL CHECK(before_experience>=0),
        after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+amount),
        revision INTEGER NOT NULL CHECK(revision>0),
        PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id,monster_key),
        UNIQUE(character_id,instance_id,combat_sequence),
        FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    public static int CurrentVersion => Steps.Length == 0 ? BaselineVersion : Steps[^1].Version;

    static void UpgradePremiumContracts(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')";
        int effects = Convert.ToInt32(query.ExecuteScalar());
        if (effects != 2)
        {
            // 只接管已发布本分支的完整v34形状，不能借迁移修补未知缺表库。
            query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='premium_contracts' AND upper(type)='TEXT' AND \"notnull\"=1 AND dflt_value='''{}'''";
            if (effects != 0 || Convert.ToInt32(query.ExecuteScalar()) != 1)
                throw new InvalidDataException("未知v34任务或契约账本结构");
            AddQuestEffects(db, tx);
        }
        AddPremiumContractsColumn(db, tx);
    }

    static void AddQuestEffects(SqliteConnection db,SqliteTransaction tx)
    {
        using var query=db.CreateCommand(); query.Transaction=tx;
        // 本地旧v32使用任务账本编号，未执行master的商店v32；只接管这两张已存在的账本形状。
        query.CommandText="""
            SELECT count(*) FROM sqlite_schema WHERE type='table'
              AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')
              AND NOT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='account_adventure_purchases')
            """;
        if(Convert.ToInt32(query.ExecuteScalar())==2) Execute(db,tx,AdventurePurchasesSchema);
        Execute(db,tx,QuestEffectsSchema);
    }

    static void AddSeriaLuckColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='seria_luck'";
        if ((long)query.ExecuteScalar()! == 0)
            Execute(db, tx, "ALTER TABLE account_wallets ADD COLUMN seria_luck INTEGER NOT NULL DEFAULT 0 CHECK(seria_luck BETWEEN 0 AND 8)");
    }

    static void AddPremiumContractsColumn(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='premium_contracts'";
        if ((long)query.ExecuteScalar()! == 0)
            Execute(db, tx, "ALTER TABLE account_wallets ADD COLUMN premium_contracts TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(premium_contracts) AND json_type(premium_contracts)='object')");
        query.CommandText = "SELECT count(*) FROM pragma_table_info('account_wallets') WHERE name='premium_contracts' AND upper(type)='TEXT' AND \"notnull\"=1 AND dflt_value='''{}'''";
        if (Convert.ToInt32(query.ExecuteScalar()) != 1)
            throw new InvalidDataException("账号契约列结构不兼容");
        query.CommandText = "SELECT count(*) FROM account_wallets WHERE NOT json_valid(premium_contracts) OR json_type(premium_contracts)!='object'";
        if (Convert.ToInt32(query.ExecuteScalar()) != 0)
            throw new InvalidDataException("账号契约账本非法");
    }
    private const string SkillMetadataSchema = """
        CREATE TABLE character_tp_sources(
         character_id INTEGER PRIMARY KEY,bonus_tp INTEGER NOT NULL CHECK(bonus_tp BETWEEN 0 AND 65535),
         FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
        CREATE TABLE character_skill_commands(
         character_id INTEGER NOT NULL,context INTEGER NOT NULL CHECK(context IN(0,1)),skill_id INTEGER NOT NULL CHECK(skill_id BETWEEN 0 AND 65535),
         keys BLOB NOT NULL CHECK(typeof(keys)='blob' AND length(keys) BETWEEN 1 AND 255),
         PRIMARY KEY(character_id,context,skill_id),FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
        CREATE TABLE character_skill_combos(
         character_id INTEGER NOT NULL,context INTEGER NOT NULL CHECK(context IN(0,1)),root_id INTEGER NOT NULL CHECK(root_id BETWEEN 1 AND 32767),
         children BLOB NOT NULL CHECK(typeof(children)='blob' AND length(children)<=12 AND length(children)%2=0),
         PRIMARY KEY(character_id,context,root_id),FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
        """;
    private const string SkillPointSourceSchema = """
        CREATE TABLE character_skill_point_sources(
          character_id INTEGER PRIMARY KEY,bonus_sp INTEGER NOT NULL CHECK(bonus_sp BETWEEN 0 AND 65535),
          FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
        """;
    private const string ExpertContractSchema = """
        CREATE TABLE account_expert_contracts(
          account TEXT PRIMARY KEY,expires_at INTEGER NOT NULL CHECK(expires_at>=0),
          FOREIGN KEY(account) REFERENCES accounts(account_id) ON DELETE CASCADE);
        """;
    private const string DungeonSettlementSchema = """
        CREATE TABLE character_dungeon_settlement_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
          instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id>0),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),map_id INTEGER NOT NULL,
          death_events BLOB NOT NULL CHECK(typeof(death_events)='blob' AND length(death_events)>0 AND length(death_events)%16=0),
          report_body BLOB NOT NULL CHECK(typeof(report_body)='blob' AND length(report_body) BETWEEN 1 AND 4096),
          generation INTEGER NOT NULL CHECK(generation>0),
          base_experience INTEGER NOT NULL CHECK(base_experience BETWEEN 0 AND 4294967295),
          rank_experience INTEGER NOT NULL CHECK(rank_experience BETWEEN 0 AND 4294967295),
          total_experience INTEGER NOT NULL CHECK(total_experience BETWEEN 1 AND 4294967295 AND total_experience=base_experience+rank_experience),
          rank_bonus_index INTEGER NOT NULL CHECK(rank_bonus_index BETWEEN -1 AND 4),
          rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
          after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),
          after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+total_experience),
          revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string QuestClearSchema = """
        CREATE TABLE character_quest_clear_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
          quest_slot INTEGER NOT NULL CHECK(quest_slot BETWEEN 0 AND 29),
          event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          dungeon_id INTEGER NOT NULL,map_id INTEGER NOT NULL,
          generation INTEGER NOT NULL CHECK(generation>0),
          revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,acceptance_id),UNIQUE(character_id,event_id),
          UNIQUE(character_id,instance_id),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string QuestCompletionSchema = """
        CREATE TABLE character_quest_completion_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,acceptance_id TEXT NOT NULL,
          quest_id INTEGER NOT NULL CHECK(quest_id=3145),event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          resource_source TEXT NOT NULL,quest_source TEXT NOT NULL,
          native_index INTEGER NOT NULL CHECK(native_index>=0),base_experience INTEGER NOT NULL CHECK(base_experience>0),
          raw_additional_percent INTEGER NOT NULL,difficulty_key TEXT NOT NULL,difficulty_weight INTEGER NOT NULL CHECK(difficulty_weight>0),
          experience_amount INTEGER NOT NULL CHECK(experience_amount BETWEEN 1 AND 2147483647),
          gold_amount INTEGER NOT NULL CHECK(gold_amount=0),
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),after_experience INTEGER NOT NULL CHECK(after_experience=before_experience+experience_amount),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array' AND json_array_length(rewards)=7),
          PRIMARY KEY(character_id,acceptance_id),UNIQUE(character_id,quest_id),
          FOREIGN KEY(character_id,acceptance_id) REFERENCES character_quest_clear_receipts(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string DungeonLootSchema = """
        CREATE TABLE character_dungeon_loot_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
          instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
          epoch INTEGER NOT NULL CHECK(epoch>0),map_id INTEGER NOT NULL,
          monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 1 AND 65535),
          rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
          ground_key INTEGER CHECK(ground_key BETWEEN 1 AND 65535),
          core TEXT CHECK(core IS NULL OR (json_valid(core) AND json_type(core)='object')),
          x INTEGER CHECK(x BETWEEN 0 AND 65535),y INTEGER CHECK(y BETWEEN 0 AND 65535),
          record_slot INTEGER CHECK(record_slot BETWEEN -32768 AND 32767),
          CHECK((ground_key IS NULL AND core IS NULL AND x IS NULL AND y IS NULL AND record_slot IS NULL) OR
            (ground_key IS NOT NULL AND core IS NOT NULL AND x IS NOT NULL AND y IS NOT NULL AND record_slot IS NOT NULL)),
          PRIMARY KEY(character_id,event_id),UNIQUE(character_id,ground_key),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string DungeonPickupSchema = """
        CREATE TABLE character_dungeon_pickup_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
          ground_key INTEGER NOT NULL CHECK(ground_key BETWEEN 1 AND 65535),
          instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
          epoch INTEGER NOT NULL CHECK(epoch>0),destination_list INTEGER NOT NULL,
          destination_slot INTEGER NOT NULL CHECK(destination_slot BETWEEN 0 AND 351),
          item_uid INTEGER NOT NULL CHECK(item_uid BETWEEN 1 AND 2147483647),
          after_core TEXT NOT NULL CHECK(json_valid(after_core) AND json_type(after_core)='object'),
          revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,event_id),UNIQUE(character_id,ground_key),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_loot_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
    private const string DungeonOwnerSchema = """
        CREATE TABLE character_dungeon_state(
          character_id INTEGER NOT NULL PRIMARY KEY,account TEXT NOT NULL,
          next_object_key INTEGER NOT NULL CHECK(next_object_key BETWEEN 1 AND 65536),
          last_attempt INTEGER NOT NULL CHECK(last_attempt>=0),
          capsule_experience INTEGER NOT NULL CHECK(capsule_experience BETWEEN 0 AND 4294967295),
          proficiency_level INTEGER NOT NULL CHECK(proficiency_level BETWEEN 0 AND 4294967295),
          proficiency_experience INTEGER NOT NULL CHECK(proficiency_experience>=0),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    public static void Apply(SqliteConnection db) => Apply(db, Steps);

    internal static void Apply(SqliteConnection db, IReadOnlyList<SqliteMigration> steps)
    {
        for (int i = 0; i < steps.Count; i++)
            if (steps[i].Version != BaselineVersion + i + 1 || string.IsNullOrWhiteSpace(steps[i].Name) || steps[i].Upgrade is null)
                throw new InvalidOperationException("数据库迁移必须从 v2 开始连续编号并提供名称和实现");
        int current = steps.Count == 0 ? BaselineVersion : steps[^1].Version;
        using var tx = db.BeginTransaction();
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='schema_metadata'";
        if ((long)query.ExecuteScalar()! != 1)
            throw new InvalidDataException("数据库缺少 S5A9 基线标识；请使用独立目录创建新库");
        query.CommandText = "SELECT value FROM schema_metadata WHERE key='baseline_id'";
        if (query.ExecuteScalar() is not string baseline || baseline != BaselineId)
            throw new InvalidDataException("数据库基线不属于本服务端");
        query.CommandText = "PRAGMA user_version";
        long version = (long)query.ExecuteScalar()!;
        if (version < BaselineVersion || version > current)
            throw new InvalidDataException($"数据库版本 {version} 不在当前支持范围 {BaselineVersion}–{current}");
        bool existingFatigue = version == 14 && HasPublishedFatigue(db, tx);
        if (version == 34 && current >= 36 && DungeonCardClaimSchema.IsPreviousLayout(db,tx))
        {
            query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_effect_receipts','character_quest_special_reward_claims')";
            if (query.ExecuteScalar() is 0L) AddQuestEffects(db,tx);
        }
        foreach (var step in steps)
        {
            if (step.Version <= version)
                continue;
            if (step.Version != 15 || !existingFatigue) step.Upgrade(db, tx);
            Execute(db, tx, $"PRAGMA user_version={step.Version}");
        }
        ValidateSchema(db, tx);
        tx.Commit();
    }

    internal static void MarkCurrent(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO schema_metadata(key,value) VALUES('baseline_id',$baseline)";
        command.Parameters.AddWithValue("$baseline", BaselineId);
        command.ExecuteNonQuery();
        Execute(db, tx, $"PRAGMA user_version={CurrentVersion}");
    }

    internal static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void ValidateSchema(SqliteConnection db, SqliteTransaction tx)
    {
        // 基线标识不能代替必需列检查；损坏或残缺结构不得进入监听阶段。
        foreach (string sql in RequiredColumns)
        {
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = sql;
            using var reader = query.ExecuteReader();
        }
        DungeonDropCollectionSchema.Validate(db, tx, required: true);
        using var constraints = db.CreateCommand();
        constraints.Transaction = tx;
        constraints.CommandText = """
            SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name IN
              ('title_book_exclusive_insert','title_book_exclusive_update','inventory_not_title_insert',
               'inventory_not_title_update','mail_not_title_insert','mail_not_title_update');
            """;
        if (Convert.ToInt64(constraints.ExecuteScalar()) != 6)
            throw new InvalidDataException("称号簿与背包、邮件的实例互斥约束缺失");
    }

    private static readonly string[] RequiredColumns =
    [
        // 拍卖行挂牌表由 v37 建出（`IF NOT EXISTS`），列是否齐全在这里兜底：
        // 缺列或缺表都会在建库/升级阶段直接拒绝启动，而不是等到运行时读挂牌才发现。
        "SELECT listing_id,seller_character_id,seller_name,item_template_id,item_kind,item_count,item_entry,starting_price,buyout_price,current_bid,current_bidder_character_id,state,posted_at_unix,expires_at_unix,settled_at_unix,deposit_gold FROM auction_listings LIMIT 0",
        "SELECT character_id,operation_id,before_revision,revision,claimed_quests,changed_items FROM character_quest_effect_receipts LIMIT 0",
        "SELECT character_id,quest_id,operation_id FROM character_quest_special_reward_claims LIMIT 0",
        "SELECT account,item_id,count FROM account_adventure_purchases LIMIT 0",
        "SELECT account,area,run_id,started,ends,reward,extra_reward,attributes FROM account_adventure_expeditions LIMIT 0",
        "SELECT account,area,ordinal,character_id,member_snapshot FROM account_adventure_expedition_members LIMIT 0",
        "SELECT account,event_id,character_id,action,amount,before_revision,revision FROM account_adventure_receipts LIMIT 0",
        "SELECT account,job,grow,character_id FROM account_adventure_representatives LIMIT 0",
        "SELECT account,revision,name,created_date,level,experience,remainder,capsule_experience,brave_experience,points,month FROM account_adventures LIMIT 0",
        "SELECT emotion FROM characters LIMIT 0",
        "SELECT account,profile,hotkeys FROM account_hotkey_settings LIMIT 0",
        "SELECT character_id,account,dungeon_id,completed,reward_received FROM character_tutorial_state LIMIT 0",
        "SELECT character_id,account,event_id,kind,instance_id,task_attempt,epoch,map_id,subject,experience,before_revision,revision,plan_snapshot FROM character_tutorial_receipts LIMIT 0",
        "SELECT character_id,account,list_type,slot_index,item_uid FROM character_title_book_slots LIMIT 0",
        "SELECT character_id,account,quest_id,remaining1,remaining2,remaining3,completed_revision FROM character_title_progress LIMIT 0",
        "SELECT character_id,account,section_key,revision FROM character_title_sections LIMIT 0",
        "SELECT account,black_diamond_expires,gold_card_expires,revision FROM account_card_memberships LIMIT 0",
        "SELECT character_id,account,service_day,used FROM character_gold_card_uses LIMIT 0",
        "SELECT character_id,account,instance_id,options_json,black_diamond_reward_json,bonus_mail_id,paid_reward_json,paid_mail_id,paid_cost,paid_revision,paid_day FROM character_dungeon_card_options LIMIT 0",
        "SELECT character_id,account,instance_id,plan_json,reward_json,mail_id,revision,claimed FROM character_dungeon_card_rewards LIMIT 0",
        "SELECT event_id,revision,starts_at,ends_at,override_active FROM server_events LIMIT 0",
        "SELECT character_id,account,acceptance_id,parent_key,question_quest,before_revision,revision,plan_snapshot,operation_id,operation_kind FROM character_quest_skip_receipts LIMIT 0",
        "SELECT character_id,account,event_id,before_revision,revision,plan_snapshot,changed_items FROM character_quest_drop_receipts LIMIT 0",
        "SELECT account,quest_id,character_id,acceptance_id,daily_period,answer FROM account_completed_quests LIMIT 0",
        "SELECT character_id,account,operation_id,action,quest_id,acceptance_id,before_revision,revision,before_active,after_active,changed_items,scene_event,before_completed,after_completed FROM character_quest_lifecycle_receipts LIMIT 0",
        "SELECT character_id,bonus_tp FROM character_tp_sources LIMIT 0",
        "SELECT character_id,context,skill_id,keys FROM character_skill_commands LIMIT 0",
        "SELECT character_id,context,root_id,children FROM character_skill_combos LIMIT 0",
        "SELECT character_id,bonus_sp FROM character_skill_point_sources LIMIT 0",
        "SELECT account,expires_at FROM account_expert_contracts LIMIT 0",
        "SELECT [key],[value] FROM [schema_metadata] LIMIT 0",
        "SELECT character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_settlement_receipts LIMIT 0",
        "SELECT character_id,account,event_id,instance_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,ground_key,core,x,y,record_slot FROM character_dungeon_loot_receipts LIMIT 0",
        "SELECT character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,destination_list,destination_slot,item_uid,after_core,revision FROM character_dungeon_pickup_receipts LIMIT 0",
        "SELECT character_id,account,next_object_key,last_attempt,capsule_experience,proficiency_level,proficiency_experience FROM character_dungeon_state LIMIT 0",
        "SELECT character_id,account,used,maximum,used_maximum,remaining,context_auxiliary,selection_auxiliary FROM character_fatigue LIMIT 0",
        "SELECT character_id,account,instance_id,task_attempt,map_id,epoch,generation,rule_id,before_revision,revision,before_state,after_state FROM character_dungeon_fatigue_receipts LIMIT 0",
        "SELECT character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_growth_receipts LIMIT 0",
        "SELECT [account_id],[salt],[password_hash],[iterations],[disabled],[revision] FROM [accounts] LIMIT 0",
        "SELECT [account],[game_options] FROM [account_client_settings] LIMIT 0",
        "SELECT [character_id],[account],[character_options] FROM [character_client_settings] LIMIT 0",
        "SELECT [character_id],[account],[game_options] FROM [character_game_settings] LIMIT 0",
        "SELECT [id],[account],[slot],[name],[revision],[town],[area],[x],[y],[direction],[return_town],[return_area],[return_x],[return_y],[return_direction],[job],[grow_type],[level],[accepted_story_level],[gold],[experience],[inventory_expansion],[revive_coins],[win_points],[gold_carry_limit],[auction_gold_limit],[cargo_capacity],[quest_cycle_day],[equipment_slot_flags] FROM [characters] LIMIT 0",
        "SELECT [character_id],[quest_id],[daily_period],[answer] FROM [character_completed_quests] LIMIT 0",
        "SELECT [character_id],[skill_id],[level] FROM [character_skills] LIMIT 0",
        "SELECT [character_id],[slot],[quest_id],[condition_state],[acceptance_id] FROM [character_active_main_quests] LIMIT 0",
        "SELECT character_id,account,acceptance_id,quest_id,quest_slot,event_id,instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision FROM character_quest_clear_receipts LIMIT 0",
        "SELECT character_id,account,acceptance_id,quest_id,event_id,instance_id,task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,rewards,clear_acceptance_id,condition_type,selector,multiplier,consumed,changed_items,plan_snapshot FROM character_quest_completion_receipts LIMIT 0",
        "SELECT [character_id],[context],[sp],[tp] FROM [character_skill_points] LIMIT 0",
        "SELECT [character_id],[context],[slot],[skill_id],[level] FROM [character_skill_bindings] LIMIT 0",
        "SELECT [audit_id],[created_at],[account],[character_id],[generation],[action_name],[list_type],[slot_index],[item_uid_before],[item_uid_after],[item_id_before],[item_id_after],[value_before],[value_after],[item_kind_before],[item_kind_after],[count_before],[count_after],[count_delta],[core_hash_before],[core_hash_after] FROM [inventory_audit_log] LIMIT 0",
        "SELECT [account],[revision],[rental_points],[seria_luck],[premium_contracts] FROM [account_wallets] LIMIT 0",
        "SELECT [account],[currency_kind],[balance] FROM [account_currency_balances] LIMIT 0",
        "SELECT [audit_id],[created_at],[account],[character_id],[generation],[action_name],[currency_kind],[account_scope],[balance_before],[balance_after] FROM [currency_audit_log] LIMIT 0",
        "SELECT [account],[revision],[capacity],[gold] FROM [account_cargo] LIMIT 0",
        "SELECT [item_uid],[character_id],[account],[owner_character_id],[item_core],[created_at],[updated_at] FROM [character_inventory_items] LIMIT 0",
        "SELECT [audit_id],[created_at],[account],[character_id],[generation],[action_name],[personal_capacity_before],[personal_capacity_after],[account_capacity_before],[account_capacity_after],[account_gold_before],[account_gold_after] FROM [cargo_audit_log] LIMIT 0",
        "SELECT [account],[character_id],[npc_id],[item_id],[reset_daily],[day],[count] FROM [shop_purchase_counts] LIMIT 0",
        "SELECT [account],[revision],[order_hash],[counts] FROM [account_epic_books] LIMIT 0",
        "SELECT [audit_id],[created_at],[account],[character_id],[generation],[action_name],[order_hash],[piece_index],[count_before],[count_after] FROM [epic_piece_audit_log] LIMIT 0",
        "SELECT [item_uid],[detail] FROM [inventory_avatar_details] LIMIT 0",
        "SELECT [item_uid],[detail] FROM [inventory_creature_details] LIMIT 0",
        "SELECT [character_id],[account],[list_type],[owner_character_id],[slot_index],[item_uid] FROM [character_inventory_slots] LIMIT 0",
        "SELECT [mail_id],[revision],[recipient_id],[recipient_account],[sender_id],[sender_name],[body],[gold],[created_at],[expires_at],[state],[system_mail],[delivery_key],[delivery_content] FROM [mailbox_messages] LIMIT 0",
        "SELECT [attachment_id],[mail_id],[ordinal],[item_uid],[source_uid],[source_list],[source_slot],[claimed] FROM [mailbox_attachments] LIMIT 0",
        "SELECT [character_id],[day],[amount] FROM [mailbox_sent_gold] LIMIT 0",
        "SELECT [audit_id],[mail_id],[account],[character_id],[generation],[action_name],[created_at],[assets] FROM [mailbox_audit_log] LIMIT 0",
    ];

    // 设置按账号与角色分别保存原始字节；缺省不建行，不改角色资产与 revision。
    internal const string ClientSettingsSchema = """
        CREATE TABLE account_client_settings(
            account TEXT NOT NULL PRIMARY KEY REFERENCES accounts(account_id),
            game_options BLOB NOT NULL CHECK(typeof(game_options)='blob' AND length(game_options)=204)
        );
        CREATE TABLE character_client_settings(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            character_options BLOB NOT NULL CHECK(typeof(character_options)='blob' AND length(character_options)=512),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    internal const string CharacterGameSettingsSchema = """
        CREATE TABLE character_game_settings(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            game_options BLOB NOT NULL CHECK(typeof(game_options)='blob' AND length(game_options)=204),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    // 邮箱从 v4 追加；新库完整 SQL 同时维护，升级逐条 DDL 参与原事务。
    internal const string MailboxSchema = """
        CREATE TABLE mailbox_messages(
            mail_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(mail_id BETWEEN 1 AND 1073741823),
            revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
            recipient_id INTEGER NOT NULL,
            recipient_account TEXT NOT NULL,
            sender_id INTEGER NOT NULL CHECK(sender_id BETWEEN 0 AND 65535),
            sender_name TEXT NOT NULL,
            body TEXT NOT NULL,
            gold INTEGER NOT NULL CHECK(gold BETWEEN 0 AND 2147483647),
            created_at INTEGER NOT NULL CHECK(created_at>=0),
            expires_at INTEGER,
            state INTEGER NOT NULL DEFAULT 1 CHECK(state BETWEEN 0 AND 3),
            system_mail INTEGER NOT NULL CHECK(system_mail IN (0,1)),
            delivery_key TEXT UNIQUE,
            delivery_content TEXT,
            FOREIGN KEY(recipient_id,recipient_account) REFERENCES characters(id,account),
            CHECK(expires_at IS NULL OR expires_at>created_at),
            CHECK((system_mail=0 AND delivery_key IS NULL AND delivery_content IS NULL) OR
                  (system_mail=1 AND delivery_key IS NOT NULL AND delivery_content IS NOT NULL))
        );
        CREATE INDEX mailbox_inbox ON mailbox_messages(recipient_id,state,created_at,mail_id);
        CREATE TABLE mailbox_attachments(
            attachment_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(attachment_id BETWEEN 1 AND 1073741823),
            mail_id INTEGER NOT NULL REFERENCES mailbox_messages(mail_id),
            ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 9),
            item_uid INTEGER UNIQUE REFERENCES character_inventory_items(item_uid),
            source_uid INTEGER NOT NULL CHECK(source_uid>=0),
            source_list INTEGER NOT NULL,
            source_slot INTEGER NOT NULL,
            claimed INTEGER NOT NULL DEFAULT 0 CHECK(claimed IN (0,1)),
            UNIQUE(mail_id,ordinal),
            CHECK((claimed=0 AND item_uid IS NOT NULL) OR (claimed=1 AND item_uid IS NULL))
        );
        CREATE TABLE mailbox_sent_gold(
            character_id INTEGER PRIMARY KEY REFERENCES characters(id),
            day INTEGER NOT NULL,
            amount INTEGER NOT NULL CHECK(amount BETWEEN 0 AND 9223372036854775807)
        );
        CREATE TABLE mailbox_audit_log(
            audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
            mail_id INTEGER NOT NULL REFERENCES mailbox_messages(mail_id),
            account TEXT NOT NULL,
            character_id INTEGER NOT NULL,
            generation INTEGER NOT NULL,
            action_name TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            assets TEXT NOT NULL CHECK(json_valid(assets))
        );
        """;
}
