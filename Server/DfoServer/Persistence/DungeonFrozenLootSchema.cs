using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class DungeonFrozenLootSchema
{
    internal const string Batches = "character_dungeon_drop_batches";
    internal const string Pickups = "character_dungeon_drop_pickups";
    static readonly (string Table, string Marker, string[] Columns)[] additions =
    [
        (Batches, "PRIMARY KEY(",
        [
            "freeze_format INTEGER NOT NULL DEFAULT 0 CHECK(freeze_format IN(0,1))",
            "combat_sequence INTEGER CHECK(combat_sequence IS NULL OR combat_sequence>0)",
            "committed_epoch INTEGER CHECK(committed_epoch IS NULL OR committed_epoch>0)",
            "map_snapshot TEXT CHECK(map_snapshot IS NULL OR (json_valid(map_snapshot) AND json_type(map_snapshot)='object'))",
            "uses_collection INTEGER CHECK(uses_collection IS NULL OR uses_collection IN(0,1))"
        ]),
        (Pickups, "CHECK((destination_slot",
        [
            "receipt_format INTEGER NOT NULL DEFAULT 0 CHECK(receipt_format IN(0,1))",
            "destination_list INTEGER CHECK(destination_list IS NULL OR destination_list BETWEEN 0 AND 255)",
            "epic_piece TEXT CHECK(epic_piece IS NULL OR (json_valid(epic_piece) AND json_type(epic_piece)='object'))"
        ])
    ];

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (table, _, columns) in additions)
        {
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = $"PRAGMA table_info({table})";
            var installed = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = query.ExecuteReader())
                while (reader.Read()) installed.Add(reader.GetString(1));
            if (installed.Count == 0) throw new InvalidDataException("持久冻结迁移缺少原账本: " + table);
            foreach (string column in columns)
                if (!installed.Contains(column[..column.IndexOf(' ')]))
                    SqliteMigrations.Execute(db, tx, $"ALTER TABLE {table} ADD COLUMN {column}");
        }
        using (var query = db.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table", Pickups);
            string sql = (string)query.ExecuteScalar()!;
            if (!sql.Contains("CHECK((epic_piece IS NULL", StringComparison.Ordinal))
            {
                var (original, replacement) = EpicPieceAssetCheck(sql);
                SqliteMigrations.ReplaceTableConstraint(db, tx, Pickups, original, replacement);
            }
        }
        Validate(db, tx);
    }

    internal static string Definition(string sql, string table)
    {
        foreach (var (name, marker, columns) in additions)
        {
            if (name != table || sql.Contains(columns[0].Split(' ')[0], StringComparison.Ordinal)) continue;
            int index = sql.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) throw new InvalidDataException("持久冻结表缺少已确认约束: " + table);
            sql = sql.Insert(index, string.Join(",", columns) + ",");
        }
        if (table == Pickups && !sql.Contains("CHECK((epic_piece IS NULL", StringComparison.Ordinal))
        {
            var (original, replacement) = EpicPieceAssetCheck(sql);
            sql = sql.Replace(original, replacement, StringComparison.Ordinal);
        }
        return sql;
    }

    // 原位扩展实体/货币CHECK，保留旧行、外键、索引和触发器。
    static (string Original, string Replacement) EpicPieceAssetCheck(string sql)
    {
        int start = sql.IndexOf("CHECK((destination_slot", StringComparison.Ordinal);
        int end = start < 0 ? -1 : sql.IndexOf("PRIMARY KEY(", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new InvalidDataException("碎片回执迁移缺少原资产约束");
        string original = sql[start..end].TrimEnd();
        if (!original.EndsWith(")),", StringComparison.Ordinal))
            throw new InvalidDataException("碎片回执迁移不是已确认的原资产约束");
        string condition = original["CHECK(".Length..^2];
        string replacement = "CHECK((epic_piece IS NULL AND (" + condition + ")) OR " +
            "(epic_piece IS NOT NULL AND destination_slot IS NULL AND item_uid IS NULL AND after_core IS NULL AND " +
            "currency_kind IS NULL AND after_balance IS NULL AND account_revision IS NULL AND " +
            "json_type(epic_piece,'$.Index') IS 'integer' AND json_extract(epic_piece,'$.Index')>=0 AND " +
            "json_type(epic_piece,'$.PieceId') IS 'integer' AND json_extract(epic_piece,'$.PieceId')>0 AND " +
            "json_type(epic_piece,'$.Amount') IS 'integer' AND json_extract(epic_piece,'$.Amount')=amount AND " +
            "json_type(epic_piece,'$.Balance') IS 'integer' AND json_extract(epic_piece,'$.Balance')>=amount AND " +
            "json_type(epic_piece,'$.BookRevision') IS 'integer' AND json_extract(epic_piece,'$.BookRevision')>0)),";
        return (original, replacement);
    }

    internal static void Validate(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT count(*) FROM character_dungeon_drop_batches
            WHERE freeze_format=1 AND (combat_sequence IS NULL OR committed_epoch IS NULL OR map_snapshot IS NULL OR uses_collection IS NULL)
            """;
        if (Convert.ToInt64(query.ExecuteScalar()) != 0) throw new InvalidDataException("当前格式冻结头缺少完整来源");
        query.CommandText = """
            SELECT count(*) FROM character_dungeon_drop_pickups
            WHERE receipt_format=1 AND ((epic_piece IS NULL AND ((currency_kind IS NULL AND destination_list IS NULL) OR
              (currency_kind IS NOT NULL AND destination_list IS NOT NULL))) OR
              (epic_piece IS NOT NULL AND destination_list IS NOT NULL))
            """;
        if (Convert.ToInt64(query.ExecuteScalar()) != 0) throw new InvalidDataException("当前格式拾取缺少实际容器身份");
    }
}
