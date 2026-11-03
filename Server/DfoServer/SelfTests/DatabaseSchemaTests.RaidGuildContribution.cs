using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RaidGuildContributionUpgrade(string root)
    {
        root = Path.Combine(root, "raid-guild-contribution");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    DROP INDEX character_raid_entry_clear;
                    ALTER TABLE character_raid_runs DROP COLUMN reward_epoch;
                    ALTER TABLE character_raid_runs DROP COLUMN entry_epoch;
                    ALTER TABLE character_raid_runs DROP COLUMN guild_joined_at;
                    ALTER TABLE character_raid_runs DROP COLUMN guild_id;
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('raid-guild-old',0,'RaidGuildOld',7,38,1,450,234,0,123);
                    INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state)
                    VALUES(1,'raid-guild-old','dddddddddddddddddddddddddddddddd',2,'fixture/guild-old',1,0);
                    PRAGMA user_version=167;
                    """);
                if (scenario == "conflict") Execute(db, "ALTER TABLE character_raid_runs ADD COLUMN guild_id TEXT");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE character_raid_runs ADD COLUMN guild_joined_at TEXT");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "168冲突或第二列不兼容使整笔迁移回滚，不留半列或提升版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT guild_id IS NULL AND guild_joined_at IS NULL FROM character_raid_runs") is 1L,
                    "168保留旧run空会籍，不按当前公会补造归属");
                Check(Query(db, "SELECT state||':'||source FROM character_raid_runs") is "0:fixture/guild-old" &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123", "168保留原生命周期和角色资产");
                Execute(db, "UPDATE character_raid_runs SET guild_id=0,guild_joined_at=0");
                Check(Query(db, "SELECT guild_id+guild_joined_at FROM character_raid_runs") is 0L,
                    "新入场可明确保存无会籍0/0");
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "168按连续版本升级");
            }
            string upgraded = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == upgraded, "168重开不追发贡献或改写原会籍");
        }
    }
}
