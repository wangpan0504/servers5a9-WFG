using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassAvatarCompoundTests
{
    public static void RunCore()
    {
        int checks = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var material = new InventoryItem(7, new(InventoryListType.Main, 65), new(2600400, ItemKind.Consumable, 2));
        var source0 = new InventoryItem(11, new(InventoryListType.Avatar, 10), new(1001, ItemKind.Avatar, 11)) { Avatar = new() };
        var source1 = new InventoryItem(13, new(InventoryListType.Avatar, 11), new(1001, ItemKind.Avatar, 13)) { Avatar = new() };
        var before = new InventorySnapshot([material, source0, source1], Gold: 1000);
        var remaining = material with { Core = material.Core with { Value = 1 } };
        var paid = before with { Items = [remaining] };
        var reward = new InventoryItem(0, new(InventoryListType.Avatar, 10), new(1002, ItemKind.Avatar, 0) { Durability = 2 }) { Avatar = new() };
        var after = paid with { Items = [remaining, reward] };
        var request = new AvatarCompoundRequest(65, 10, 1001, 11, 1001, 1002, 2, 0, 1);
        var receipt = new AvatarCompoundReceipt(2600400, 1, "hair", 1000, 9999, false, [new(reward.Position, 1002)]);
        var fact = new AvatarCompoundFact(Guid.NewGuid(), at, TimeProvider.System, request, receipt, source0, source1, material, before, paid, after);
        var plan = new InventoryPlan(before, after, "avatar_compound") { AvatarCompound = fact };
        var writtenReward = reward with { Uid = 19, Core = reward.Core with { Value = 19 } };
        var actual = after with { Items = [remaining, writtenReward] };
        That(fact.Matches(plan, actual), "原两装扮和材料1消费，writer为新详情补共享UID与Core.Value");
        That(ReferenceEquals(plan.WithAfter(after).AvatarCompound, fact), "WithAfter保留装扮合成事实与已抽结果");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, actual), "空来源拒绝");
        That(!fact.Matches(plan, after) && !fact.Matches(plan, actual with { Items = [remaining, writtenReward with { Core = reward.Core }] }), "新UID和详情共享Value缺一拒绝");
        That(!fact.Matches(plan, actual with { Items = [remaining, writtenReward with { Uid = source0.Uid, Core = reward.Core with { Value = checked((int)source0.Uid) } }] }), "新产物不复用已消费源UID");
        That(!(fact with { Source1 = source0 }).Matches(plan, actual), "同一装扮不能冒两独立源");
        That(!(fact with { Material = material with { Core = material.Core with { Value = 1 } } }).Matches(plan, actual), "材料原完整数量不符拒绝");
        That(!(fact with { Paid = before }).Matches(plan, actual), "未实际扣两源和材料拒绝");
        That(!fact.Matches(plan.WithAfter(after with { Gold = 999 }), actual), "新After不借旧事实额外扣款");
        That(!fact.Matches(plan, actual with { Items = [remaining with { Core = remaining.Core with { Marker16 = 1 } }, writtenReward] }), "保留材料完整core不能变化");
        That(!fact.Matches(plan, actual with { Items = [remaining, writtenReward with { Core = writtenReward.Core with { Durability = 3 } }] }), "产物所选能力须保持");
        That(!fact.Matches(plan, actual with { Items = [remaining, writtenReward with { Avatar = null }] }), "产物详情不能丢失");
        That(!(fact with { Receipt = receipt with { MaterialItemId = 1 } }).Matches(plan, actual) && !(fact with { Receipt = receipt with { Outputs = [new(reward.Position, 1)] } }).Matches(plan, actual), "原材料与保证目标receipt须一致");
        var rare = reward with { Position = new(InventoryListType.Avatar, 11), Core = reward.Core with { ItemId = 1003 } };
        var rareAfter = paid with { Items = [remaining, reward, rare] };
        var rareReceipt = receipt with { HitRare = true, RareRoll = 0, Outputs = [new(reward.Position, 1002), new(rare.Position, 1003)] };
        var rareFact = fact with { Receipt = rareReceipt, After = rareAfter };
        var rareActual = rareAfter with { Items = [remaining, writtenReward, rare with { Uid = 23, Core = rare.Core with { Value = 23 } }] };
        That(rareFact.Matches(new(before, rareAfter, "avatar_compound"), rareActual), "原稀有命中两产物仍仅一次合成事实");
        That(!(rareFact with { Receipt = rareReceipt with { RareRoll = 9999 } }).Matches(new(before, rareAfter, "avatar_compound"), rareActual), "稀有命中须与原roll/rate一致");
        var definition = new BattlePassMission(68, 6, 1600, 41, "", -1);
        That(BattlePassAvatarCompoundSource.Matches(definition), "仅原68定义接线");
        That(!BattlePassAvatarCompoundSource.Matches(definition with { RawTypeCode = 42 }) && !BattlePassAvatarCompoundSource.Matches(definition with { RawParameter = 0 }) && !BattlePassAvatarCompoundSource.Matches(definition with { RequiredCount = 5 }) && !BattlePassAvatarCompoundSource.Matches(definition with { Experience = 1 }), "其他raw/参数/数量/XP定义不得泛接");
        Console.WriteLine($"PASS BattlePassAvatarCompoundTests core ({Count - checks} checks)");
    }
}
