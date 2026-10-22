using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 开发GM扩展装备栏：只读查询、按位或开启1/2/16、保留8/32、幂等、revision与重开存档；仓储拒绝越权位与清除位。
internal static class DevelopmentEquipmentSlotTests
{
    public static void RunCore()
    {
        int before = Count;
        using var f = new MailboxPersistenceTests.Fixture();
        f.OtherOwner.Dispose();
        using (var db = new SqliteConnection("Data Source=" + f.Path + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE characters SET equipment_slot_flags=40 WHERE account='other'";
            That(command.ExecuteNonQuery() == 1, "夹具预置光环8与宠物幻化32");
        }
        string Answer(string line) => DevelopmentGm.Answer(line, "other", f.Repository, f.Owners, _ => null);
        JsonElement Json(string text)
        {
            That(text.StartsWith("OK ", StringComparison.Ordinal), "扩展装备栏命令成功: " + text);
            return JsonDocument.Parse(text[3..]).RootElement;
        }
        Character Load()
        {
            using var lease = f.Owners.Acquire("other", () => { });
            return f.Repository.LoadCharacter(lease, 0);
        }
        long revision = Load().Revision;
        foreach (string invalid in new[] { "equip-slots Other 0", "equip-slots Other 8", "equip-slots Other 32", "equip-slots Other 4", "equip-slots Other 27",
            "equip-slots Other 256", "equip-slots Other -1", "equip-slots Other x", "equip-slots Other 1 2", "equip-slots Nobody 1", "equip-slots" })
            That(Answer(invalid).StartsWith("ERR "), "扩展装备栏参数严格拒绝: " + invalid);
        That(Load().EquipmentSlotFlags == 40 && Load().Revision == revision, "拒绝的请求不写库、不递增revision");

        var read = Json(Answer("equip-slots Other"));
        That(read.GetProperty("flags").GetInt32() == 40 && !read.GetProperty("slots").GetProperty("support").GetBoolean() &&
            read.GetProperty("slots").GetProperty("aura").GetBoolean() && read.GetProperty("slots").GetProperty("creatureSkin").GetBoolean() &&
            Load().Revision == revision, "只读查询返回当前标志及各栏状态，不写库");

        var support = Json(Answer("equip-slots Other 1"));
        That(support.GetProperty("flags").GetInt32() == 41 && support.GetProperty("added").GetInt32() == 1 &&
            Load() is { EquipmentSlotFlags: 41 } one && one.Revision == revision + 1, "单独开启辅助装备位，保留8/32并递增revision");
        var combined = Json(Answer("equip-slots Other 19"));
        That(combined.GetProperty("flags").GetInt32() == 59 && combined.GetProperty("added").GetInt32() == 18 &&
            Load() is { EquipmentSlotFlags: 59 } all && all.Revision == revision + 2, "组合开启只追加尚未开启的魔法石与耳环位");
        var same = Json(Answer("equip-slots Other 2"));
        That(same.GetProperty("added").GetInt32() == 0 && !same.GetProperty("changed").GetBoolean() && Load().Revision == revision + 2,
            "已开启的位重复设置不提交、不递增revision");
        That(new CharacterRepository(f.Path) is var reopened && ReadFlags(reopened, f) == 59, "重新打开存档后标志保留");

        // 仓储层独立复核：GM原因只能追加1/2/16，不能借道开放幻化位或清除已有位。
        var current = Load();
        foreach (byte bad in new byte[] { (byte)(current.EquipmentSlotFlags & ~1), (byte)(current.EquipmentSlotFlags | 4), current.EquipmentSlotFlags })
        {
            var plan = new InventoryPlan(current.Inventory, current.Inventory, "gm_equipment_slot_flags", new(current)) { EquipmentSlotFlagsAfter = bad };
            bool refused = false;
            using (var lease = f.Owners.Acquire("other", () => { }))
                try { f.Repository.CommitInventory(lease, current, plan); }
                catch (InvalidOperationException) { refused = true; }
            That(refused && Load().EquipmentSlotFlags == 59 && Load().Revision == current.Revision, "仓储拒绝GM开栏越权或清除位: " + bad);
        }
        Console.WriteLine($"PASS {Count - before} 开发GM扩展装备栏核心断言");
    }

    static byte ReadFlags(CharacterRepository repository, MailboxPersistenceTests.Fixture f)
    {
        using var lease = f.Owners.Acquire("other", () => { });
        return repository.LoadCharacter(lease, 0).EquipmentSlotFlags;
    }
}
