using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// 原奖励额度仍用于来源及重放校验；实际到账由经验前后值表示，封顶时允许小于额度。
internal static class ExperienceCapSchema
{
    static readonly IReadOnlyDictionary<string, string> Rewards = new Dictionary<string, string>
    {
        ["character_dungeon_growth_receipts"] = "amount",
        ["character_dungeon_settlement_receipts"] = "total_experience",
        ["character_quest_completion_receipts"] = "experience_amount"
    };

    internal static string Definition(string sql, string table)
    {
        if (!Rewards.TryGetValue(table, out var amount)) return sql;
        string old = $"CHECK(after_experience=before_experience+{amount})";
        string capped = $"CHECK(after_experience>=before_experience AND after_experience<=before_experience+{amount})";
        if (sql.Contains(capped, StringComparison.Ordinal)) return sql;
        if (!sql.Contains(old, StringComparison.Ordinal))
            throw new InvalidDataException("经验回执的旧到账约束不匹配：" + table);
        return sql.Replace(old, capped, StringComparison.Ordinal);
    }

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        var schemas = new Dictionary<string, string>(StringComparer.Ordinal);
        var secondary = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name";
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                string kind = reader.GetString(0), table = reader.GetString(2), sql = reader.GetString(3);
                if (kind == "table" && !table.StartsWith("sqlite_", StringComparison.Ordinal)) schemas.Add(table, sql);
                else if (kind is "index" or "trigger")
                {
                    if (!secondary.TryGetValue(table, out var definitions)) secondary[table] = definitions = [];
                    definitions.Add(sql);
                }
            }
        }
        bool changed = false;
        foreach (string table in Rewards.Keys)
        {
            if (!schemas.TryGetValue(table, out string? old)) throw new InvalidDataException("经验迁移缺少回执：" + table);
            string updated = Definition(old, table);
            changed |= updated != old;
            schemas[table] = updated;
        }
        if (!changed) return;

        var parents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string table in schemas.Keys)
        {
            var references = new HashSet<string>(StringComparer.Ordinal);
            query.CommandText = "PRAGMA foreign_key_list(" + Quote(table) + ")";
            using var reader = query.ExecuteReader();
            while (reader.Read()) references.Add(reader.GetString(2));
            parents.Add(table, references);
        }
        var selected = Rewards.Keys.ToHashSet(StringComparer.Ordinal);
        bool added;
        do
        {
            added = false;
            foreach (var (table, references) in parents)
                if (references.Overlaps(selected)) added |= selected.Add(table);
        } while (added);

        // 依赖表一起复制、从子到父替换；不关闭外键、不删历史回执或重放触发器。
        var ordered = new List<string>();
        var remaining = selected.ToHashSet(StringComparer.Ordinal);
        while (remaining.Count != 0)
        {
            string? next = remaining.Order(StringComparer.Ordinal).FirstOrDefault(table => !parents[table].Overlaps(remaining));
            if (next is null) throw new InvalidDataException("经验回执依赖存在循环");
            remaining.Remove(next);
            ordered.Add(next);
        }
        foreach (string table in ordered)
        {
            // 单次替换完整名称，保留SQL字符串常量，避免子表前缀被父表名称再次替换。
            string namesPattern = string.Join('|', selected.OrderByDescending(name => name.Length)
                .Select(System.Text.RegularExpressions.Regex.Escape));
            string sql = System.Text.RegularExpressions.Regex.Replace(schemas[table],
                "'([^']|'')*'|(?<![\\w])(?:" + namesPattern + ")(?![\\w])",
                match => match.Value.StartsWith('\'') ? match.Value : match.Value + "_xp_cap");
            SqliteMigrations.Execute(db, tx, sql);
            query.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
            var columns = new List<string>();
            using (var reader = query.ExecuteReader()) while (reader.Read()) columns.Add(Quote(reader.GetString(1)));
            string names = string.Join(',', columns);
            SqliteMigrations.Execute(db, tx, $"INSERT INTO {Quote(table + "_xp_cap")}(rowid,{names}) SELECT rowid,{names} FROM {Quote(table)}");
        }
        foreach (string table in ordered.AsEnumerable().Reverse()) SqliteMigrations.Execute(db, tx, "DROP TABLE " + Quote(table));
        foreach (string table in ordered) SqliteMigrations.Execute(db, tx, "ALTER TABLE " + Quote(table + "_xp_cap") + " RENAME TO " + Quote(table));
        foreach (string table in ordered)
            if (secondary.TryGetValue(table, out var definitions))
                foreach (string sql in definitions) SqliteMigrations.Execute(db, tx, sql);
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("经验迁移破坏回执外键");
    }

    static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
