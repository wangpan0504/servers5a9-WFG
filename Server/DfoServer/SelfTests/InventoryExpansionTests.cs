using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class InventoryExpansionTests
{
    static readonly ItemTemplate Template = new(10152158, ItemKind.Consumable, 0, 1, 0, 1000, 0, 2, 1,
        "[account]", ushort.MaxValue, 0, "背包扩展券", "[etc] 0");
    static RecoveryConsumableFile File() => new(Template.Id, "coupon.stk",
        new(Template.Id, "coupon.stk", new("[etc]", [0]), 1000, null, null, 1, null, [])
        {
            HasActionType = true, ActionDeclaration = new("[upgrade inventory]", []),
            HasActionUsablePlace = true, DeclaredActionUsablePlaces = ["[village]", "[seria room]"],
            DeclaredNeededMaterials = [490704332, 300]
        }, null, null, null, null, ["need material"], []);

    public static void RunCore()
    {
        var raw = File();
        var definition = InventoryExpansionDefinition.Parse(raw, Template)!;
        That(definition is not null, "背包券允许村庄与赛丽亚的并集，商城兑换价不是使用成本");
        foreach (var bad in new[] { raw with { Stackable = raw.Stackable with { ActionDeclaration = new("[upgrade inventory]", [8]) } },
            raw with { Stackable = raw.Stackable with { HasUseActionPacket = true, DeclaredUseActionPacket = 1 } },
            raw with { DeclaredFields = ["usable first growtype"] }, raw with { StructureDiagnostics = ["invalid"] } })
            That(InventoryExpansionDefinition.Parse(bad, Template) is null, "未知参数、发包模式和额外使用资格不能被忽略");
        That(InventoryExpansionDefinition.Parse(raw with { Stackable = raw.Stackable with
            { DeclaredActionUsablePlaces = ["[dungeon]", "[unknown]"] } }, Template) is not null,
            "地点类型和数量不用于判断扩展券身份");
        That(InventoryExpansionRequests.Read([65, 0]) == 65 && InventoryExpansionRequests.Read([255, 255]) == 65535,
            "430严格读取u16，不将未知付费哨兵当作真实源槽");
        foreach (int length in new[] { 0, 1, 3, 19 })
            Reject<InvalidDataException>(() => InventoryExpansionRequests.Read(new byte[length]), "拒绝旧格式和附加参数");
        That(InventoryExpansionPackets.Ack(InventoryResult.Reject(0)).SequenceEqual(new byte[] { 0, 0, 0 }) &&
            InventoryExpansionPackets.Ack(new(true, 0)).SequenceEqual(new byte[] { 1 }), "公共ACK成功1B失败3B，不能省掉失败error");
        var ticket = new InventoryItem(9, new(InventoryListType.Main, 65), new(Template.Id, ItemKind.Consumable, 4) { SortLockFlag = 1 });
        var role = new Character(1, 0, "Expand", 0, new(1, 0, 10, 10), null) { Inventory = new([ticket], 0, 1234) };
        for (ushort expansion = 0; expansion < 24; expansion += 8)
        {
            var result = InventoryExpansionService.Plan(role, 65, definition, 1000);
            That(result.Accepted && result.Plan!.After.Expansion == expansion + 8 && result.Plan.After.Gold == 1234 &&
                result.Plan.After.Find(ticket.Position)?.Core.Count == 3 - expansion / 8 &&
                result.Plan.After.Find(ticket.Position)?.Core.SortLockFlag == 1 && result.Plan.After.Find(ticket.Position)?.Uid == 9,
                "每次当前快照只扣一张升一档，保留UID、排序锁和金币");
            role = role with { Inventory = result.Plan!.After };
            That(ItemSlotBoundService.Allows(role.Inventory, new(InventoryListType.Main, (short)(96 + expansion + 8)), ItemKind.Consumable) &&
                !ItemSlotBoundService.Allows(role.Inventory, new(InventoryListType.Main, (short)(97 + expansion + 8)), ItemKind.Consumable),
                "新增槽只到当前档边界，不开放下一档或跨页");
        }
        string before = JsonSerializer.Serialize(role);
        That(!InventoryExpansionService.Plan(role, 65, definition, 1000).Accepted && JsonSerializer.Serialize(role) == before,
            "满档重发保持剩余券和全部资产不变");
        role = role with { Inventory = new([ticket], 0) };
        foreach (var invalid in new[] { ticket with { Core = ticket.Core with { EquipmentLockId = 1 } },
            ticket with { Core = ticket.Core with { ExpireTime = 999 } }, ticket with { Core = ticket.Core with { ItemId = 4 } },
            ticket with { Core = ticket.Core with { Kind = ItemKind.Material } }, ticket with { Uid = 0 } })
            That(!InventoryExpansionService.Plan(role with { Inventory = new([invalid], 0) }, 65, definition, 1000).Accepted,
                "锁定、过期、错源身份、错类或未提交实例均不扣券");
        That(!InventoryExpansionService.Plan(role, 65535, definition, 1000).Accepted &&
            !InventoryExpansionService.Plan(role, 66, definition, 1000).Accepted &&
            !InventoryExpansionService.Plan(role, 65, null, 1000).Accepted && new InventorySnapshot([]).Expansion == 24,
            "错槽、未知付费入口和非券拒绝，默认满扩展24不变");
        var qualified = definition! with { Item = Template with { MinimumLevel = 10, MaximumLevel = 20,
            Jobs = 2, RequiredSkill = 999 } };
        foreach (var sample in new[]
        {
            role with { Job = 1, Level = 9, Skills = [new(999, 1)] },
            role with { Job = 1, Level = 21, Skills = [new(999, 1)] },
            role with { Job = 0, Level = 10, Skills = [new(999, 1)] },
            role with { Job = 1, Level = 10, Skills = [new(999, 0)] }
        }) That(!InventoryExpansionService.Plan(sample, 65, qualified, 1000).Accepted,
            "合成模板的等级上下界、职业与技能不足均拒绝，不改变真实资源目录");
        That(InventoryExpansionService.Plan(role with { Job = 1, Level = 10, Skills = [new(999, 1)] },
            65, qualified, 1000).Accepted, "完整合成模板资格满足时允许规划");
        RunRepository(ticket, definition!);
    }

    static void RunRepository(InventoryItem ticket, InventoryExpansionDefinition definition)
    {
        string folder = Path.Combine(Path.GetTempPath(), "s5a9-inventory-expansion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("expand", () => { });
            var location = new DfoServer.GameWorld.Towns.Location(1, 0, 10, 10);
            var role = repository.Create(owner, new("Expand", location, location)
                { Inventory = new([ticket with { Uid = 0 }], 0, 1234) });
            var planned = InventoryExpansionService.Plan(role, 65, definition, 1000);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_expansion AFTER INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='inventory_expansion_coupon' BEGIN SELECT RAISE(ABORT,'expansion rollback'); END";
            command.ExecuteNonQuery();
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, role, planned), "后段SQL失败回滚容量和消费");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot)) == JsonSerializer.Serialize(role),
                "真实回滚保留角色revision、金币、容量与完整实例");
            command.CommandText = "DROP TRIGGER fail_expansion"; command.ExecuteNonQuery();
            var committed = InventoryService.Commit(repository, owner, role, planned);
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, role, planned), "同一旧revision计划不能再次扣券");
            var loaded = new CharacterRepository(path).LoadCharacter(owner, role.Slot);
            That(loaded.Inventory.Expansion == 8 && loaded.Inventory.Find(ticket.Position)?.Core.Count == 3 &&
                loaded.Revision == committed.Revision, "既有schema重读容量和券数量，不要求迁移");
            var staleGeneration = InventoryExpansionService.Plan(committed, 65, definition, 1000);
            using var replacement = owners.Acquire("expand", () => { });
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, committed, staleGeneration),
                "接管后旧owner的已规划消费不能提交");
            That(repository.LoadCharacter(replacement, role.Slot).Inventory.Expansion == 8,
                "接管拒绝不修改新owner的容量");
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(folder, recursive: true); }
    }

    public static void RunResources(ItemCatalog items)
    {
        int[] ids = [10151976, 10152158, 10153881, 10154424, 10155824, 10158510, 10160883, 10163533, 10163923, 10164965, 10301463, 10302996, 10303815, 10304281, 10304364, 10305441, 10307289, 10308255, 10309807, 490001777, 490001976, 490002123, 490002829, 490003088, 490003335, 490003555, 490004055, 490004197, 490004741, 490005059, 490005559, 490006550, 490006860, 490007005, 490006980, 490007302, 490007311, 490007801, 490007899, 490008261, 490008530, 490009309, 490009389, 490010423, 490013842, 490700946, 490701116, 490701690, 490703203, 490703683, 490703958, 490704342, 490704516, 490705128];
        That(ids.All(id => items.InventoryExpansion(id) is not null), "匹配PVF的54份背包券均接入明确资源资格");
    }
}
