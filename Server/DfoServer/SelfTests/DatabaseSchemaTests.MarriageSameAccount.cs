using System.Text.Json;
using DfoServer.Game.Marriage;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void MarriageSameAccountUpgrade(string root)
    {
        root = Path.Combine(root, "marriage-same-account");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "late-failure", "unknown-shape", "newer" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            var active = MarriageDirectRules.Create(Guid.NewGuid(), 31, 1, 2, true, false, 1000);
            var history = active with { Id = Guid.NewGuid(), ContextId = 32, State = 0, EndedAt = 2000 };
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES(1,'first',0,'First',7,38,1,450,234,0,123),(2,'second',0,'Second',8,38,1,450,234,0,456),
                          (3,'same',0,'Third',9,38,1,450,234,0,789),(4,'same',1,'Fourth',10,38,1,450,234,0,321);
                    """);
                Insert(db, active, "first", "second");
                Insert(db, history, "first", "second");
                Execute(db, $"INSERT INTO marriage_active_members VALUES(1,'{active.Id:N}'),(2,'{active.Id:N}')");
                using (var write = db.BeginWrite("DatabaseSchemaTests.PreviousMarriage"))
                {
                    SqliteMigrations.ReplaceTableConstraint(db, write.Transaction, "marriage_records", "CHECK(length(second_account)>0)",
                        "CHECK(length(second_account)>0 AND second_account<>first_account)");
                    write.Transaction.Commit();
                }
                Execute(db, "PRAGMA user_version=175");
                if (scenario == "late-failure") Execute(db, "DROP TABLE character_completed_quests");
                if (scenario == "unknown-shape") Execute(db, "ALTER TABLE marriage_records ADD COLUMN unknown_marriage_column INTEGER");
                if (scenario == "newer") Execute(db, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "同账号婚姻迁移未知结构、晚校验失败或较新版本均无存档变化");
                continue;
            }
            _ = new CharacterRepository(path);
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM marriage_records") is 2L &&
                    Query(db, "SELECT count(*) FROM marriage_active_members") is 2L,
                    "175升176及重开保留当前与历史婚姻、双方有效索引");
                using (var query = db.CreateCommand())
                {
                    query.CommandText = "SELECT aggregate_json FROM marriage_records WHERE marriage_id=$id";
                    query.Parameters.AddWithValue("$id", active.Id.ToString("N"));
                    Check(Equals(query.ExecuteScalar(), JsonSerializer.Serialize(active)), "婚姻迁移不重写聚合JSON、版本或关系身份");
                }
                Check(Query(db, "SELECT revision||':'||gold FROM characters WHERE id=1") is "7:123" &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name IN('marriage_active_relation','marriage_first_history','marriage_second_history')") is 3L &&
                    Query(db, "PRAGMA foreign_key_check") is null,
                    "婚姻迁移保留角色资产、三个索引与有效外键");
                var same = MarriageDirectRules.Create(Guid.NewGuid(), 33, 3, 4, true, false, 3000);
                Insert(db, same, "same", "same");
                Execute(db, $"INSERT INTO marriage_active_members VALUES(3,'{same.Id:N}'),(4,'{same.Id:N}')");
                RejectDatabase(() => Execute(db, $"INSERT INTO marriage_active_members VALUES(3,'{active.Id:N}')"));
                Check(Query(db, "SELECT count(*) FROM marriage_active_members") is 4L, "同账号关系仍受成员唯一性限制");
            }
            var reopened = new CharacterRepository(path);
            Check(reopened.FindMarriage(3)?.SecondId == 4 && reopened.FindMarriage(4)?.FirstId == 3 &&
                reopened.FindMarriage(1)?.Id == active.Id, "升级后同账号关系与既有关系均可按双方身份重新装载");
        }

        static void Insert(SqliteConnection db, MarriageRecord record, string firstAccount, string secondAccount)
        {
            using var query = db.CreateCommand();
            query.CommandText = """
                INSERT INTO marriage_records(marriage_id,context_id,revision,first_id,second_id,first_account,second_account,state,created_at,ended_at,aggregate_json)
                VALUES($id,$context,$revision,$first,$second,$firstAccount,$secondAccount,$state,$created,$ended,$json)
                """;
            query.Parameters.AddWithValue("$id", record.Id.ToString("N"));
            query.Parameters.AddWithValue("$context", record.ContextId);
            query.Parameters.AddWithValue("$revision", record.Revision);
            query.Parameters.AddWithValue("$first", record.FirstId);
            query.Parameters.AddWithValue("$second", record.SecondId);
            query.Parameters.AddWithValue("$firstAccount", firstAccount);
            query.Parameters.AddWithValue("$secondAccount", secondAccount);
            query.Parameters.AddWithValue("$state", record.State);
            query.Parameters.AddWithValue("$created", record.CreatedAt);
            query.Parameters.AddWithValue("$ended", record.EndedAt);
            query.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record));
            query.ExecuteNonQuery();
        }
    }
}
