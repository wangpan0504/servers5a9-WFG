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
        new(7, "角色主设置快照", static (db, tx) => Execute(db, tx, CharacterGameSettingsSchema))
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
        "SELECT character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision FROM character_dungeon_growth_receipts LIMIT 0",
        "SELECT [account_id],[salt],[password_hash],[iterations],[disabled],[revision] FROM [accounts] LIMIT 0",
        "SELECT [account],[game_options] FROM [account_client_settings] LIMIT 0",
        "SELECT [character_id],[account],[character_options] FROM [character_client_settings] LIMIT 0",
        "SELECT [character_id],[account],[game_options] FROM [character_game_settings] LIMIT 0",
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
