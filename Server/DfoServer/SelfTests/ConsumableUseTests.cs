using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ConsumableUseTests
{
    internal static RecoveryConsumableDefinition Hp { get; } = new(1108, 16, 10000, 0, new("+", 410, 1000, "myself"), null);
    internal static RecoveryConsumableDefinition Mp { get; } = new(1114, 16, 10000, 1, null, new("+", 330, 1000, "myself"));

    public static void RunCore()
    {
        byte[] body = [65, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0, 0, 0, 0, 0];
        var request = ConsumableUseRequests.Read(body);
        That(request.Position == new InventorySlot(InventoryListType.Main, 65) && request.OpaqueCheck == 0x89ABCDEF && request.ItemId == 1108 && request.Argument == 0,
            "普通44正文按已证15B独立字段解析，不把opaque当时间戳或数量");
        Bytes(ConsumableUsePackets.Ack(request, true), [1, 65, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0], "成功ACK44完整12B保留原slot/list/check/itemId");
        Bytes(ConsumableUsePackets.Ack(request, false), [0, 123, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0], "失败ACK44完整12B用error替代slot，仍保留原核验字段");
        foreach (var bad in new[] { body[..10], body[..14], body.Concat(new byte[] { 0 }).ToArray() })
            Reject<InvalidDataException>(() => ConsumableUseRequests.Read(bad), "普通44不截断兼容特殊宽度或未知尾");
        request = request with { OpaqueCheck = 0x1234 };
        var service = new InventoryConsumableUseService(new([Hp, Mp]));
        var declaration = new StackableFile(1108, "stackable/synthetic.stk", new("[waste]", [4]), 1000, 400, null, 16, "potion", [])
        { DeclaredUsableJobs = ["[all]"], HasUsableJobs = true };
        var raw = new RecoveryConsumableFile(1108, declaration.ScriptPath, declaration, 10000, 0,
            new("+", 410, 1000, "myself"), null, ["stackable type", "usable job", "minimum level", "cool time", "cooltime group", "hp recovery"], []);
        That(RecoveryConsumableCatalog.Read(raw) == Hp && RecoveryConsumableCatalog.Read(raw with { DeclaredFields = raw.DeclaredFields.Add("appendage") }) is null &&
            RecoveryConsumableCatalog.Read(raw with { HpRecovery = new("+", 410, 1000, "party") }) is null &&
            RecoveryConsumableCatalog.Read(raw with { Diagnostics = ["duplicate"] }) is null,
            "即使已开放ID也须完整原声明，新增副作用、其他目标或原格式诊断不获资格");
        var core = new ItemCore(1108, ItemKind.Consumable, 2) { SortLockFlag = 1, Marker16 = 44, ExpireTime = 2000 };
        Character character = new(1, 0, "Potion", 7, new(1, 0, 10, 10), null)
        {
            Level = 16, Inventory = new([new(11, request.Position, core)])
        };
        var plan = service.Plan(character, request, 1000).Plan!;
        That(plan.After.Items.Single() == new InventoryItem(11, request.Position, core with { Value = 1 }) && plan.CharacterPrecondition!.Matches(character),
            "单件消费仅改原指定UID数量，保留排序锁、期限和全部核心属性及角色资格");
        var last = service.Plan(character with { Inventory = plan.After }, request with { OpaqueCheck = 0 }, 1000);
        That(last.Accepted && last.Plan!.After.Items.IsEmpty, "最后一件清槽且opaque0是合法普通请求");
        That(service.Plan(character, request with { OpaqueCheck = 32767 }, 1000).Accepted &&
            !service.Plan(character, request with { OpaqueCheck = 32768 }, 1000).Accepted,
            "首批普通核验值范围0..32767，不把未知producer高值当成功入口");
        foreach (var invalid in new[] { request with { ItemId = 1114 }, request with { ItemId = 1016 }, request with { Argument = 1 },
            request with { Position = new(InventoryListType.PersonalCargo, 0) }, request with { Position = new(InventoryListType.Main, 1) } })
            That(!service.Plan(character, invalid, 1000).Accepted, "错误源模板、未知药型、参数或持有域拒绝");
        foreach (var invalid in new[] { character with { Level = 15 }, character with { Inventory = InventorySnapshot.Empty },
            character with { Inventory = new([new(11, request.Position, core with { EquipmentLockId = 1 })]) },
            character with { Inventory = new([new(11, request.Position, core with { ExpireTime = 999 })]) } })
            That(!service.Plan(invalid, request, 1000).Accepted, "等级、空槽、安全锁和过期状态不因Consumable类别取得使用资格");
        var shortcut = character with { Inventory = new([new(11, new(InventoryListType.Main, 3), core)]) };
        That(service.Plan(shortcut, request with { Position = new(InventoryListType.Main, 3) }, 1000).Accepted,
            "普通快捷栏是实际源槽，沿同一单件消费规则");
    }

    public static void RunResources(ItemResources resources)
    {
        That(resources.RecoveryConsumables.TryGet(1108, out var hp) && hp == Hp &&
            resources.RecoveryConsumables.TryGet(1114, out var mp) && mp == Mp,
            "匹配PVF两种药保留最低等级、冷却原值/组和完整恢复四元组");
        That(resources.RecoveryConsumables.TryGet(1107,out var lowHp) &&
            lowHp == new RecoveryConsumableDefinition(1107,6,10000,0,new("+",180,1000,"myself"),null) &&
            resources.RecoveryConsumables.TryGet(1065,out var flower) &&
            flower == new RecoveryConsumableDefinition(1065,1,10000,1,null,new("+",115,1000,"myself")),
            "现场1107入门HP药剂及1065魔力之花严格匹配完整PVF恢复声明");
        foreach (int id in new[] { 1016, 1047, 1051 })
            That(!resources.RecoveryConsumables.TryGet(id, out _), "额外材料、持续效果、随机及异常状态药不泛放");
        foreach (int id in new[] { 1065, 1107, 1108, 1114 })
            That(resources.Items.Get(id).Kind == ItemKind.Consumable && resources.Items.Get(id).MinimumLevel ==
                (id == 1065 ? 1 : id == 1107 ? 6 : 16),
                "使用目录与现有物品目录实际类别/等级一致，不能替代库存实例校验");
    }
}
