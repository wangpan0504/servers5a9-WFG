using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace DfoServer.Persistence;

// 账号完成记录保存原接取身份；删除来源角色时允许清空来源而保留完成事实。
internal static class AccountQuestSourceSchema
{
    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        const string table = "account_completed_quests";
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_completed_quests'";
        string sql = query.ExecuteScalar() as string ?? throw new InvalidDataException("账号任务迁移缺少完成表");
        var required = new Dictionary<string, (string Type, int NotNull, int PrimaryKey)>(StringComparer.Ordinal)
        {
            ["account"] = ("TEXT", 1, 1), ["quest_id"] = ("INTEGER", 1, 2), ["character_id"] = ("INTEGER", -1, 0),
            ["acceptance_id"] = ("TEXT", 1, 0), ["daily_period"] = ("INTEGER", 1, 0), ["answer"] = ("INTEGER", 0, 0)
        };
        var columns = new List<string>();
        bool? nullable = null;
        query.CommandText = "PRAGMA table_info(" + table + ")";
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                string name = reader.GetString(1);
                columns.Add(Quote(name));
                if (!required.Remove(name, out var expected)) continue;
                int notNull = reader.GetInt32(3);
                if (!string.Equals(reader.GetString(2), expected.Type, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt32(5) != expected.PrimaryKey || expected.NotNull >= 0 && notNull != expected.NotNull)
                    throw new InvalidDataException("账号任务来源的已知字段定义不匹配：" + name);
                if (name == "character_id") nullable = notNull == 0;
            }
        }
        if (required.Count != 0 || nullable is null) throw new InvalidDataException("账号任务来源缺少已知字段");
        query.CommandText = "PRAGMA foreign_key_list(" + table + ")";
        var foreignKeys = new List<(int Id, int Sequence, string Table, string From, string To)>();
        using (var reader = query.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(3) is "account" or "character_id")
                {
                    if (reader.GetString(5) != "NO ACTION" || reader.GetString(6) != "NO ACTION" || reader.GetString(7) != "NONE")
                        throw new InvalidDataException("账号任务来源的外键动作不匹配");
                    foreignKeys.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
                }
        var characterKeys = foreignKeys.Where(value => value.Table == "characters").OrderBy(value => value.Sequence).ToArray();
        if (foreignKeys.Count != 3 || characterKeys.Length != 2 || characterKeys[0].Id != characterKeys[1].Id ||
            (characterKeys[0].Sequence, characterKeys[0].From, characterKeys[0].To) != (0, "character_id", "id") ||
            (characterKeys[1].Sequence, characterKeys[1].From, characterKeys[1].To) != (1, "account", "account") ||
            foreignKeys.Count(value => (value.Sequence, value.Table, value.From, value.To) == (0, "accounts", "account", "account_id")) != 1)
            throw new InvalidDataException("账号任务来源的已知外键定义不匹配");
        // 只接管原声明和原复合外键；额外CHECK/UNIQUE/生成表达式引用来源时须单独确认。
        string tokens = Regex.Replace(sql, "'(?:''|[^'])*'|--[^\\r\\n]*|/\\*[\\s\\S]*?\\*/", " ");
        var declaration = new Regex("(?<=[,(])\\s*character_id\\s+INTEGER(?<required>\\s+NOT\\s+NULL)?,", RegexOptions.IgnoreCase);
        var declarations = declaration.Matches(sql);
        if (Regex.Matches(tokens, "\\bcharacter_id\\b", RegexOptions.IgnoreCase).Count != 2 || declarations.Count != 1 ||
            declarations[0].Groups["required"].Success == nullable.Value)
            throw new InvalidDataException("账号任务来源存在未知声明或约束");
        if (nullable.Value) return;
        string updated = declaration.Replace(sql, match => match.Value.Remove(
            match.Groups["required"].Index - match.Index, match.Groups["required"].Length), 1);
        string shadow = new Regex(
            "^CREATE TABLE (?:account_completed_quests|\"account_completed_quests\")")
            .Replace(updated, "CREATE TABLE account_completed_quests_source", 1);
        if (shadow == updated) throw new InvalidDataException("账号任务来源的表定义不匹配");
        var tables = new List<string>();
        query.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using (var reader = query.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));
        foreach (string candidate in tables)
        {
            query.CommandText = "PRAGMA foreign_key_list(" + Quote(candidate) + ")";
            using var reader = query.ExecuteReader();
            while (reader.Read())
                if (reader.GetString(2) == table)
                    throw new InvalidDataException("账号任务来源存在未识别的外键子表：" + candidate);
        }
        var secondary = new List<string>();
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name='account_completed_quests' AND type IN('index','trigger') AND sql IS NOT NULL ORDER BY name";
        using (var reader = query.ExecuteReader())
            while (reader.Read()) secondary.Add(reader.GetString(0));
        SqliteMigrations.Execute(db, tx, shadow);
        string names = string.Join(',', columns);
        SqliteMigrations.Execute(db, tx, $"INSERT INTO account_completed_quests_source(rowid,{names}) SELECT rowid,{names} FROM account_completed_quests");
        SqliteMigrations.Execute(db, tx, "DROP TABLE " + table);
        SqliteMigrations.Execute(db, tx, "ALTER TABLE account_completed_quests_source RENAME TO account_completed_quests");
        // 恢复历史数据之后才装回触发器，不重放旧领奖事件。
        foreach (string definition in secondary) SqliteMigrations.Execute(db, tx, definition);
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("账号任务来源迁移破坏外键");
    }

    static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
