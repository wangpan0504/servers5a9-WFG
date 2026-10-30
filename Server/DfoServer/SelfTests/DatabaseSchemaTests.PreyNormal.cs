using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void PreyNormalUpgrade(string root)
    {
        root = Path.Combine(root,"prey-normal-progress");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root,"prey-normal-"+scenario+".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db,"""
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('prey-old',0,'PreyOld',7,118,0,450,234,0,123);
                    DROP TABLE character_prey_normal_progress;
                    PRAGMA user_version=159;
                    """);
                if (scenario == "conflict") Execute(db,"CREATE TABLE character_prey_normal_progress(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db,"ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before,"普雷迁移结构冲突及晚期失败整笔回滚DDL与版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db,"SELECT count(*) FROM character_prey_normal_progress") is 0L &&
                    Query(db,"SELECT revision||':'||gold FROM characters") is "7:123","159升级只建立空进度，保留资产");
                Execute(db,"INSERT INTO character_prey_normal_progress SELECT id,account,20261031,3,4 FROM characters");
                foreach (string change in new[] { "progress=-1","progress=6","progress=1.5","branch=0","branch=7","account='other'" })
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db,"UPDATE character_prey_normal_progress SET "+change),
                        "普雷日进度约束拒绝越界及跨账号写入");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved,"重开恢复原日进度，不重复迁移");
            var repository = new CharacterRepository(path);
            using var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire("prey-old",() => { });
            repository.Delete(owner,repository.LoadDeletionTarget(owner,0));
            using (var db = Open(path))
                Check(Query(db,"SELECT count(*) FROM character_prey_normal_progress") is 0L,
                    "删除角色在原删除事务清理非空普雷进度，不遗留外键");
        }
    }
}
