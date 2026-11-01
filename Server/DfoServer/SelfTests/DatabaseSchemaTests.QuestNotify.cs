using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void QuestNotifyUpgrade(string root)
    {
        root = Path.Combine(root, "quest-notify"); Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "wrong-column", "wrong-check", "wrong-owner" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                      VALUES('old-notify',0,'OldNotify',7,38,1,450,234,0,123);
                    DROP TABLE character_quest_notify;
                    PRAGMA user_version=174;
                    """);
                if (scenario != "ordinary")
                    Execute(db, scenario switch
                    {
                        "wrong-column" => SqliteMigrations.QuestNotifySchema.Replace("entries", "unknown", StringComparison.Ordinal),
                        "wrong-check" => SqliteMigrations.QuestNotifySchema.Replace("length(entries)=16", "length(entries)=15", StringComparison.Ordinal),
                        _ => SqliteMigrations.QuestNotifySchema.Replace("FOREIGN KEY(character_id,account) REFERENCES characters(id,account)",
                            "FOREIGN KEY(character_id) REFERENCES characters(id)", StringComparison.Ordinal),
                    });
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "175未知列、约束或账号外键拒绝后版本与玩家数据不变");
                continue;
            }
            _ = new CharacterRepository(path);
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_quest_notify") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123" &&
                    Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "174升级175只建空书签表，保留角色资产与revision且可重复打开");
        }
    }
}
