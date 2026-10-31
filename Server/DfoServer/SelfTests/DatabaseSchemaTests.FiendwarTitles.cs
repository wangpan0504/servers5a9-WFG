using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void FiendwarTitlesUpgrade(string root)
    {
        root = Path.Combine(root, "fiendwar-titles"); Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "wrong-table", "name-collision" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    DROP TABLE character_fiendwar_title_facts;
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('old-fw-title',0,'OldFwTitle',7,38,1,450,234,0,123);
                    PRAGMA user_version=169;
                    """);
                if (scenario == "wrong-table") Execute(db, "CREATE TABLE character_fiendwar_title_facts(character_id TEXT)");
                if (scenario == "name-collision") Execute(db, "CREATE VIEW character_fiendwar_title_facts AS SELECT id FROM characters");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "170结构不兼容全量回滚，不放宽同名表或视图保护");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT count(*) FROM character_fiendwar_title_facts") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123" &&
                    Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "170保留资产且不补造历史成就事实");
                Execute(db, "INSERT INTO character_fiendwar_title_facts VALUES(1,'old-fw-title',7007,5,2)");
                RejectDatabase(() => Execute(db, "UPDATE character_fiendwar_title_facts SET applied=6"));
                RejectDatabase(() => Execute(db, "UPDATE character_fiendwar_title_facts SET quest_id=8907"));
            }
        }
    }
}
