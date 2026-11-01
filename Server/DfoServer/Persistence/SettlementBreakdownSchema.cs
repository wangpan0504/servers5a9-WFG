using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace DfoServer.Persistence;

// v70：通关回执补齐秘药与冒险团经验分项。旧行按0补齐，不重算历史奖励；
// 父表CHECK需要重写，故连同引用它的翻牌子表一起重建，不关闭外键、不丢弃历史行。
internal static class SettlementBreakdownSchema
{
    const string Settlement = "character_dungeon_settlement_receipts";
    const string Breakdown = "character_dungeon_card_rewards", BreakdownOptions = "character_dungeon_card_options";
    internal static string PreviousSettlementSql => DungeonSceneSettlementSchema.SettlementSql;
    // 上游 #543 先合入的结构：已有其他加成与冻结分项，尚未有秘药/冒险团分项。
    internal static string ExtendedPreviousSettlementSql => DungeonExperienceBonusSchema.Sql;
    // 本地整合构建曾先于上游 #543 建出的结构：已有秘药/冒险团分项，缺其他加成分项。
    internal static string PanelNumberedSettlementSql => PreviousSettlementSql
        .Replace("total_experience=base_experience+rank_experience+growth_contract_experience",
            "total_experience=base_experience+rank_experience+growth_contract_experience+potion_experience+adventure_experience",
            StringComparison.Ordinal)
        .Replace("""
            growth_contract_experience INTEGER NOT NULL DEFAULT 0 CHECK(growth_contract_experience BETWEEN 0 AND 4294967295),
            """, """
            growth_contract_experience INTEGER NOT NULL DEFAULT 0 CHECK(growth_contract_experience BETWEEN 0 AND 4294967295),
            potion_experience INTEGER NOT NULL DEFAULT 0 CHECK(potion_experience BETWEEN 0 AND 4294967295),
            adventure_experience INTEGER NOT NULL DEFAULT 0 CHECK(adventure_experience BETWEEN 0 AND 4294967295),
            """, StringComparison.Ordinal);
    internal static string SettlementSql => ExtendedPreviousSettlementSql
        .Replace("+other_bonus_experience", "+potion_experience+adventure_experience+other_bonus_experience", StringComparison.Ordinal)
        .Replace("""
            growth_contract_experience INTEGER NOT NULL DEFAULT 0 CHECK(growth_contract_experience BETWEEN 0 AND 4294967295),
            """, """
            growth_contract_experience INTEGER NOT NULL DEFAULT 0 CHECK(growth_contract_experience BETWEEN 0 AND 4294967295),
            potion_experience INTEGER NOT NULL DEFAULT 0 CHECK(potion_experience BETWEEN 0 AND 4294967295),
            adventure_experience INTEGER NOT NULL DEFAULT 0 CHECK(adventure_experience BETWEEN 0 AND 4294967295),
            """, StringComparison.Ordinal);

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
        if (!schemas.TryGetValue(Settlement, out string? actual)) throw new InvalidDataException("通关分项迁移缺少结算回执表");
        string canonical = SqliteMigrations.NormalizeTable(actual, Settlement);
        if (canonical == SqliteMigrations.NormalizeTable(ZeroExperienceDungeonSchema.SettlementSql, Settlement)) return;
        if (canonical == SqliteMigrations.NormalizeTable(SettlementSql, Settlement)) return;
        // 上游 #543 先合入时回执已带其他加成分项列；本步在其结构上继续补秘药与冒险团分项。
        bool extended = canonical == SqliteMigrations.NormalizeTable(ExtendedPreviousSettlementSql, Settlement);
        // 从当前 schema 回退重放的夹具还可能已带 v74 追加的熟练度到账列。
        if (canonical == SqliteMigrations.NormalizeTable(SettlementProficiencySchema.SettlementSql, Settlement)) return;
        if (!extended && canonical != SqliteMigrations.NormalizeTable(PreviousSettlementSql, Settlement))
            throw new InvalidDataException("通关分项迁移不是已确认的原结算结构");
        foreach (string table in new[] { Breakdown, BreakdownOptions })
            if (!schemas.ContainsKey(table)) throw new InvalidDataException("通关分项迁移缺少翻牌依赖表: " + table);

        var parents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string table in schemas.Keys)
        {
            var references = new HashSet<string>(StringComparer.Ordinal);
            query.CommandText = "PRAGMA foreign_key_list(" + Quote(table) + ")";
            using var reader = query.ExecuteReader();
            while (reader.Read()) references.Add(reader.GetString(2));
            parents.Add(table, references);
        }
        var selected = new HashSet<string>(StringComparer.Ordinal) { Settlement };
        var allowed = new HashSet<string>(StringComparer.Ordinal) { Settlement, Breakdown, BreakdownOptions, "character_dungeon_tournament_rewards" };
        bool added;
        do
        {
            added = false;
            foreach (var (table, references) in parents)
                if (references.Overlaps(selected))
                {
                    if (!allowed.Contains(table)) throw new InvalidDataException("通关分项迁移发现未知依赖表: " + table);
                    added |= selected.Add(table);
                }
        } while (added);
        schemas[Settlement] = SettlementSql;
        var ordered = new List<string>();
        var remaining = selected.ToHashSet(StringComparer.Ordinal);
        while (remaining.Count != 0)
        {
            string? next = remaining.Order(StringComparer.Ordinal).FirstOrDefault(table => !parents[table].Overlaps(remaining));
            if (next is null) throw new InvalidDataException("通关分项迁移依赖存在循环");
            remaining.Remove(next); ordered.Add(next);
        }
        string pattern = string.Join('|', selected.OrderByDescending(name => name.Length).Select(Regex.Escape));
        foreach (string table in ordered)
        {
            string sql = Regex.Replace(schemas[table], "'([^']|'')*'|(?<![\\w])(?:" + pattern + ")(?![\\w])",
                match => match.Value.StartsWith('\'') ? match.Value : match.Value + "_breakdown");
            SqliteMigrations.Execute(db, tx, sql);
            query.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
            var columns = new List<string>();
            using (var reader = query.ExecuteReader()) while (reader.Read()) columns.Add(Quote(reader.GetString(1)));
            string names = string.Join(',', columns);
            string extraNames = table == Settlement ? ",potion_experience,adventure_experience" : "";
            string extraValues = table == Settlement ? ",0,0" : "";
            SqliteMigrations.Execute(db, tx, "INSERT INTO " + Quote(table + "_breakdown") + "(rowid," + names + extraNames +
                ") SELECT rowid," + names + extraValues + " FROM " + Quote(table));
        }
        // 复制完成后再从子到父替换，避免外键在删除父表时指向已消失的表名。
        foreach (string table in ordered.AsEnumerable().Reverse()) SqliteMigrations.Execute(db, tx, "DROP TABLE " + Quote(table));
        foreach (string table in ordered) SqliteMigrations.Execute(db, tx, "ALTER TABLE " + Quote(table + "_breakdown") + " RENAME TO " + Quote(table));
        foreach (string table in ordered)
            if (secondary.TryGetValue(table, out var definitions))
                foreach (string sql in definitions) SqliteMigrations.Execute(db, tx, sql);
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("通关分项迁移破坏结算或翻牌外键");
    }

    static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
