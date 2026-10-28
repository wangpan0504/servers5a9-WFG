using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void PvpRatingUpgrade(string root)
    {
        root = Path.Combine(root, "pvp-rating");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "state-conflict", "claim-conflict", "late-failure" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                // 回退点 132 高于团本生命周期 109，该步不会重跑，团本表须保持当前结构。
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('rating-old',0,'RatingOld',7,38,1,450,234,0,123);
                    INSERT INTO pvp_arena_match_results VALUES('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','{}',0);
                    DROP TABLE character_pvp_rating_claims;
                    DROP TABLE character_pvp_ratings;
                    PRAGMA user_version=132;
                    """);
                if (scenario == "state-conflict") Execute(db, "CREATE TABLE character_pvp_ratings(unknown INTEGER)");
                if (scenario == "claim-conflict") Execute(db, "CREATE TABLE character_pvp_rating_claims(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "评分迁移中途冲突或末段失败须回滚全部DDL、索引和版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_pvp_ratings") is 0L &&
                    Query(db, "SELECT count(*) FROM character_pvp_rating_claims") is 0L &&
                    Query(db, "SELECT count(*) FROM pvp_arena_match_results") is 1L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "132升级仅建空评分及来源表，保留旧事实和资产，不补发历史奖励");
                Execute(db, "INSERT INTO character_pvp_ratings SELECT id,account,1,'{}' FROM characters");
                foreach (string change in new[] { "revision=0", "revision=1.5", "state_json='[]'", "account='wrong-owner'" })
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db,
                        "UPDATE character_pvp_ratings SET " + change), "评分版本、JSON形状和角色归属受数据库约束");
                Execute(db, "INSERT INTO character_pvp_rating_claims(match_id,character_id,account,rating_mode,outcome) SELECT 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',id,account,0,0 FROM characters");
                foreach (string change in new[] { "rating_mode=3", "outcome=4", "receipt_json='[]'", "match_id='absent'", "account='wrong-owner'" })
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db,
                        "UPDATE character_pvp_rating_claims SET " + change), "结算来源模式、胜负、回执及双重外键均受约束");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "重开库保留评分及来源，不重置或重复升级");
            using (var db = Open(path)) Execute(db, "DROP TABLE character_pvp_ratings");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }

    static void PvpArenaResultUpgrade(string root)
    {
        root = Path.Combine(root, "pvp-arena-results");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                // 回退点 131 高于团本生命周期 109，该步不会重跑，团本表须保持当前结构。
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('arena-old',0,'ArenaOld',7,38,1,450,234,0,123);
                    DROP TABLE pvp_arena_match_results;
                    PRAGMA user_version=131;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE pvp_arena_match_results(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "决斗回执迁移冲突或末段失败整体回滚DDL、版本和角色资产");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM pvp_arena_match_results") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "131升级仅建空整局回执表，不补历史或改写角色资产");
                Execute(db, "INSERT INTO pvp_arena_match_results VALUES('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','{}',0)");
                foreach (string change in new[] { "match_id=NULL", "match_id='x'", "match_id='00000000000000000000000000000000'",
                    "result_json='[]'", "finished_at=-1" })
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db,
                        "UPDATE pvp_arena_match_results SET " + change), "决斗回执标识、JSON形状及时间边界受数据库约束");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "重开库保留整局回执，不重复执行迁移");
            using (var db = Open(path)) Execute(db, "DROP TABLE pvp_arena_match_results");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
