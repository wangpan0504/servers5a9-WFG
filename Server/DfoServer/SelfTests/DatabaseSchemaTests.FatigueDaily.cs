using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void ServerItemUpgrade(string root)
    {
        root = Path.Combine(root, "server-items");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root,scenario+".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                // 物品周期两步在本线为 110/111，夹具基线取 110（疲劳日戳），升级时重跑这两步。
                Execute(db,"DROP TABLE account_server_item_refills; DROP TABLE character_server_item_usage; PRAGMA user_version=110");
                if (scenario == "conflict") Execute(db,"CREATE TABLE account_server_item_refills(incompatible INTEGER)");
                if (scenario == "late-failure") Execute(db,"ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before=Snapshot(path);
                RejectDatabase(()=>new CharacterRepository(path));
                Check(Snapshot(path)==before,"物品周期迁移冲突和晚期结构失败整笔回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            using(var db=Open(path))
                Check(Query(db,"SELECT count(*) FROM character_server_item_usage") is 0L &&
                    Query(db,"SELECT count(*) FROM account_server_item_refills") is 0L,"旧库新增空状态，不回补历史补给或邮件");
            string saved=Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path)==saved,"周期结构重开保持一致");
        }
    }

    static void FatigueDailyUpgrade(string root)
    {
        root = Path.Combine(root, "fatigue-daily");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "fatigue-daily-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES(1,'fatigue-old',0,'FatigueOld',7,38,1,450,234,0,123);
                    INSERT INTO character_fatigue VALUES(1,'fatigue-old',150,156,23,6,11,9);
                    DROP TABLE character_fatigue_reset_days;
                    PRAGMA user_version=109;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE character_fatigue_reset_days(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "疲劳日戳迁移冲突及末段失败回滚版本和原资产");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_fatigue_reset_days") is 0L &&
                    Query(db, "SELECT used||':'||maximum||':'||remaining FROM character_fatigue") is "150:156:6" &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123", "109升级只新增空日戳表，保留余额和角色资产");
                Execute(db, "INSERT INTO character_fatigue_reset_days VALUES(1,739916)");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved, "重开库不改变已保存疲劳服务日");
            using (var db = Open(path)) Execute(db, "DROP TABLE character_fatigue_reset_days");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
