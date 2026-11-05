using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TayberrsCardRewardTests
{
    public static void RunCore()
    {
        string[] paidNames = ["[specific goldcard group]", "[reward group]", "[reward dungeon type list]",
            "[/reward dungeon type list]", "[reward item list]", "[/reward item list]", "[/reward group]", "[/specific goldcard group]"];
        PvfToken[] paid = [new(3,0), new(3,1), new(0,101), new(3,2), new(0,71), new(3,3), new(3,4),
            new(0,10158700), new(0,10000), new(0,1), new(3,5), new(3,6), new(3,7)];
        That(DungeonSpecificCardRewards.ParseTayberrsPaid(paid,key=>paidNames[key]) is [{ ItemId:10158700,Count:1 }],
            "金币组101按类型71和完整三列读取");
        var uses = ClearRewardSections.Walk(paid,key=>paidNames[key]);
        That(uses.Any(row=>row.Name=="specific goldcard group" && row.Use==ClearRewardSectionUse.PartlyRead) &&
            uses.Where(row=>row.Name is "reward group" or "reward item list" or "reward dungeon type list")
                .All(row=>row.Use==ClearRewardSectionUse.ReadAlready), "诊断看板如实标记已接入金币组");
        paid[9] = new(0,0);
        Reject<InvalidDataException>(()=>DungeonSpecificCardRewards.ParseTayberrsPaid(paid,key=>paidNames[key]),
            "金币组零数量不能变成空奖励成功");
        paid[9] = new(0,1); paid[8] = new(0,9999);
        Reject<InvalidDataException>(()=>DungeonSpecificCardRewards.ParseTayberrsPaid(paid,key=>paidNames[key]),
            "金币组缺失权重不能补入默认奖池");
        paid[8] = new(0,10000); paid[4] = new(0,45);
        Reject<InvalidDataException>(()=>DungeonSpecificCardRewards.ParseTayberrsPaid(paid,key=>paidNames[key]),
            "金币组类型不符不能借泰波尔斯材料发放");
    }

    public static void RunResources(PvfArchive archive, ItemResources items)
    {
        var prices = DfoServer.Testing.SelfTestCatalogs.CardPrices(archive);
        var dungeons = DfoServer.Testing.SelfTestCatalogs.Dungeons(archive);
        var rules = DfoServer.Testing.SelfTestCatalogs.DropRules(archive);
        var clearLimits = ClearRewardLimitCatalog.Load(archive);
        That(clearLimits.ItemLimits.SequenceEqual([new(10305620, 3, 6), new(10306736, 3, 6)]) &&
            clearLimits.DungeonLimits.Length == 2 &&
            clearLimits.FindDungeon(291100320) is { GroupId: 1, DailyLimit: 3, WeeklyLimit: 6 } group1 &&
            group1.Dungeons.SequenceEqual([291100320, 291100322, 5349]) &&
            clearLimits.FindDungeon(400000105) is { GroupId: 2, DailyLimit: 1, WeeklyLimit: 2 } group2 &&
            group2.Dungeons.SequenceEqual([400000105]),
            "clearrewardlimit.etc完整解析道具上限与泰波尔斯/魔界大战英雄两组通关奖励上限");
        var limitedTayberrs = new TayberrsCardRewardPolicy(prices, items.Boosters, items.Items, clearLimits);
        var sampleDungeon = dungeons.Get(291100320);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        That(limitedTayberrs.Prepare(sampleDungeon, 1000, _ => 0, now, new(2, 5)) is { Item: not null, TayberrsWish: not null } &&
            limitedTayberrs.Prepare(sampleDungeon, 1000, _ => 0, now, new(3, 3)) is { Gold: 1000, Item: null, TayberrsWish: null } &&
            limitedTayberrs.Prepare(sampleDungeon, 1000, _ => 0, now, new(1, 6)) is { Gold: 1000, Item: null, TayberrsWish: null },
            "泰波尔斯通关奖励达到每日3次或每周6次上限后仅保留金币，不再冻结材料");
        That(prices.TayberrsPaidRewards.Select(row => (row.ItemId,row.Weight,row.Count)).SequenceEqual(
            new[] { (10158700,7000,1), (10158700,2000,2), (10158700,500,3), (10305821,500,1) }),
            "真实类型71金币奖励组完整保留四行");
        That(!dungeons.Get(291100321).IsTayberrs && !dungeons.Get(9541).IsTayberrs, "任务版和升级版不误套三模式奖励");
        foreach (var (id, box) in new[] { (291100320,10305620), (291100322,10306736), (5349,10308862) })
        {
            var dungeon = dungeons.Get(id);
            That(dungeon.IsTayberrs, "仅真实三模式识别专用奖励：" + id);
            var booster = items.Boosters.Get(box);
            That(booster.PendingRule is null && booster.Pools.Length == 2, "资源礼盒可由现有开盒链解释，保留双材料池");
            int start = 0;
            foreach (var row in prices.TayberrsPaidRewards)
            {
                foreach (int edge in new[] { start,start+row.Weight-1 })
                {
                    var paidPolicy = new DungeonCardRewardPolicy(rules, items.DungeonDropItems, items.Items, items.GoldLimits,
                        items.EpicPieces, random:bound=>bound==10000?edge:0, prices:prices);
                    var options = paidPolicy.PrepareOptions(dungeon,2,10,new(),DateTimeOffset.UtcNow)!;
                    That(options.Paid is { Gold:0, Item:{} core } && core.ItemId==row.ItemId && core.Value==row.Count &&
                        options.Price==15580 && options.BlackDiamond is null,
                        "付费牌各权重边界保留时空石1/2/3及灵石1，不重复免费礼盒：" + id);
                }
                start += row.Weight;
            }
        }
    }
}
