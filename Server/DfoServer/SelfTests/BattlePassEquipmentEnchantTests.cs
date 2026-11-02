using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassEquipmentEnchantTests
{
    public static void RunCore()
    {
        int checks = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var source = new InventoryItem(11, new(InventoryListType.Main, 82), new(1001, ItemKind.Consumable, 2) { Marker16 = 319 });
        var spare = source with { Uid = 13, Position = new(InventoryListType.Main, 83) };
        var target = new InventoryItem(17, new(InventoryListType.Main, 9), new(2001, ItemKind.Equipment, 0)
            { EnchantCardId = 71, EnchantUpgrade = 2, GenuineUpgrade = 4, Rune = 8, Marker16 = 591 });
        var before = new InventorySnapshot([source, target, spare], Gold: 1000);
        var paid = before with { Items = [source with { Core = source.Core with { Value = 1 } }, target, spare] };
        var changed = target with { Core = target.Core with { EnchantCardId = 1002, EnchantUpgrade = 0 } };
        var after = paid with { Items = [paid.Items[0], changed, spare] };
        var fact = EquipmentEnchantFact.Capture(before, paid, after, source, target, EquipmentEnchantMode.BeadCard,
            "enchant_by_bead", at, TimeProvider.System);
        var plan = new InventoryPlan(before, after, "enchant_by_bead") { EquipmentEnchant = fact };
        That(fact.Matches(plan, after), "原宝珠扣一与同UID目标完整附魔核心匹配");
        That(ReferenceEquals(plan.WithAfter(after).EquipmentEnchant, fact), "WithAfter保原附魔事实和已抽结果");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, after), "空附魔来源拒绝");
        That(!(fact with { Source = spare }).Matches(plan, after), "不能以同ID备用UID替换源");
        That(!(fact with { Paid = before }).Matches(plan, after), "未实扣宝珠不能计附魔");
        That(!(fact with { Items = [new(source, 2)] }).Matches(plan, after), "冻结原扣一数量不可换成扣二");
        That(!fact.Matches(plan, after with { Items = [after.Items[0], changed with { Uid = 29 }, spare] }), "附魔目标必须保原持久UID");
        That(!fact.Matches(plan, after with { Items = [after.Items[0], changed with { Core = changed.Core with { Marker16 = 0 } }, spare] }), "完整目标非附魔core不得丢失");
        That(!(fact with { TargetAfter = changed.Core with { Rune = 0 } }).Matches(plan, after), "不能借附魔修改其他属性");
        That(!fact.Matches(plan, after with { Gold = 999 }), "宝珠不可额外扣未冻结金币");
        That(!fact.Matches(plan.WithAfter(after with { Gold = 999 }), after), "旧事实不得用于替换After");
        var titleChanged = target with { Core = target.Core with { Extension97 = 0x00020003 } };
        var titleAfter = paid with { Items = [paid.Items[0], titleChanged, spare] };
        var titleFact = EquipmentEnchantFact.Capture(before, paid, titleAfter, source, target,
            EquipmentEnchantMode.BeadTitle, "enchant_by_bead", at, TimeProvider.System);
        var titlePlan = new InventoryPlan(before, titleAfter, "enchant_by_bead") { EquipmentEnchant = titleFact };
        That(titleFact.Matches(titlePlan, titleAfter), "随机称号技能仅保原抽定Extension97完整值");
        That(!(titleFact with { TargetAfter = titleChanged.Core with { EnchantCardId = 0 } }).Matches(titlePlan, titleAfter), "称号随机技能不得清普通附魔");
        var expertBefore = CurrencyWallet.Set(before, CurrencyKind.WhiteCube, 100);
        var expertPaid = CurrencyWallet.Set(expertBefore with { Items = paid.Items, Gold = 970 }, CurrencyKind.WhiteCube, 90);
        var expertTarget = target with { Core = target.Core with { EnchantCardId = source.Core.ItemId, EnchantUpgrade = 1 } };
        var expertAfter = expertPaid with { Items = [expertPaid.Items[0], expertTarget, spare] };
        var expertFact = EquipmentEnchantFact.Capture(expertBefore, expertPaid, expertAfter, source, target,
            EquipmentEnchantMode.ExpertCard, "expert_job_card_enchant", at, TimeProvider.System);
        var expertPlan = new InventoryPlan(expertBefore, expertAfter, "expert_job_card_enchant") { EquipmentEnchant = expertFact };
        var expertActual = expertAfter with { AccountWallet = expertAfter.AccountWallet with { Revision = 1 } };
        That(expertFact.Matches(expertPlan, expertActual), "专家原卡片/金币/晶块完整支付及原walletrevision保持");
        That(!expertFact.Matches(expertPlan, expertAfter), "原账号币扣费须writer推进钱包版本");
        That(!(expertFact with { Currencies = [] }).Matches(expertPlan, expertActual), "原材料钱包支出不能省略");
        That(!(expertFact with { TargetAfter = expertTarget.Core with { EnchantCardId = 1002 } }).Matches(expertPlan, expertActual), "专家卡片必须安装原消耗卡的实际效果");
        var tradeAfter = expertAfter with { Gold = expertAfter.Gold - 7 };
        var tradeFact = expertFact.ForTrade(expertPlan, expertBefore, tradeAfter, 7);
        var tradePlan = new InventoryPlan(expertBefore, tradeAfter, "expert_job_trade_customer") { EquipmentEnchant = tradeFact };
        var tradeActual = tradeAfter with { AccountWallet = tradeAfter.AccountWallet with { Revision = 1 } };
        That(tradeFact.Matches(tradePlan, tradeActual), "他机顾客仅绑定原交易服务费且原材料核心不变");
        That(tradeFact.Id == expertFact.Id && tradeFact.Target.Uid == target.Uid && tradeFact.Source.Uid == source.Uid,
            "跨账号交易保原顾客来源GUID及真实源目标UID");
        That(new InventoryPlan(before, before, "expert_job_trade_owner").EquipmentEnchant is null,
            "店主收费和熟练度不冒顾客附魔来源");
        That(!(tradeFact with { Reason = "expert_job_trade_owner" }).Matches(tradePlan, tradeActual), "店主reason不能提交附魔来源");
        var definition = new BattlePassMission(30, 2, 1000, 27, "", -1);
        That(BattlePassEquipmentEnchantSource.Matches(definition), "仅原weekly30定义接线");
        That(!BattlePassEquipmentEnchantSource.Matches(definition with { RawTypeCode = 26 }) &&
            !BattlePassEquipmentEnchantSource.Matches(definition with { RawParameter = 0 }) &&
            !BattlePassEquipmentEnchantSource.Matches(definition with { RequiredCount = 1 }) &&
            !BattlePassEquipmentEnchantSource.Matches(definition with { Experience = 1 }), "其他raw/参数/数量/XP不泛接");
        Console.WriteLine($"PASS BattlePassEquipmentEnchantTests core ({Count - checks} checks)");
    }
}
