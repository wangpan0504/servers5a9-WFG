using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void FiendwarCounterUpgrade(string root)
    {
        root = Path.Combine(root, "fiendwar-counter"); Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "wrong-column", "weak-sequence", "wrong-owner" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                      VALUES('old-fw-counter',0,'OldFwCounter',7,38,1,450,234,0,123);
                    DROP TABLE character_fiendwar_counter_stream;
                    PRAGMA user_version=177;
                    """);
                if (scenario != "ordinary") Execute(db, scenario switch
                {
                    "wrong-column" => FiendwarCounterSchema.Definition.Replace("stream", "unknown_stream", StringComparison.Ordinal)
                        .Replace("character_fiendwar_counter_unknown_stream", "character_fiendwar_counter_stream", StringComparison.Ordinal),
                    "weak-sequence" => FiendwarCounterSchema.Definition.Replace("BETWEEN 1 AND 4294967295", "BETWEEN 0 AND 4294967295", StringComparison.Ordinal),
                    _ => FiendwarCounterSchema.Definition.Replace("FOREIGN KEY(character_id,account) REFERENCES characters(id,account)",
                        "FOREIGN KEY(character_id) REFERENCES characters(id)", StringComparison.Ordinal)
                });
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "178错误列、水位范围和外键拒绝后整库回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "SELECT count(*) FROM character_fiendwar_counter_stream") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123" &&
                    Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "178保留原角色、资产和版本，不补造历史破招");
        }
    }
}
