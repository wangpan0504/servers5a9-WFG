using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentGoldBonusTests
{
    static readonly int[] Items = [26115, 26341, 26626, 100331319, 26342, 26627, 100331240, 24191,
        100320775, 100341606, 102010249, 104000181, 101020286, 101020526, 109000133, 2747548];

    public static void RunCore()
    {
        int before = Count;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var character = new Character(1, 0, "GoldBonus", 0, new(1, 0, 10, 10), null)
        {
            Inventory = new([Worn(26626), Worn(100320775), Worn(100341606), Worn(104000181), Worn(2747548)], Gold: 10)
        };
        EquipmentGoldReward Capture(Character role, int basis = 101) =>
            EquipmentGoldBonusPolicy.Capture(role, basis, now.ToUnixTimeSeconds());
        That(Capture(character).Amount == 154 && character.Inventory.Gold == 10,
            "称号、戒指、辅助装备、武器及成宠按原金币分别截尾相加，不改变角色快照");
        That(Capture(character, 19).Amount == 26,
            "小额金币逐件截尾：不能先把53%合并后取整，也不递乘");
        foreach (int id in Items)
        {
            var definition = EquipmentGoldBonusDefinition.Get(id)!.Value;
            var role = character with { Inventory = new([Worn(id)]) };
            That(Capture(role, 100).Amount == 100 + definition.Percent, "已核对金币装备编号生效：" + id);
            foreach (var slot in new[] { new InventorySlot(InventoryListType.Main, 9), new(InventoryListType.PersonalCargo, 0),
                new(InventoryListType.Equipment, 11), new(InventoryListType.Equipment, 33) })
                That(Capture(role with { Inventory = new([Worn(id) with { Position = slot }]) }).Amount == 101,
                    "背包、仓库和纯外观槽不提供金币加成");
        }
        var title = Worn(26627) with { Core = Worn(26627).Core with { ExpireTime = (int)now.ToUnixTimeSeconds() + 1 } };
        var limited = character with { Inventory = new([title]) };
        That(Capture(limited).ExpiresAt == title.ExpireTime &&
            EquipmentGoldBonusPolicy.Capture(limited, 101, title.ExpireTime).Amount == 101,
            "有限期来源记录最早截止，到期等号停止加成");
        var expiring = DungeonPickupPlanning.Gold(limited, new(null, 101),
            new GoldLimitCatalog("[gold limit from level]\n0 1000 1 1000\n[/gold limit from level]"), now);
        That(expiring.Plan!.CharacterPrecondition!.Matches(limited, title.ExpireTime - 1) &&
            !expiring.Plan.CharacterPrecondition.Matches(limited, title.ExpireTime),
            "拾取提交资格保留实际贡献装备的期限，不能用到期前计划继续获利");
        var hungry = Worn(2747548) with { Creature = new() { Satiety = 0 } };
        That(Capture(character with { Inventory = new([hungry]) }).Amount == 101 &&
            Capture(character with { Inventory = new([Worn(2747548) with { Core = new(2747547, ItemKind.Creature, 1) }]) }).Amount == 101,
            "饥饿成宠和宠物蛋不提供金币加成");
        That(EquipmentGoldBonusDefinition.Get(10015074) is null && EquipmentGoldBonusDefinition.Get(400990278) is null,
            "金币药剂及增幅金币折扣宠物不混入装备金币收益");
        var limits = new GoldLimitCatalog("[gold limit from level]\n0 1000 1 1000\n[/gold limit from level]");
        var content = new DungeonGroundContent(null, 101);
        var pickup = DungeonPickupPlanning.Gold(character, content, limits, now);
        That(pickup.Accepted && pickup.Plan!.After.Gold == 164 && content.GoldAmount == 101 &&
            pickup.Plan.ChangedSlots.IsEmpty && pickup.Plan.ChangedCurrencies.SequenceEqual(new[] { CurrencyKind.Gold }),
            "共用拾取规划只增加实际金币，不改地面金额、实体或随机结果");
        That(new CurrencyService(limits).Grant(character, CurrencyKind.Gold, 101).Plan!.After.Gold == 111,
            "非地面拾取的普通金币发放不获得穿戴加成");
        var full = character with { Inventory = character.Inventory with { Gold = 850 } };
        That(!DungeonPickupPlanning.Gold(full, content, limits, now).Accepted && full.Inventory.Gold == 850,
            "原金币放得下但含加成的完整收益放不下时整件保留，不吞掉或截断奖励");
        That(!DungeonPickupPlanning.Gold(character, new(null, int.MaxValue), limits, now).Accepted,
            "大额金币使用宽中间值，不溢出为负奖励");
        That(Capture(character with { Id = 2, Inventory = new([]) }).Amount == 101,
            "另一角色不继承金币加成");
        Console.WriteLine($"PASS {Count - before} equipment gold bonus assertions");
    }

    static InventoryItem Worn(int id)
    {
        var definition = EquipmentGoldBonusDefinition.Get(id)!.Value;
        return new(id, new(InventoryListType.Equipment, definition.Slot), new(id, definition.Kind, 1))
            { Creature = definition.Kind == ItemKind.Creature ? new() : null };
    }

    public static void RunResources(ItemCatalog items)
    {
        foreach (int id in Items)
        {
            var actual = items.Get(id);
            var definition = EquipmentGoldBonusDefinition.Get(id)!.Value;
            That(actual.Kind == definition.Kind && actual.EquipmentSlot == definition.Slot && !actual.CreatureEgg && !actual.UsableExpired,
                "固定编号的金币效果只绑定当前PVF核对的真实类型、属性槽及期限规则：" + id);
        }
    }
}
