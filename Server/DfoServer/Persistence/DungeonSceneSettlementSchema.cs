using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace DfoServer.Persistence;

// 物件结算绑定原场景来源；保留原父子账本、经验分解及历史回执，不补造死亡或奖励。
internal static class DungeonSceneSettlementSchema
{
    const string Scene = "character_dungeon_scene_clear_receipts", Settlement = "character_dungeon_settlement_receipts";
    internal static string SceneSql => SqliteMigrations.QuestSceneClearSchema.Replace(" AND length(death_events)>0", "", StringComparison.Ordinal);
    internal static string PreviousSettlementSql => ExperienceCapSchema.Definition(SqliteMigrations.DungeonSettlementSchema, Settlement)
        .Replace("CHECK(quest_id>0)", "CHECK(quest_id>=0)", StringComparison.Ordinal);
    internal static string SettlementSql => PreviousSettlementSql
        .Replace(" AND length(death_events)>0", "", StringComparison.Ordinal)
        .Replace("total_experience=base_experience+rank_experience", "total_experience=base_experience+rank_experience+growth_contract_experience", StringComparison.Ordinal)
        .Replace("PRIMARY KEY(character_id,event_id),", """
            scene_operation_id TEXT,
            growth_contract_experience INTEGER NOT NULL DEFAULT 0 CHECK(growth_contract_experience BETWEEN 0 AND 4294967295),
            CHECK(length(death_events)>0 OR scene_operation_id IS NOT NULL),
            PRIMARY KEY(character_id,event_id),
            """, StringComparison.Ordinal)
        .Replace("FOREIGN KEY(character_id,account)", "FOREIGN KEY(character_id,scene_operation_id) REFERENCES character_dungeon_scene_clear_receipts(character_id,operation_id),\nFOREIGN KEY(character_id,account)", StringComparison.Ordinal);

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        var schemas = new Dictionary<string, string>(StringComparer.Ordinal);
        var secondary = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name";
        using (var reader = query.ExecuteReader())
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
        bool sceneCurrent = Current(Scene, SqliteMigrations.QuestSceneClearSchema, SceneSql);
        bool settlementCurrent = Current(Settlement, PreviousSettlementSql, SettlementSql);
        if (sceneCurrent && settlementCurrent) return;
        if (sceneCurrent != settlementCurrent) throw new InvalidDataException("物件结算迁移发现半份来源结构");

        var parents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string table in schemas.Keys)
        {
            var references = new HashSet<string>(StringComparer.Ordinal);
            query.CommandText = "PRAGMA foreign_key_list(" + Quote(table) + ")";
            using var reader = query.ExecuteReader();
            while (reader.Read()) references.Add(reader.GetString(2));
            parents.Add(table, references);
        }
        var selected = new HashSet<string>(StringComparer.Ordinal) { Scene, Settlement };
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            Scene, Settlement, "character_quest_clear_receipts", "character_quest_completion_receipts",
            "character_dungeon_card_rewards", "character_dungeon_card_options", "character_dungeon_tournament_rewards"
        };
        bool added;
        do
        {
            added = false;
            foreach (var (table, references) in parents)
                if (references.Overlaps(selected))
                {
                    if (!allowed.Contains(table)) throw new InvalidDataException("物件结算迁移发现未知来源依赖表: " + table);
                    added |= selected.Add(table);
                }
        } while (added);
        schemas[Scene] = SceneSql;
        schemas[Settlement] = SettlementSql;
        // 新结算依赖原scene；排序按新关系建立，其他父子关系原样保留。
        parents[Settlement].Add(Scene);
        var ordered = new List<string>();
        var remaining = selected.ToHashSet(StringComparer.Ordinal);
        while (remaining.Count != 0)
        {
            string? next = remaining.Order(StringComparer.Ordinal).FirstOrDefault(table => !parents[table].Overlaps(remaining));
            if (next is null) throw new InvalidDataException("物件结算来源依赖存在循环");
            remaining.Remove(next); ordered.Add(next);
        }
        string pattern = string.Join('|', selected.OrderByDescending(name => name.Length).Select(Regex.Escape));
        foreach (string table in ordered)
        {
            string sql = Regex.Replace(schemas[table], "'([^']|'')*'|(?<![\\w])(?:" + pattern + ")(?![\\w])",
                match => match.Value.StartsWith('\'') ? match.Value : match.Value + "_scene_settlement");
            SqliteMigrations.Execute(db, tx, sql);
            query.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
            var columns = new List<string>();
            using (var reader = query.ExecuteReader()) while (reader.Read()) columns.Add(Quote(reader.GetString(1)));
            string names = string.Join(',', columns);
            string extraNames = table == Settlement ? ",scene_operation_id,growth_contract_experience" : "";
            string extraValues = table == Settlement ? ",NULL,0" : "";
            SqliteMigrations.Execute(db, tx, "INSERT INTO " + Quote(table + "_scene_settlement") + "(rowid," + names + extraNames +
                ") SELECT rowid," + names + extraValues + " FROM " + Quote(table));
        }
        // 旧关系与新关系的父子顺序相容，先删子表，避免CASCADE和触发器重放历史事件。
        foreach (string table in ordered.AsEnumerable().Reverse()) SqliteMigrations.Execute(db, tx, "DROP TABLE " + Quote(table));
        foreach (string table in ordered) SqliteMigrations.Execute(db, tx, "ALTER TABLE " + Quote(table + "_scene_settlement") + " RENAME TO " + Quote(table));
        foreach (string table in ordered)
            if (secondary.TryGetValue(table, out var definitions))
                foreach (string sql in definitions) SqliteMigrations.Execute(db, tx, sql);
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("物件结算迁移破坏来源或奖励外键");

        bool Current(string table, string previous, string current)
        {
            if (!schemas.TryGetValue(table, out string? actual)) throw new InvalidDataException("物件结算迁移缺少原来源表: " + table);
            string canonical = SqliteMigrations.NormalizeTable(actual, table);
            if (canonical == SqliteMigrations.NormalizeTable(current, table)) return true;
            if (canonical == SqliteMigrations.NormalizeTable(previous, table)) return false;
            throw new InvalidDataException("物件结算迁移不是已确认的原来源结构: " + table);
        }
    }

    static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
