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
    internal static RecoveryConsumableDefinition Hp { get; } = new(1108, 16, 10000, 0);
    internal static RecoveryConsumableDefinition Mp { get; } = new(1114, 16, 10000, 1);

    public static void RunCore()
    {
        byte[] body = [65, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0, 0, 0, 0, 0];
        var request = ConsumableUseRequests.Read(body);
        That(request.Position == new InventorySlot(InventoryListType.Main, 65) && request.OpaqueCheck == 0x89ABCDEF && request.ItemId == 1108 && request.Argument == 0,
            "普通44正文按已证15B独立字段解析，不把opaque当时间戳或数量");
        Bytes(ConsumableUsePackets.Ack(request, true), [1, 65, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0], "成功ACK44完整12B保留原slot/list/check/itemId");
        Bytes(ConsumableUsePackets.Ack(request, false), [0, 123, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0], "失败ACK44完整12B用error替代slot，仍保留原核验字段");
        Bytes(ConsumableUsePackets.Ack(request, true, noConsumption: true),
            [0, 0, 0, 0, 0xEF, 0xCD, 0xAB, 0x89, 0x54, 4, 0, 0], "原生免扣保留本版u16零错误码、list及token，不传数量或套旧端11B");
        Bytes(ConsumableUsePackets.Ack(request, false, noConsumption: true), ConsumableUsePackets.Ack(request, false),
            "免耗模式不把非法源或冷却拒绝伪装为业务已接受");
        foreach (var bad in new[] { body[..10], body[..14], body.Concat(new byte[] { 0 }).ToArray() })
            Reject<InvalidDataException>(() => ConsumableUseRequests.Read(bad), "普通44不截断兼容特殊宽度或未知尾");
        request = request with { OpaqueCheck = 0x1234 };
        var service = new InventoryConsumableUseService(new([Hp, Mp]));
        var declaration = new StackableFile(1108, "stackable/synthetic.stk", new("[waste]", [4]), 1000, 400, null, 16, "potion", [])
        { DeclaredUsableJobs = ["[all]"], HasUsableJobs = true };
        var raw = new RecoveryConsumableFile(1108, declaration.ScriptPath, declaration, 10000, 0,
            new("+", 410, 1000, "myself"), null, ["stackable type", "usable job", "minimum level", "cool time", "cooltime group", "hp recovery"], []);
        That(RecoveryConsumableCatalog.Read(raw) == Hp && RecoveryConsumableCatalog.Read(raw with { DeclaredFields = raw.DeclaredFields.Add("appendage") }) == Hp &&
            RecoveryConsumableCatalog.Read(raw with { HpRecovery = new("%", 95, 333, "party") }) == Hp &&
            RecoveryConsumableCatalog.Read(raw with { HpRecovery = null, Diagnostics = ["客户端效果形状不由服务端核验"] }) == Hp,
            "服务端只取使用资格及冷却，恢复数值、目标、附加效果和客户端效果格式不构成使用白名单");
        foreach (int subtype in new[] { 0, 1, 4, 5, 6, 99 })
        {
            var other = raw with
            {
                TemplateId = 500001,
                ScriptPath = "stackable/other/place.stk",
                Stackable = declaration with { TemplateId = 500001, TypeDeclaration = new("[waste]", [subtype]) }
            };
            That(RecoveryConsumableCatalog.Read(other) is { ItemId: 500001, MinimumLevel: 16, CoolTime: 10000 },
                "普通waste按大类识别，不按目录、ID、分组名称或子型固定值限制");
        }
        foreach (string category in new[] { "[etc]", "[contract]", "[booster]", "[creature]", "[material]" })
            That(RecoveryConsumableCatalog.Read(raw with { Stackable = declaration with { TypeDeclaration = new(category, [4]) } }) is null,
                "其他业务大类不能通过普通消耗应答伪造已实现");
        foreach (string category in new[] { "[throw]", "[set]" })
        {
            That(RecoveryConsumableCatalog.Read(raw with { Stackable = declaration with { TypeDeclaration = new(category, [3]) } }) is
                { ItemId: 1108, RequiresDungeon: true, CoolTime: 10000 },
                "投掷及放置按资源大类接入原单件消费，不按特效数据或目录建立白名单");
            That(RecoveryConsumableCatalog.Read(raw with { Stackable = declaration with { TypeDeclaration = new(category, [3]),
                HasUseActionPacket = true, DeclaredUseActionPacket = 1 } }) is null,
                "投掷及放置类中另走专用动作请求的道具不能由普通44冒充成功");
        }
        That(RecoveryConsumableCatalog.Read(raw with { SubType = 2, DeclaredFields = raw.DeclaredFields.Add("sub type") }) is
            { EffectsAfterAcknowledgement: true } && RecoveryConsumableCatalog.Read(raw with { SubType = 0 }) is
            { EffectsAfterAcknowledgement: false }, "sub type2的应答后效果不能套发送端即时效果的原生免扣");
        That(RecoveryConsumableCatalog.Read(raw with { DeclaredFields = raw.DeclaredFields.Add("sub type") }) is null,
            "畸形子类型不能当作缺省0而错误选择免扣应答");
        That(RecoveryConsumableCatalog.Read(raw with { CoolTime = -1 }) is null &&
            RecoveryConsumableCatalog.Read(raw with { CoolTime = null }) is null &&
            RecoveryConsumableCatalog.Read(raw with { CoolGroup = null }) is null &&
            RecoveryConsumableCatalog.Read(raw with { DeclaredFields = raw.DeclaredFields.Add("action type") }) is null &&
            RecoveryConsumableCatalog.Read(raw with { Stackable = declaration with { HasUseActionPacket = true, DeclaredUseActionPacket = 1 } }) is null,
            "错误扣费元数据、冷却声明和使用其他请求的服务端动作不套普通客户端效果路径");
        var limited = RecoveryConsumableCatalog.Read(raw with
        {
            Stackable = declaration with
            { HasMaximumLevel = true, DeclaredMaximumLevel = 20, DeclaredUsableJobs = ["[mage]"] }
        })!;
        That(limited.Allows(16, 3) && !limited.Allows(21, 3) && !limited.Allows(16, 0),
            "物品等级与职业仍是服务端使用资格，不与客户端恢复效果混为一谈");
        var core = new ItemCore(1108, ItemKind.Consumable, 2) { SortLockFlag = 1, Marker16 = 44, ExpireTime = 2000 };
        Character character = new(1, 0, "Potion", 7, new(1, 0, 10, 10), null)
        {
            Level = 16,
            Inventory = new([new(11, request.Position, core)])
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
            "匹配PVF两种药只保留最低等级和冷却原值/组，不把恢复效果带入服务端使用定义");
        That(resources.RecoveryConsumables.TryGet(1107, out var lowHp) &&
            lowHp == new RecoveryConsumableDefinition(1107, 6, 10000, 0) &&
            resources.RecoveryConsumables.TryGet(1065, out var flower) &&
            flower == new RecoveryConsumableDefinition(1065, 1, 10000, 1),
            "现场1107入门HP药剂及1065魔力之花沿普通类别读取PVF资格");
        That(resources.RecoveryConsumables.TryGet(1113, out var beginnerMp) && beginnerMp == new RecoveryConsumableDefinition(1113, 6, 10000, 1),
            "现场1113入门MP药剂无需加入ID白名单即可获得普通消费资格");
        foreach (int id in new[] { 1000, 1016, 1047, 1051, 1065, 1107, 1108, 1113, 1114, 1073, 6001 })
            That(resources.RecoveryConsumables.TryGet(id, out var definition) && resources.Items.Get(id).Kind == ItemKind.Consumable &&
                resources.Items.Get(id).MinimumLevel == definition.MinimumLevel,
                "使用目录与现有物品目录实际类别/等级一致，不能替代库存实例校验");
        That(resources.RecoveryConsumables.TryGet(1073, out var set) && set.RequiresDungeon && set.MinimumLevel == 20 && set.CoolTime == 5000 &&
            resources.RecoveryConsumables.TryGet(6001, out var projectile) && projectile.RequiresDungeon && projectile.MinimumLevel == 12 && projectile.CoolTime == 3000,
            "藤蔓种子set及飞盘throw从当前PVF读取等级与冷却，不解析或伪造客户端物件效果");
        int assistantId = resources.DevilContracts.Assistants.Keys.Order().First();
        That(resources.RecoveryConsumables.TryGet(assistantId, out var assistant) && assistant is
            { SummonsApc: true, EffectsAfterAcknowledgement: true } && !beginnerMp.EffectsAfterAcknowledgement,
            "真实PVF任务助手sub type2等待成功应答，普通1113保留发送端即时效果路径");
        foreach (int id in new[] { 25, 3037, 2660408, int.MaxValue })
            That(!resources.RecoveryConsumables.TryGet(id, out _), "宠物、材料、契约和未知ID不作为普通客户端效果物品消费");
    }
}
