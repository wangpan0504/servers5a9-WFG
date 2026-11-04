using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SharedEffectItemTests
{
    public static void RunResources(PvfArchive pvf, TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var clock = new DailyBoosterTestClock(now);
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, new FirstSharedReward(), contracts: resources.Contracts, clock: clock);
        Character? cooledSource = null;
        foreach (var sample in new[]
        {
            (Id: 10002497, Path: "event/chn_event_20131107_solo_paradise_couple_hell/chn_stk_rose_without_love_10002497.stk",
                Booster: 10002500, Animation: "chn_rose_without_love", Rewards: new[] { 10002498 }),
            (Id: 10004383, Path: "event/chn_event_20141111_solo_day/chn_stk_rose_love_10004383.stk",
                Booster: 10004230, Animation: "chn_rose_with_love", Rewards: new[] { 10004386, 10004385 }),
            (Id: 490006626, Path: "490006001/chn_490006626.stk",
                Booster: 10004230, Animation: "chn_rose_with_love", Rewards: new[] { 10004386, 10004385 })
        })
        {
            string path = "stackable/" + sample.Path;
            string animation = "Common/CommonEffect/Animation/" + sample.Animation;
            var raw = SharedEffectConsumableFile.Parse(sample.Id, path, pvf.ReadTokens(path), pvf.ResolveString);
            var expected = new SharedEffectOutcome(0, 100000, sample.Booster, animation + "_effect.ani", animation + "_back.ani")
                { AdditionalAnimation = animation + "_front.ani" };
            That(raw.Errors.Count == 0 && raw.Outcomes.SequenceEqual([expected]),
                "三动画真实源完整保留三条精确路径和子礼包引用 " + sample.Id);
            var definition = resources.Items.SharedEffect(sample.Id);
            That(definition is { CoolTime: 1000, CooltimeMaintenance: 0, Outcomes.Length: 1 } &&
                definition.CoolGroup == -sample.Id && definition.Outcomes[0] == new SharedEffectReward(0, 100000, sample.Booster),
                "真实共享特效目录保留冷却、结果序号与原礼包合同 " + sample.Id);
            var template = resources.Items.Get(sample.Id);
            var source = new InventoryItem(1, Bag(65), InventoryRewardGrantService.Create(template, 2, now));
            var actor = new Character(1, 0, "SharedEffect", 0, towns.Room, towns.InitialReturn)
                { Job = 0, Level = 95, Inventory = new([source]) };
            actor.Inventory.Validate();
            That(InventoryAvatarPackageService.SourceEligible(actor, source, template) && !source.IsExpired(now.ToUnixTimeSeconds()),
                "真实源按模板创建且职业、等级、技能和期限合法 " + sample.Id);
            string before = JsonSerializer.Serialize(actor.Inventory);
            var result = service.UseSharedEffect(actor, sample.Id, 65, out byte outcome, now);
            That(result.Accepted && result.Plan is not null && outcome == 0,
                "三动画源沿共享特效入口合法发奖 " + sample.Id + "/" + result.Error + "/" + result.RejectReason);
            var plan = result.Plan!;
            That(ReferenceEquals(plan.Before, actor.Inventory) && JsonSerializer.Serialize(actor.Inventory) == before &&
                plan.After.Find(Bag(65)) == (source with { Core = source.Core with { Value = 1 } }) && plan.OverflowItems.IsEmpty,
                "成功只扣一件源并保留原快照，不把中间礼包放入背包 " + sample.Id);
            var granted = plan.After.Items.Where(item => item.Core.ItemId != sample.Id).OrderBy(item => item.Core.ItemId).ToArray();
            That(granted.Select(item => item.Core.ItemId).SequenceEqual(sample.Rewards.Order()) &&
                granted.All(item => item.Core == InventoryRewardGrantService.Create(resources.Items.Get(item.Core.ItemId), 1, now) &&
                    item.Position.List == InventoryListType.Main && ItemSlotBoundService.Allows(plan.After, item.Position, item.Core.Kind)),
                "固定首骰发放真实子池的全部奖励及正确数量、模板属性和容器 " + sample.Id);
            That(plan.After.ConsumableStates is [{ Kind: ConsumableStateKind.Cooldown } cooldown] &&
                cooldown.ItemId == sample.Id && cooldown.CoolGroup == -sample.Id && cooldown.Maintenance == 0 &&
                cooldown.UntilMilliseconds == now.ToUnixTimeMilliseconds() + 1000,
                "源消费、奖励和一秒冷却进入同一计划 " + sample.Id);
            cooledSource ??= actor with { Inventory = plan.After };
        }
        var cooled = cooledSource!;
        string cooledBefore = JsonSerializer.Serialize(cooled.Inventory);
        var denied = service.UseSharedEffect(cooled, 10002497, 65, out _, now.AddMilliseconds(999));
        That(!denied.Accepted && denied.Plan is null && JsonSerializer.Serialize(cooled.Inventory) == cooledBefore,
            "同源冷却拒绝路径不再扣源、不追加奖励且不改原冷却快照");
        Console.WriteLine($"PASS {Count - checks} 三动画共享特效真实源、奖励与原子计划断言");

        static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    }

    sealed class FirstSharedReward : Random
    {
        public override long NextInt64(long maxValue) => 0;
    }
}
