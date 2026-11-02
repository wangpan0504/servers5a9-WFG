using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassMonsterCardBindTests
{
    public static void RunCore()
    {
        int beforeCount = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var binder = new InventoryItem(11, new(InventoryListType.Main, 65), new(2600479, ItemKind.Consumable, 1));
        var cards = new InventoryItem(17, new(InventoryListType.Main, 233), new(1001, ItemKind.Profession, 2) { Marker16 = 3719 });
        var other = new InventoryItem(23, new(InventoryListType.Main, 3), new(10400, ItemKind.Equipment, 0) { Marker16 = 111 });
        var initial = new InventorySnapshot([binder, cards, other], Gold: 1000);
        var paid = initial with { Items = [other] };
        var reward = new InventoryItem(0, cards.Position, new(1002, ItemKind.Profession, 1));
        var after = paid with { Items = [other, reward] };
        var request = new MonsterCardBindRequest(65, [233, 233]);
        var receipt = new MonsterCardBindReceipt(65, 0, 0, 1, true, request.CardSlots, [new(233, 1002, 1, 0)]);
        var fact = new MonsterCardBindFact(Guid.NewGuid(), at, TimeProvider.System, request, receipt, binder,
            0, [new(cards, 2, 0)], 1002, initial, paid, after);
        var plan = new InventoryPlan(initial, after, "monster_card_bind") { MonsterCardBind = fact };
        var actual = after with { Items = [other, reward with { Uid = 31 }] };
        That(fact.Matches(plan, actual), "原普通合成器1件与同槽2卡实际扣除，原writer为奖励赋新UID");
        That(ReferenceEquals(plan.WithAfter(after).MonsterCardBind, fact), "WithAfter保留原合成来源和已抽结果");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, actual), "空来源GUID拒绝");
        That(!(fact with { Materials = [new(cards, 1, 0)] }).Matches(plan, actual) &&
            !(fact with { Materials = [new(cards, 2, 1)] }).Matches(plan, actual), "逐源张数与原稀有度不符拒绝");
        That(!(fact with { Materials = [new(cards, 1, 0), new(cards, 1, 0)] }).Matches(plan, actual), "重复材料事实不能代替原请求分组");
        That(!(fact with { Receipt = receipt with { DeletedSlots = [233] } }).Matches(plan, actual) &&
            !(fact with { Receipt = receipt with { Rewards = [new(233, 1002, 2, 0)] } }).Matches(plan, actual), "原ACK删除槽和实际奖励完整数量必须保持");
        That(!fact.Matches(plan, after) && !fact.Matches(plan, actual with { Items = [other, reward with { Uid = cards.Uid }] }), "奖励UID须由writer赋值且不复用已消耗源UID");
        That(!fact.Matches(plan, actual with { Items = [other with { Core = other.Core with { Marker16 = 0 } }, actual.Items[1]] }), "无关物品完整core不能丢失");
        That(!fact.Matches(plan.WithAfter(after with { Gold = 999 }), actual), "更换After不能借原事实额外扣款");
        var sameCards = cards with { Core = cards.Core with { Value = 4 } };
        var sameBefore = initial with { Items = [binder, sameCards, other] };
        var remaining = cards with { Core = cards.Core with { Value = 2 } };
        var samePaid = sameBefore with { Items = [remaining, other] };
        var merged = remaining with { Core = remaining.Core with { Value = 3 } };
        var sameAfter = samePaid with { Items = [merged, other] };
        var sameReceipt = receipt with { OutputRarity = 0, RarityUpgraded = false, Rewards = [new(233, 1001, 3, 0)] };
        var sameFact = fact with { Materials = [new(sameCards, 2, 0)], RewardItemId = 1001, Before = sameBefore, Paid = samePaid, After = sameAfter, Receipt = sameReceipt };
        That(sameFact.Matches(new(sameBefore, sameAfter, "monster_card_bind"), sameAfter), "原同ID奖励叠回净少1卡仍为一次成功合成");
        That(!(sameFact with { Paid = sameBefore }).Matches(new(sameBefore, sameAfter, "monster_card_bind"), sameAfter), "净变少1不能掩盖未扣合成器或少扣材料");
        var sphereBinder = binder with { Core = binder.Core with { ItemId = 10307441 } };
        var sphereCards = cards with { Core = cards.Core with { Value = 5 } };
        var sphereBefore = initial with { Items = [sphereBinder, sphereCards, other] };
        var spherePaid = sphereBefore with { Items = [other] };
        var sphereRequest = new MonsterCardBindRequest(65, [233, 233, 233, 233, 233]);
        var sphereReceipt = receipt with { BindType = 5, OutputRarity = 3, DeletedSlots = sphereRequest.CardSlots };
        var sphereAfter = spherePaid with { Items = [other, reward] };
        var sphereFact = fact with { Synthesizer = sphereBinder, BindType = 5, Request = sphereRequest, Receipt = sphereReceipt,
            Materials = [new(sphereCards, 5, 0)], Before = sphereBefore, Paid = spherePaid, After = sphereAfter };
        That(sphereFact.Matches(new(sphereBefore, sphereAfter, "monster_card_bind"), sphereAfter with { Items = [other, reward with { Uid = 47 }] }), "原Sphere五卡同族成功也只产生一次合成来源");
        That(!(sphereFact with { BindType = 0, Receipt = sphereReceipt with { BindType = 0 } }).Matches(new(sphereBefore, sphereAfter, "monster_card_bind"), actual), "普通两卡不得借Sphere五卡资格");
        foreach (var definition in new BattlePassMission[] { new(21, 1, 100, 21, "", -1), new(34, 2, 1000, 21, "", -1), new(60, 20, 1600, 21, "", -1) })
        {
            That(BattlePassMonsterCardBindSource.Matches(definition), "仅保存正常原目录三定义接线" + definition.Id);
            That(!BattlePassMonsterCardBindSource.Matches(definition with { RawParameter = 1 }) &&
                !BattlePassMonsterCardBindSource.Matches(definition with { Experience = definition.Experience + 1 }), "参数与XP变化不泛接未来定义" + definition.Id);
        }
        Console.WriteLine($"PASS BattlePassMonsterCardBindTests core ({Count - beforeCount} checks)");
    }
}
