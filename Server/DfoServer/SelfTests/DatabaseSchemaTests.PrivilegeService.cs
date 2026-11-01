using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void PrivilegeServiceUpgrade(string root)
    {
        root = Path.Combine(root, "privilege-service"); Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "wrong-column", "wrong-check", "wrong-owner" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                      VALUES('old-privilege',0,'OldPrivilege',7,38,1,450,234,0,123);
                    DROP TABLE character_privilege_service;
                    PRAGMA user_version=176;
                    """);
                if (scenario != "ordinary")
                    Execute(db, scenario switch
                    {
                        "wrong-column" => PrivilegeServiceSchema.Definition.Replace("login_days", "unknown_days", StringComparison.Ordinal),
                        "wrong-check" => PrivilegeServiceSchema.Definition.Replace("expires_at>started_at", "expires_at>=started_at", StringComparison.Ordinal),
                        _ => PrivilegeServiceSchema.Definition.Replace(" REFERENCES characters(id) ON DELETE CASCADE", "", StringComparison.Ordinal),
                    });
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "礼遇未知列、弱化期限约束或缺外键拒绝后原库不变");
                continue;
            }
            _ = new CharacterRepository(path);
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_privilege_service") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123" &&
                    Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "176升级177只建空礼遇表，保留角色资产与revision且可重复打开");
        }
    }
}
