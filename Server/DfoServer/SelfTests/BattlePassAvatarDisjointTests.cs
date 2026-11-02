using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassAvatarDisjointTests
{
    public static void RunCore()
    {
        int checks = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var source = new InventoryItem(11, new(InventoryListType.Avatar, 0), new(1001, ItemKind.Avatar, 11)) { Avatar = new() };
        var other = source with { Uid = 13, Position = new(InventoryListType.Avatar, 1), Core = source.Core with { Value = 13 } };
        var oldEmblem = new InventoryItem(17, new(InventoryListType.Main, 289), new(2001, ItemKind.Emblem, 5) { Marker16 = 319 });
        var before = new InventorySnapshot([oldEmblem, source, other], Gold: 1000);
        var paid = before with { Items = [oldEmblem, other] };
        var merged = oldEmblem with { Core = oldEmblem.Core with { Value = 7 } };
        var reward = new InventoryItem(0, new(InventoryListType.Main, 290), new(2002, ItemKind.Emblem, 1));
        var after = paid with { Items = [merged, reward, other] };
        var request = new AvatarDisjointRequest(0, 1001);
        var receipt = new AvatarDisjointReceipt(request, [new(289, 2001, 2), new(290, 2002, 1)]);
        var fact = new AvatarDisjointFact(Guid.NewGuid(), at, TimeProvider.System, request, receipt,
            receipt.Rewards.ToImmutableArray(), source, before, paid, after);
        var plan = new InventoryPlan(before, after, "avatar_disjoint") { AvatarDisjoint = fact };
        var actual = after with { Items = [merged, reward with { Uid = 23 }, other] };
        That(fact.Matches(plan, actual), "单件原装扮消费与原徽章合并/新UID奖励完整保持");
        That(ReferenceEquals(plan.WithAfter(after).AvatarDisjoint, fact), "WithAfter保原来源与已抽奖励");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, actual), "空来源拒绝");
        That(!(fact with { Source = other }).Matches(plan, actual), "不能消费同ID另一原UID实例");
        That(!(fact with { Paid = before }).Matches(plan, actual), "未扣实际source拒绝");
        That(!(fact with { Rewards = [new(289, 2001, 1), new(290, 2002, 1)] }).Matches(plan, actual), "冻结完整原奖励增量不符拒绝");
        That(!(fact with { Receipt = receipt with { Rewards = [new(289, 2001, 1), new(290, 2002, 1)] } }).Matches(plan, actual), "可变receipt不可替换已冻结奖励");
        That(!fact.Matches(plan, actual with { Items = [merged, reward with { Uid = source.Uid }, other] }), "新奖励不得复用被消费sourceUID");
        That(!fact.Matches(plan, after), "新奖励须由原writer赋持久UID");
        That(!fact.Matches(plan, actual with { Items = [merged with { Core = merged.Core with { Marker16 = 0 } }, actual.Items[1], other] }), "原徽章叠回须保全部core");
        That(!fact.Matches(plan, actual with { Items = [merged, actual.Items[1], other with { Avatar = null }] }), "未消费装扮详情须完整保持");
        That(!fact.Matches(plan, actual with { Gold = 999 }), "徽章奖励不允许额外钱包扣费");
        That(!fact.Matches(plan.WithAfter(after with { Gold = 999 }), actual), "不得借旧事实替换After扣款");
        var definition = new BattlePassMission(69, 6, 1600, 42, "", -1);
        That(BattlePassAvatarDisjointSource.Matches(definition), "仅原season69定义接线");
        That(!BattlePassAvatarDisjointSource.Matches(definition with { RawTypeCode = 41 }) && !BattlePassAvatarDisjointSource.Matches(definition with { RawParameter = 0 }) && !BattlePassAvatarDisjointSource.Matches(definition with { RequiredCount = 5 }) && !BattlePassAvatarDisjointSource.Matches(definition with { Experience = 1 }), "其他raw/参数/数量/XP定义不泛接");
        Console.WriteLine($"PASS BattlePassAvatarDisjointTests core ({Count - checks} checks)");
    }
}
