using DfoServer.Persistence;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void VariousBuffUpgrade(string root)
    {
        root = Path.Combine(root, "various-buff-upgrade");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('various',0,'Various',7,38,1,450,234,0,777)");
            Execute(db, "DROP TABLE character_various_buffs; PRAGMA user_version=203");
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Query(db, "SELECT count(*) FROM character_various_buffs") is 0L && Query(db, "SELECT gold FROM characters") is 777L &&
                Query(db, "SELECT revision FROM characters") is 7L, "v203增量建立空Buff账本，不改角色版本、余额或补发资格");
            string json = JsonSerializer.Serialize(new[] { new DfoServer.Game.Inventory.Models.VariousBuffState(91001, 10008219,
                DfoServer.Game.Inventory.Models.VariousBuffLifetime.Timed, 1900000600000, 0) });
            using var command = db.CreateCommand(); command.CommandText = "INSERT INTO character_various_buffs VALUES(1,'various',$json)";
            command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
        }
        string before = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == before, "重开新Buff库不重建状态、改期限或重新抽选");
        using (var db = Open(path)) Execute(db, "DROP TABLE character_various_buffs; CREATE TABLE character_various_buffs(character_id INTEGER PRIMARY KEY,account TEXT,buffs TEXT); PRAGMA user_version=203");
        before = Snapshot(path);
        Reject<InvalidDataException>(() => _ = new CharacterRepository(path), "未知同名Buff表不被CREATE IF NOT EXISTS冒充迁移成功");
        Check(Snapshot(path) == before, "Buff迁移末段拒绝整体回滚DDL和版本，不改原角色数据");
    }
}
