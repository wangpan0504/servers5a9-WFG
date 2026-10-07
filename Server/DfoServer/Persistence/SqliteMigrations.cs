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
        new(3, "副本成长事件账本", static (db, tx) => Execute(db, tx, DungeonGrowthSchema))
    ];

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
        "SELECT [key],[value] FROM [schema_metadata] LIMIT 0",
        "SELECT character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_sha256,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_growth_receipts LIMIT 0",
        "SELECT [account_id],[salt],[password_hash],[iterations],[disabled],[revision] FROM [accounts] LIMIT 0",
        "SELECT [id],[account],[slot],[name],[revision],[town],[area],[x],[y],[direction],[return_town],[return_area],[return_x],[return_y],[return_direction],[job],[grow_type],[level],[accepted_story_level],[gold],[experience],[inventory_expansion],[revive_coins],[win_points],[gold_carry_limit],[auction_gold_limit],[cargo_capacity] FROM [characters] LIMIT 0",
        "SELECT [character_id],[quest_id] FROM [character_completed_quests] LIMIT 0",
        "SELECT [character_id],[skill_id],[level] FROM [character_skills] LIMIT 0",
        "SELECT [character_id],[slot],[quest_id],[condition_state] FROM [character_active_main_quests] LIMIT 0",
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
    ];
}
