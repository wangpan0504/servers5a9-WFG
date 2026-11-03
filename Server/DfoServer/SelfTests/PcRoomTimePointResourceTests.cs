using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.OnlineEvents;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PcRoomTimePointResourceTests
{
    internal static void RunCore()
    {
        var source = new PcRoomTimePointFile(false, true, 1, 4,
            [new(1, 11, 2, 1200, false, true), new(1, 12, 1, 800, true, false)],
            [new(2, 22, 1, 3, true), new(1, 21, 2, 2, false)],
            "UI/Event/Example.xui", []);
        var catalog = new PcRoomTimePointCatalog(source);
        That(catalog.Daily[0].RequiredMilliseconds == 1200 && catalog.Daily[0].RequiredSeconds == 2 &&
            catalog.Daily[1].RequiredMilliseconds == 2000 && catalog.Daily[1].RequiredSeconds == 2,
            "原增量毫秒累计后只在秒通知边界向上取整，不逐段取整或提前发奖");
        That(catalog.Period[0] is { StageIndex: 1, RequiredDays: 2 } && catalog.Period[1].RequiredDays == 5 &&
            !catalog.PcRoomOnly && !catalog.Daily[0].FirstFlag && !catalog.Period[0].Flag,
            "累计奖励按声明阶段映射并保留标志；历史技术名不增加网吧资格");
        Reject<InvalidDataException>(() => new PcRoomTimePointCatalog(source with
        {
            DailyRewards = [.. source.DailyRewards, source.DailyRewards[0], source.DailyRewards[0], source.DailyRewards[0]]
        }));
        Reject<InvalidDataException>(() => new PcRoomTimePointCatalog(source with
        {
            PeriodRewards = [source.PeriodRewards[0], source.PeriodRewards[0]]
        }));
        Reject<InvalidDataException>(() => new PcRoomTimePointCatalog(source with
        {
            DailyRewards = [source.DailyRewards[0] with { DurationMilliseconds = 0 }]
        }));
    }

    internal static void Run(PvfArchive pvf, ItemCatalog items)
    {
        var catalog = PcRoomTimePointCatalog.Load(pvf, items);
        That(catalog.Daily.Select(row => row.ItemId).SequenceEqual(new[] { 490014060, 490014061, 490014063, 490014064 }) &&
            catalog.Daily.Select(row => row.Count).SequenceEqual(new[] { 1, 1, 2, 1 }) &&
            catalog.Daily.Select(row => row.RequiredSeconds).SequenceEqual(new long[] { 1800, 3600, 5400, 7200 }),
            "当前客户端四档在线奖励保留真实物品、数量与30/60/90/120分钟阈值");
        That(catalog.Period.Select(row => row.ItemId).SequenceEqual(new[] { 490014067, 490014068, 490014069, 490014173 }) &&
            catalog.Period.Select(row => row.Count).SequenceEqual(new[] { 3, 1, 1, 1 }) &&
            catalog.Period.Select(row => row.RequiredDays).SequenceEqual(new[] { 5, 10, 15, 20 }),
            "当前客户端累计天数奖励保留真实物品及5/10/15/20天阈值");
        That(!catalog.DailyRewardAutoGet && catalog.ExitAlarm && catalog.DailyRewardLoop == 1 &&
            catalog.PeriodRewardLoop == 4 && !catalog.PcRoomOnly &&
            catalog.UiFile == "ui/event/pcroomtimepoint/pcroomtimepointrewarditeminfowindow_chn.xui",
            "真实活动228资源与界面匹配，不混用TimeAttendance或HoneyTime活动");
    }
}