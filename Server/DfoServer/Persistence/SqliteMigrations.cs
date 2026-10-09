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
        new(14, "完整任务状态及原子回执", UpgradeQuestSystem),
    ];

    static void UpgradeQuestSystem(SqliteConnection db, SqliteTransaction tx)
    {
        // 直接从已发布v13建立本版最终结构；已有账本最多重建一次，不改历史规则或回执内容。
        Execute(db, tx, "PRAGMA defer_foreign_keys=ON");
        Execute(db, tx, "ALTER TABLE characters ADD COLUMN quest_cycle_day INTEGER NOT NULL DEFAULT 0 CHECK(quest_cycle_day=0 OR quest_cycle_day BETWEEN 19000101 AND 99991231)");
        Execute(db, tx, "ALTER TABLE characters ADD COLUMN equipment_slot_flags INTEGER NOT NULL DEFAULT 0 CHECK(equipment_slot_flags BETWEEN 0 AND 255)");
        Execute(db, tx, "ALTER TABLE character_completed_quests ADD COLUMN daily_period INTEGER NOT NULL DEFAULT 0 CHECK(daily_period=0 OR daily_period BETWEEN 19000101 AND 99991231)");
        Execute(db, tx, "ALTER TABLE character_completed_quests ADD COLUMN answer INTEGER CHECK(answer BETWEEN 0 AND 254)");
        RebuildReceipt(db, tx, "character_dungeon_growth_receipts", DungeonGrowthSchema.Replace("resource_sha256", "resource_source").Replace("CHECK(quest_id>0)", "CHECK(quest_id>=0)"));
        RebuildReceipt(db, tx, "character_dungeon_settlement_receipts", DungeonSettlementSchema.Replace("CHECK(quest_id>0)", "CHECK(quest_id>=0)"));
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
        foreach (var step in steps)
        {
            if (step.Version <= version)
                continue;
            step.Upgrade(db, tx);
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
    }

    private static readonly string[] RequiredColumns =
    [
        "SELECT character_id,account,acceptance_id,parent_key,question_quest,before_revision,revision,plan_snapshot FROM character_quest_skip_receipts LIMIT 0",
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
        "SELECT [account],[revision],[rental_points] FROM [account_wallets] LIMIT 0",
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
