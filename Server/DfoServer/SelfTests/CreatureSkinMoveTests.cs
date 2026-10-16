using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureSkinMoveTests
{
    static readonly InventorySlot SkinSlot = new(InventoryListType.Equipment, 33);
    static readonly InventorySlot RealSlot = new(InventoryListType.Equipment, 26);

    public static void RunCore()
    {
        var template = new ItemTemplate(7001, ItemKind.Creature, 26, 255, 0, 1, 0, 0, 0, "[trade]", 0, 999, "幻化成宠")
            { CreatureSubType = 0 };
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [7001] = template, [7002] = template with { Id = 7002 } });
        var detail = new CreatureDetail { Name = "保留名字", Experience = 50, Level = 7, Satiety = 19,
            ExpireTime = 2000, Mode = 1, Mode1Field0 = 7, Mode1Field1 = 8, TailFlag = 1 };
        var skin = new InventoryItem(101, new(InventoryListType.Pet, 0), new(7001, ItemKind.Creature, 101)
            { SortLockFlag = 1, EquipmentLockId = 9, Marker16 = 77, Extension27 = 123 }) { Creature = detail };
        var real = new InventoryItem(102, RealSlot, new(7002, ItemKind.Creature, 102)) { Creature = new() { Name = "真宠" } };
        Character character = new(1, 0, "SkinMove", 0, new(1, 0, 0, 0), null)
            { Level = 0, EquipmentSlotFlags = 32, Inventory = new([skin, real]) };

        That(ItemSlotBoundService.Allows(character.Inventory, SkinSlot, ItemKind.Creature) &&
            !ItemSlotBoundService.Allows(character.Inventory, SkinSlot, ItemKind.CreatureEquipment) &&
            !ItemSlotBoundService.Allows(character.Inventory, new(InventoryListType.Equipment, 34), ItemKind.Creature),
            "幻化槽33仅接受宠物，不能放宠物装备或越界扩成其它装备槽");
        foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
        {
            var owner = character with { Job = job };
            var result = InventoryMoveService.Plan(owner, Request(skin, SkinSlot), catalog, 1000);
            That(result.Accepted && result.Plan!.After.Find(SkinSlot) == skin with { Position = SkinSlot } &&
                result.Plan.After.Find(RealSlot) == real && result.Plan.After.Find(skin.Position) is null &&
                result.Plan.After.Items.Select(item => item.Uid).Distinct().Count() == 2 && owner.Inventory.Find(skin.Position) == skin,
                "所有职业均移动原幻化实例且保留完整详情/锁，不套角色穿戴等级/职业/技能限制，不改真实26或复制UID");
            That(result.Plan!.CharacterPrecondition!.Matches(owner, 1999) &&
                !result.Plan.CharacterPrecondition.Matches(owner, 2000) &&
                !result.Plan.CharacterPrecondition.Matches(owner with { EquipmentSlotFlags = 0 }, 1000) &&
                !result.Plan.CharacterPrecondition.Matches(owner with { Revision = 1 }, 1000),
                "幻化移动冻结角色开通位、revision及真实详情期限，过期前规划不能越期提交");
        }
        That(!InventoryMoveService.Plan(character with { EquipmentSlotFlags = 0 }, Request(skin, SkinSlot), catalog, 1000).Accepted,
            "没有角色开通位不能把成宠装进幻化栏");
        foreach (var invalid in new[] { template with { Kind = ItemKind.Equipment }, template with { EquipmentSlot = 33 },
            template with { CreatureSubType = 1 }, template with { CreatureSubType = -1 } })
        {
            catalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [7001] = invalid });
            That(!InventoryMoveService.Plan(character, Request(skin, SkinSlot), catalog, 1000).Accepted,
                "原模板必须为部位26且明确subtype0的成宠，伪造Core分类、模板部位或蛋均不能绕过");
        }
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [7001] = template });
        foreach (var invalid in new[] { skin with { Creature = null }, skin with { Uid = 0 },
            skin with { Core = skin.Core with { Value = 102 } }, skin with { Core = skin.Core with { SealFlag = 1 } },
            skin with { Core = skin.Core with { Kind = ItemKind.CreatureEquipment } },
            skin with { Creature = detail with { ExpireTime = 1000 } },
            skin with { Creature = detail with { Experience = -1 } }, skin with { Avatar = new() } })
            That(!InventoryMoveService.Plan(character with { Inventory = new([invalid, real]) }, Request(invalid, SkinSlot), catalog, 1000).Accepted,
                "幻化穿入拒绝缺详情、临时或不一致UID、非成宠type、错误分类、到期及损坏详情");

        var worn = skin with { Position = SkinSlot };
        var equipped = character with { Inventory = new([worn, real]) };
        var removed = InventoryMoveService.Plan(equipped, Request(worn, skin.Position), catalog, 1000);
        That(removed.Accepted && removed.Plan!.After.Find(SkinSlot) is null && removed.Plan.After.Find(skin.Position) == skin &&
            removed.Plan.After.Find(RealSlot) == real, "幻化卸下恢复同一完整实例，不动真实宠物与详情");
        var reversed = InventoryMoveService.Plan(equipped,
            new(skin.Position, 0, 1, SkinSlot, skin.Core.ItemId, 0, -1, 0, 0), catalog, 1000);
        That(reversed.Accepted && reversed.Plan!.After.Items.SequenceEqual(removed.Plan!.After.Items),
            "客户端反向空源卸装请求沿同一规则处理");

        var other = real with { Position = new(InventoryListType.Pet, 139) };
        var swapRole = character with { Inventory = new([worn, other]) };
        var swap = InventoryMoveService.Plan(swapRole, Request(worn, other.Position, other.Core.ItemId), catalog, 1000);
        That(swap.Accepted && swap.Plan!.After.Find(SkinSlot) == other with { Position = SkinSlot } &&
            swap.Plan.After.Find(other.Position) == worn with { Position = other.Position } &&
            !swap.Plan.CharacterPrecondition!.Matches(swapRole, 2000),
            "从幻化拖向非空宠物格的交换也校验反向进入33的成宠，双方UID/详情及到期资格完整保留");
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [7002] = template with { Id = 7002, CreatureSubType = 1 } });
        That(!InventoryMoveService.Plan(swapRole, Request(worn, other.Position, other.Core.ItemId), catalog, 1000).Accepted,
            "反向交换不能把目标格里的宠物蛋绕过资格送入33");
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [7002] = template with { Id = 7002 } });
        var actualSwap = InventoryMoveService.Plan(equipped, Request(worn, RealSlot, real.Core.ItemId), catalog, 1000);
        That(actualSwap.Accepted && actualSwap.Plan!.After.Find(RealSlot)?.Uid == skin.Uid &&
            actualSwap.Plan.After.Find(SkinSlot)?.Uid == real.Uid, "真实26与幻化33互换时交换两个资产位置，不能复制或覆写详情");
        That(!InventoryMoveService.Plan(equipped, Request(skin, SkinSlot), catalog, 1000).Accepted,
            "旧源槽重复穿入请求不能变成反向卸装，模板匹配仍按原请求顺序校验");
    }

    public static void RunResources(ItemCatalog items)
    {
        var role = new Character(1, 0, "SkinResource", 0, new(1, 0, 0, 0), null) { EquipmentSlotFlags = 32 };
        That(items.Get(63023) is { Kind: ItemKind.Creature, EquipmentSlot: 26, CreatureSubType: 0 } &&
            InventoryMoveService.Allows(role, SkinSlot, new(63023, ItemKind.Creature, 1), items, 1000) &&
            !InventoryMoveService.Allows(role, SkinSlot, new(63022, ItemKind.Creature, 1), items, 1000),
            "匹配资源成宠63023能进入幻化栏，宠物蛋63022即使伪造普通Core也不能通过");
    }

    static InventoryMoveRequest Request(InventoryItem source, InventorySlot destination, int destinationId = 0) =>
        new(source.Position, source.Core.ItemId, 1, destination, destinationId, 0, -1, 0, 0);
}
