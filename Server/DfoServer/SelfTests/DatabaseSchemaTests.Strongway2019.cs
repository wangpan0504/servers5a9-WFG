using DfoServer.Persistence;
using DfoServer.Testing;
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
                string current = Snapshot(path);
                using (var db = Open(path)) Execute(db, $"PRAGMA user_version={previousVersion}");
                if (scenario == "replay")
                {
                    _ = new CharacterRepository(path);
                    Check(Snapshot(path) == current, $"{previousVersion}重放至181/182保留两表、周索引、上游结构、完整奖励与历史事实及角色资产");
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
}
