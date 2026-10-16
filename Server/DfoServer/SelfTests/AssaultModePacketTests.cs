using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// #233：NOTI1536 五字节布局、ACK1547 布局、PVF 强袭资源与入场预检。
internal static class AssaultModePacketTests
{
    public static void Run()
    {
        That(AssaultModePackets.DailyInfo(1, 2, 3).SequenceEqual(new byte[] { 1, 2, 3, 0, 0 }), "强袭每日信息为5个u8且未确认尾字节为0");
        var sunday = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(8));
        That(AssaultModePackets.DayIndex(sunday, null) == 0 && AssaultModePackets.DayIndex(sunday.AddDays(1), null) == 1 &&
            AssaultModePackets.DayIndex(sunday.AddDays(6), null) == 6, "day索引按周日0..周六6映射");
        That(AssaultModePackets.DayIndex(sunday, "5") == 5 && AssaultModePackets.DayIndex(sunday, "7") == 0 &&
            AssaultModePackets.DayIndex(sunday, "x") == 0, "仅0..6的覆盖值生效");
        That(AssaultModePackets.SelectAccepted(7328).SequenceEqual(new byte[] { 1, 0xA0, 0x1C, 0, 0, 0, 0, 0, 0 }),
            "ACK1547成功为01+任务副本u32+未使用u32(0)");
        That(AssaultModePackets.SelectRejected().SequenceEqual(new byte[] { 0, 1, 0 }), "ACK1547失败为00+error u16");
    }

    public static void RunResources(PvfArchive pvf)
    {
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var catalog = AssaultModeCatalog.Load(pvf, dungeons);
        That(catalog.Missions.Count == 2 && catalog.Missions[7300].SequenceEqual([7328, 7330, 7332, 7334, 7336]) &&
            catalog.Missions[7327].SequenceEqual([7320, 7321, 7323, 7324, 7325, 7326]), "强袭 mission info list 按主副本归组");
        That(catalog.HiddenBaseDungeon == 7327 && catalog.DailyOpens.Length == 7 &&
            catalog.DailyOpens.All(row => row.Dungeons.SequenceEqual([7300])) &&
            catalog.DailyOpens.Select(row => row.HiddenDungeon).SequenceEqual([7326, 7320, 7321, 7323, 7324, 7325, 7326]),
            "强袭 daily open dungeon 七天均开放7300且隐藏副本按日轮换");
        foreach (int mission in catalog.Missions[7300])
            That(catalog.TryGetRequirement(mission, out var requirement, out string? error) &&
                requirement is { MinimumLevel: 95, ItemId: 10161610, ItemCount: 10, ItemFlag: 1 }, "强袭任务副本入场声明 " + mission + " " + error);
        That(!catalog.TryGetRequirement(7300, out _, out _), "主副本7300不是assaultmode dungeon，不能作为任务副本");

        var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
        var now = DateTimeOffset.UtcNow;
        Character Actor(byte level, int[] quests, int count) => new(1, 0, "强袭", 3, new(0, 0, 0, 0), null)
        {
            Level = level, CompletedQuests = quests,
            Inventory = count == 0 ? InventorySnapshot.Empty : InventorySnapshot.Empty with
                { Items = [new(11, new(InventoryListType.Main, 9), new(10161610, ItemKind.Quest, count))] }
        };
        AssaultModeDecision Decide(Character actor, uint parent = 7300, int day = 1) =>
            AssaultModeEntryPolicy.Select(catalog, actor, parent, day, pieces, now, _ => 2);
        var ok = Decide(Actor(95, [6119], 12));
        That(ok.Selection is { Parent: 7300, Mission: 7332 } && ok.Selection.Requirement.ItemCount == 10, "合格角色授权抽取的任务副本");
        That(Decide(Actor(94, [6119], 12)).Reason == AssaultModeRejection.LevelTooLow, "等级不足95拒绝");
        That(Decide(Actor(95, [], 12)).Reason == AssaultModeRejection.PrerequisiteQuest, "未完成6119拒绝");
        That(Decide(Actor(95, [6119], 9)).Reason == AssaultModeRejection.MissingItems &&
            Decide(Actor(95, [6119], 0)).Reason == AssaultModeRejection.MissingItems, "诞生之芽不足10拒绝");
        That(Decide(Actor(95, [6119], 12), 7327).Reason == AssaultModeRejection.HiddenLocked, "普通通关未满3次时隐藏副本锁定");
        AssaultModeDecision Counted(uint parent, int clears, int hidden, int day = 1) =>
            AssaultModeEntryPolicy.Select(catalog, Actor(95, [6119], 12), parent, day, pieces, now, _ => 0, new(clears, hidden), 77);
        That(Counted(7300, 3, 0).Reason == AssaultModeRejection.DailyLimitReached && Counted(7300, 2, 0).Accepted, "普通任务每日3次上限");
        var hiddenOk = Counted(7327, 3, 0);
        That(hiddenOk.Selection is { Parent: 7327, Mission: 7320, Hidden: true, DayKey: 77 } &&
            Counted(7327, 3, 0, 0).Selection?.Mission == 7326 && Counted(7327, 3, 0, 4).Selection?.Mission == 7324,
            "隐藏副本按当日 hidden 列选择任务");
        That(Counted(7327, 3, 1).Reason == AssaultModeRejection.HiddenLocked && Counted(7327, 2, 0).Reason == AssaultModeRejection.HiddenLocked,
            "隐藏副本每日1次且需普通3次");
        That(AssaultModeEntryPolicy.AfterClear(new(2, 0), false) == new AssaultDailyCounts(3, 0) &&
            AssaultModeEntryPolicy.AfterClear(new(3, 0), true) == new AssaultDailyCounts(3, 1), "通关计数累加");
        That(AssaultModePackets.Today(TimeProvider.System, new(2, 1)).Take(2).SequenceEqual(new byte[] { 2, 1 }), "NOTI1536 b0/b1 为当日计数");
        That(catalog.ClearRewards.Count == 11 &&
            catalog.Missions[7300].All(id => catalog.ClearRewards[id] is { ItemId: 10305786, Rate: 10000, Count: 15 }) &&
            catalog.Missions[7327].All(id => catalog.ClearRewards[id] is { ItemId: 10305786, Rate: 10000, Count: 45 }),
            "强袭通关奖励：任务×15、隐藏×45 黑夜穿行之证");
        That(catalog.Missions.Values.SelectMany(rows => rows).All(id => dungeons.Get(id).DisableBlackDiamondCardReward),
            "强袭任务副本禁用黑钻翻牌");
        That(Decide(Actor(95, [6119], 12), 7328).Reason == AssaultModeRejection.NotAssaultDungeon &&
            Decide(Actor(95, [6119], 12), 3).Reason == AssaultModeRejection.NotAssaultDungeon, "非主副本id拒绝");
        That(Decide(Actor(95, [6119], 12), day: 9).Reason == AssaultModeRejection.NotOpenToday, "无当日开放表拒绝");
        var spent = AssaultModeEntryPolicy.ConsumptionPlan(Actor(95, [6119], 12).Inventory, ok.Selection!.Requirement, pieces, now);
        That(spent.Accepted && spent.Plan!.After.Items.Single().Core.Count == 2 && spent.Plan.ChangedSlots.Length == 1,
            "入场扣除10个诞生之芽只改动一个背包格");
    }
}
