using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void ItemMakingUpgrade(string root)
    {
        root = Path.Combine(root, "item-making");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "item-making.db");
        var repository = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("making-test", () => { });
        var role = repository.Create(owner, new NewCharacter("Making", new(38, 1, 450, 234), null)
            { Inventory = InventorySnapshot.Empty with { Gold = 1000 } });
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT count(*) FROM item_making_skill_info") is 0L, "新角色默认零熟练度，不额外写空行");
            RemoveSkillTreeExpansionSchema(db);
            Execute(db, "DROP TABLE item_making_skill_info; DROP TABLE character_weekly_dungeon; PRAGMA user_version=75");
            using var tx = db.BeginTransaction();
            ItemMakingSkillSchema.Upgrade(db, tx);
            SqliteMigrations.Execute(db, tx, "PRAGMA user_version=77");
            tx.Rollback();
            Check(Query(db, "PRAGMA user_version") is 75L &&
                Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='item_making_skill_info'") is 0L,
                "制作表创建和版本号在迁移失败时一起回滚");
        }
        repository = new(path);
        var loaded = repository.LoadCharacter(owner, role.Slot);
        Check(JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(role) &&
            QueryOn(path, "SELECT count(*) FROM item_making_skill_info") is 0L &&
            Convert.ToInt32(QueryOn(path, "PRAGMA user_version")) == SqliteMigrations.CurrentVersion,
            "真实v75升级保留资产、角色revision及进度，不补造历史制作经验");
        var after = loaded.Inventory with { ItemMakingSkills = loaded.Inventory.ItemMakingSkills.Set(2, 19) };
        role = repository.CommitInventory(owner, loaded, new(loaded.Inventory, after, "making_test"));
        Check(new CharacterRepository(path).LoadCharacter(owner, role.Slot).Inventory.ItemMakingSkills.Points[2] == 19 &&
            role.Revision == loaded.Revision + 1, "制作经验沿现有库存事务和revision保存，重开恢复");
        using (var db = Open(path))
        {
            Reject<SqliteException>(() => Execute(db, "UPDATE item_making_skill_info SET leather=-1"), "制作点数拒绝负值");
            Reject<SqliteException>(() => Execute(db, "UPDATE item_making_skill_info SET leather=1001"), "制作点数上限1000");
            Reject<SqliteException>(() => Execute(db, "UPDATE item_making_skill_info SET account='other'"), "制作经验不允许跨账号归属");
            Execute(db, "CREATE TRIGGER making_fail AFTER UPDATE ON item_making_skill_info BEGIN SELECT RAISE(ABORT,'making rollback'); END");
        }
        string previous = JsonSerializer.Serialize(role);
        after = role.Inventory with { Gold = 900, ItemMakingSkills = role.Inventory.ItemMakingSkills.Set(2, 20) };
        Reject<SqliteException>(() => repository.CommitInventory(owner, role, new(role.Inventory, after, "making_late_fail")),
            "制作经验晚期SQL失败向外层传播");
        Check(JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot)) == previous,
            "经验写入失败不能单独扣钱、改资产或revision");
        using (var db = Open(path))
        {
            Execute(db, "DROP TRIGGER making_fail; UPDATE item_making_skill_info SET leather=18");
        }
        Reject<InvalidOperationException>(() => repository.CommitInventory(owner, role, new(role.Inventory, after, "making_stale")),
            "同revision的熟练度变化也使旧规划失效");
        repository.Delete(owner, repository.LoadDeletionTarget(owner, role.Slot));
        Check(QueryOn(path, "SELECT count(*) FROM item_making_skill_info") is 0L, "删除角色级联清理自身制作经验");
        using (var db = Open(path))
            Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "制作表完整性和外键检查正常");

        string collision = Path.Combine(root, "item-making-collision.db");
        _ = new CharacterRepository(collision);
        using (var db = Open(collision))
        {
            RewindRaidLifecycle(db);
            Execute(db, "DROP TABLE item_making_skill_info; CREATE TABLE item_making_skill_info(unknown INTEGER); PRAGMA user_version=76");
        }
        Reject<InvalidDataException>(() => _ = new CharacterRepository(collision), "未知同名表不能被迁移当作合法经验覆盖");
        Check(QueryOn(collision, "PRAGMA user_version") is 76L, "结构冲突不伪造已升级版本");

        string preview = Path.Combine(root, "item-making-preview.db");
        var oldRepository = new CharacterRepository(preview);
        var oldRole = oldRepository.Create(owner, new NewCharacter("MakingOld", new(38, 1, 450, 234), null)
            { Inventory = InventorySnapshot.Empty with { Gold = 700, ItemMakingSkills = ItemMakingSkillState.Empty.Set(2, 3) } });
        using (var db = Open(preview))
        {
            RemoveSkillTreeExpansionSchema(db);
            Execute(db, "DROP TABLE character_weekly_dungeon; PRAGMA user_version=76");
        }
        oldRepository = new(preview);
        Check(JsonSerializer.Serialize(oldRepository.LoadCharacter(owner, oldRole.Slot)) == JsonSerializer.Serialize(oldRole) &&
            QueryOn(preview, "SELECT count(*) FROM character_weekly_dungeon") is 0L &&
            Convert.ToInt32(QueryOn(preview, "PRAGMA user_version")) == SqliteMigrations.CurrentVersion,
            "精确已知的v76制作测试库补齐正式每周表后升级，保留已实测的3点、资产和revision");
        string stable = Snapshot(preview);
        _ = new CharacterRepository(preview);
        Check(stable == Snapshot(preview), "兼容升级重开幂等，不重复授予熟练度");
    }
}
