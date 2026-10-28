using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void TalismanUpgrade(string root)
    {
        root = Path.Combine(root, "talisman");
        Directory.CreateDirectory(root);
        const string beforeLimit = "CHECK(list_type IN (0,1,2,3,7,12,38))";
        const string afterLimit = "CHECK(list_type IN (0,1,2,3,7,12,38,46))";
        const string core = "{\"ItemId\":10605203,\"Kind\":2,\"Value\":2001,\"Extension97\":123,\"EquipmentLockId\":4}";
        string path = Path.Combine(root, "talisman-v89.db");
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE character_epic_production; DROP TABLE quest_eligibility_mail_dispatches; DROP TABLE character_pvp_training_skills; DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; DROP TABLE character_social_links; DROP TABLE account_social_links; DROP TABLE account_social_requests");
            Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('talisman',0,'LegacyRune',7,38,1,450,234,0,123)");
            Execute(db, $"INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(12345,1,'talisman','{core}')");
            Execute(db, "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'talisman',0,65,12345)");
            RemoveSkillTreeExpansionSchema(db);
            using var tx = db.BeginTransaction();
            SqliteMigrations.ReplaceTableConstraint(db, tx, "character_inventory_slots", afterLimit, beforeLimit);
            SqliteMigrations.Execute(db, tx, "PRAGMA user_version=89");
            tx.Commit();
        }
        _ = new CharacterRepository(path);
        using (var db = Open(path))
        {
            Check(Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) &&
                Query(db, "SELECT item_core FROM character_inventory_items WHERE item_uid=12345") is string preserved && preserved == core &&
                Query(db, "SELECT list_type||':'||slot_index||':'||item_uid FROM character_inventory_slots") is "0:65:12345" &&
                Query(db, "SELECT name||':'||revision||':'||gold FROM characters") is "LegacyRune:7:123",
                "护石v89升级只开放容器，不猜资源编号或改写旧实例、锁、角色与金币");
            Execute(db, "INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(12346,1,'talisman','{\"ItemId\":10605203,\"Kind\":14,\"Value\":1}')");
            Execute(db, "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'talisman',46,215,12346)");
            Check(Query(db, "SELECT slot_index FROM character_inventory_slots WHERE list_type=46") is 215L,
                "迁移后的正式槽表接受护石符文容器及最后装载槽");
            Reject<SqliteException>(() => Execute(db, "UPDATE character_inventory_slots SET list_type=47 WHERE item_uid=12346"),
                "护石迁移不放开未知物品容器");
        }
        string once = Snapshot(path);
        _ = new CharacterRepository(path);
        Check(Snapshot(path) == once, "护石迁移重开库不重复改写或分配实例");

        string corrupt = Path.Combine(root, "talisman-invalid-v89.db");
        _ = new CharacterRepository(corrupt);
        using (var db = Open(corrupt))
        {
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE character_epic_production; DROP TABLE quest_eligibility_mail_dispatches; DROP TABLE character_pvp_training_skills; DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; DROP TABLE character_social_links; DROP TABLE account_social_links; DROP TABLE account_social_requests");
            RemoveSkillTreeExpansionSchema(db);
            using var tx = db.BeginTransaction();
            SqliteMigrations.ReplaceTableConstraint(db, tx, "character_inventory_slots", afterLimit,
                "CHECK(list_type IN (0,1,2,3,7,12,38,45))");
            SqliteMigrations.Execute(db, tx, "PRAGMA user_version=89");
            tx.Commit();
        }
        string rejected = Snapshot(corrupt);
        Reject<InvalidDataException>(() => new CharacterRepository(corrupt), "护石迁移拒绝未确认的旧容器约束");
        Check(Snapshot(corrupt) == rejected, "护石迁移失败保持旧DDL、版本与数据完整");
    }
}
