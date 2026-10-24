using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// v87：线上的16位编号可跨实例复用，审计身份仍由原实例及死亡事件保持。
internal static class DungeonObjectKeySchema
{
    // 父表在前，替换时逆序删除；保持外键开启并在原升级事务内完成。
    internal static readonly string[] Tables =
    [
        "character_dungeon_loot_receipts", "character_dungeon_pickup_receipts",
        "character_dungeon_drop_batches", "character_dungeon_drop_objects",
        "character_dungeon_drop_pickups", "character_dungeon_passive_pickups"
    ];

    internal static string Definition(string sql, string table)
    {
        if (!Tables.Contains(table)) return sql;
        sql = sql.Replace("\"character_dungeon_drop_batches\"", "character_dungeon_drop_batches", StringComparison.Ordinal)
            .Replace("\"character_dungeon_drop_objects\"", "character_dungeon_drop_objects", StringComparison.Ordinal);
        if (table == "character_dungeon_drop_batches")
            return sql.Replace("PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id,monster_key)",
                "PRIMARY KEY(character_id,event_id),UNIQUE(character_id,event_id,instance_id),UNIQUE(character_id,instance_id,monster_key)", StringComparison.Ordinal);
        if (table == "character_dungeon_drop_objects")
            return sql.Replace("event_id TEXT NOT NULL,ordinal", "event_id TEXT NOT NULL,instance_id TEXT NOT NULL,ordinal", StringComparison.Ordinal)
                .Replace("PRIMARY KEY(character_id,ground_key)", "PRIMARY KEY(character_id,instance_id,ground_key)", StringComparison.Ordinal)
                .Replace("UNIQUE(character_id,event_id,ground_key)", "UNIQUE(character_id,event_id,instance_id,ground_key)", StringComparison.Ordinal)
                .Replace("FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_drop_batches(character_id,event_id)",
                    "FOREIGN KEY(character_id,event_id,instance_id) REFERENCES character_dungeon_drop_batches(character_id,event_id,instance_id)", StringComparison.Ordinal);
        return sql.Replace("UNIQUE(character_id,ground_key)", "UNIQUE(character_id,instance_id,ground_key)", StringComparison.Ordinal)
            .Replace("PRIMARY KEY(character_id,ground_key)", "PRIMARY KEY(character_id,instance_id,ground_key)", StringComparison.Ordinal)
            .Replace("FOREIGN KEY(character_id,event_id,ground_key) REFERENCES character_dungeon_drop_objects(character_id,event_id,ground_key)",
                "FOREIGN KEY(character_id,event_id,instance_id,ground_key) REFERENCES character_dungeon_drop_objects(character_id,event_id,instance_id,ground_key)", StringComparison.Ordinal);
    }

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        var schemas = new Dictionary<string, string>();
        var columns = new Dictionary<string, string[]>();
        var secondary = new List<string>();
        bool changed = false;
        foreach (string table in Tables)
        {
            query.CommandText = $"SELECT sql FROM sqlite_schema WHERE type='table' AND name='{table}'";
            string actual = query.ExecuteScalar() as string ?? throw new InvalidDataException("对象key迁移缺少账本：" + table);
            schemas[table] = Definition(actual, table);
            changed |= SqliteMigrations.NormalizeTable(schemas[table], table) != SqliteMigrations.NormalizeTable(actual, table);
            query.CommandText = $"PRAGMA table_info({table})";
            var names = new List<string>();
            using (var reader = query.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(1));
            columns[table] = names.ToArray();
            query.CommandText = $"SELECT sql FROM sqlite_schema WHERE tbl_name='{table}' AND type IN ('index','trigger') AND sql IS NOT NULL ORDER BY name";
            using (var reader = query.ExecuteReader())
                while (reader.Read()) secondary.Add(reader.GetString(0));
        }
        if (!changed) return;
        const string suffix = "_object_key_migration";
        string namesPattern = string.Join('|', Tables.OrderByDescending(name => name.Length).Select(Regex.Escape));
        foreach (string table in Tables)
        {
            string sql = Regex.Replace(schemas[table], "'([^']|'')*'|(?<![\\w])(?:" + namesPattern + ")(?![\\w])",
                match => match.Value.StartsWith('\'') ? match.Value : match.Value + suffix);
            SqliteMigrations.Execute(db, tx, sql);
            string names = string.Join(',', columns[table].Select(Quote));
            string selected = string.Join(',', columns[table].Select(name => "source." + Quote(name)));
            bool addInstance = table == "character_dungeon_drop_objects" && !columns[table].Contains("instance_id");
            // 回填必须来自原批次，缺来源会触发NOT NULL/外键错误并回滚，不能编造实例。
            string instance = addInstance ? ",(SELECT instance_id FROM character_dungeon_drop_batches b WHERE b.character_id=source.character_id AND b.event_id=source.event_id)" : "";
            SqliteMigrations.Execute(db, tx, $"INSERT INTO {Quote(table + suffix)}(rowid,{names}{(addInstance ? ",instance_id" : "")}) SELECT source.rowid,{selected}{instance} FROM {Quote(table)} source");
        }
        foreach (string table in Tables.Reverse()) SqliteMigrations.Execute(db, tx, "DROP TABLE " + Quote(table));
        foreach (string table in Tables) SqliteMigrations.Execute(db, tx, "ALTER TABLE " + Quote(table + suffix) + " RENAME TO " + Quote(table));
        // 历史记录已复制完成后才恢复触发器，不重新执行历史奖励或审计副作用。
        foreach (string sql in secondary) SqliteMigrations.Execute(db, tx, sql);
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("对象key迁移不能保全历史外键");
    }

    static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
