using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RunStrongway2019Migrations()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-strongway-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (int previousVersion in new[] { 176, 180 })
            foreach (string scenario in new[] { "replay", "wrong-reward", "wrong-statistics" })
            {
                string path = Path.Combine(root, previousVersion + "-" + scenario + ".db");
                _ = new CharacterRepository(path);
                using (var db = Open(path))
                {
                    Execute(db, """
                        INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                        VALUES('strongway-migration',0,'StrongwayMigration',7,38,1,450,234,0,123);
                        INSERT INTO account_strongway2019_rewards
                        SELECT account,'00000000000000000000000000000001',id,1,0,5359,0,20261102,20261028,0,'{}','{}',1
                        FROM characters WHERE account='strongway-migration';
                        INSERT INTO character_strongway2019_statistics
                        SELECT account,id,'00000000000000000000000000000001',1,0,5359,0,123,'{}'
                        FROM characters WHERE account='strongway-migration';
                        """);
                    if (scenario == "wrong-reward")
                        Execute(db, "ALTER TABLE account_strongway2019_rewards ADD COLUMN unknown INTEGER");
                    if (scenario == "wrong-statistics")
                        Execute(db, "ALTER TABLE character_strongway2019_statistics ADD COLUMN unknown INTEGER");
                }
                string current = StrongwayReplaySnapshot(path);
                Dictionary<string, (long Page, string? Sql)> existingPages;
                using (var db = Open(path))
                {
                    RewindReleasedBattlePassTables(db, previousVersion);
                    existingPages = StrongwayReplayPages(db);
                    Execute(db, $"PRAGMA user_version={previousVersion}");
                }
                if (scenario == "replay")
                {
                    _ = new CharacterRepository(path);
                    // 未来空战令表须真实重建，SQL换行、插入顺序和新页号可变；逻辑结构、历史行及原有对象不变。
                    Check(StrongwayReplaySnapshot(path) == current,
                        $"{previousVersion}重放至181/182保留全部表、列、索引、约束、rowid、完整奖励与历史事实及角色资产");
                    using (var db = Open(path))
                    {
                        var replayedPages = StrongwayReplayPages(db);
                        Check(existingPages.All(row => replayedPages.TryGetValue(row.Key, out var actual) && actual == row.Value),
                            "重放保留任何原有对象的页号和原始DDL，只新增回退时已移除的未来空战令结构");
                    }
                    string saved = Snapshot(path);
                    _ = new CharacterRepository(path);
                    Check(Snapshot(path) == saved, "重放成功后重开保留完整结构、所有物理页号、历史行与版本");
                    continue;
                }
                string invalid = Snapshot(path);
                Reject<InvalidDataException>(() => new CharacterRepository(path), $"{previousVersion}兼容重放仍由最终schema校验拒绝未知表结构：" + scenario);
                Check(Snapshot(path) == invalid, "schema拒绝回滚版本及迁移，原有结构与全部事实不被修补或删除：" + scenario);
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 首期181/182兼容重放及严格结构拒绝");
    }

    static string StrongwayReplaySnapshot(string path)
    {
        using var db = Open(path);
        // 仅这九张未来空表的DDL已核实不含跨行字面量、引用标识符或注释；不规范化其它SQL或任何数据行。
        return string.Join('|', Query(db, "PRAGMA user_version"),
            Query(db, """
                SELECT group_concat(type||':'||name||':'||tbl_name||':'||coalesce(
                    CASE WHEN tbl_name IN ('account_battle_pass_seasons','account_battle_pass_claims',
                        'battle_pass_season_rules','account_battle_pass_mission_progress','account_battle_pass_mission_claims',
                        'account_battle_pass_source_events','account_battle_pass_certificate_orders',
                        'account_battle_pass_cera_consumption','account_battle_pass_cera_orders')
                    THEN replace(sql,char(13)||char(10),char(10)) ELSE sql END,''),'|')
                FROM (SELECT * FROM sqlite_schema ORDER BY name)
                """),
            string.Join('|', TableNames(db).Select(table =>
            {
                string[] columns = ColumnNames(db, table);
                return table + ':' + System.Text.Json.JsonSerializer.Serialize(columns) + ':' + Rows(db, table, columns);
            })));
    }

    static Dictionary<string, (long Page, string? Sql)> StrongwayReplayPages(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name,rootpage,sql FROM sqlite_schema";
        using var reader = command.ExecuteReader();
        var pages = new Dictionary<string, (long Page, string? Sql)>(StringComparer.Ordinal);
        while (reader.Read()) pages.Add(reader.GetString(0), (reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return pages;
    }
}
