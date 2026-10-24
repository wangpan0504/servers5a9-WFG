using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Raids;

namespace DfoServer;

internal static class RaidEntryScheduleTests
{
    public static void Run()
    {
        int before = Count;
        DateTimeOffset At(DayOfWeek day, int hour, int minute)
        {
            var cursor = new DateTimeOffset(2026, 1, 1, hour, minute, 0, RaidEntrySchedule.Zone);
            while (cursor.DayOfWeek != day) cursor = cursor.AddDays(1);
            return cursor;
        }
        var luke = new[] { new RaidEntrySchedule.Window(2, 18 * 60, 22 * 60) };
        That(RaidEntrySchedule.IsOpen(luke, At(DayOfWeek.Tuesday, 18, 0)), "周二 18:00 放行卢克");
        That(!RaidEntrySchedule.IsOpen(luke, At(DayOfWeek.Tuesday, 22, 0)), "周二 22:00 不放行卢克");
        That(!RaidEntrySchedule.IsOpen(luke, At(DayOfWeek.Wednesday, 19, 0)), "只开卢克不影响其他日子");
        That(!RaidEntrySchedule.IsOpen([], At(DayOfWeek.Tuesday, 18, 0)), "空表关闭入场");
        var sunday = new[] { new RaidEntrySchedule.Window(7, 0, 24 * 60) };
        That(RaidEntrySchedule.IsOpen(sunday, At(DayOfWeek.Sunday, 23, 59)), "周日 23:59 仍属周日");
        That(!RaidEntrySchedule.IsOpen(sunday, At(DayOfWeek.Monday, 0, 0)), "周一 00:00 不属周日");
        That(!RaidEntrySchedule.TryParse("22:00", "18:00", 2, out _, out _), "结束不晚于开始被拒绝");

        string directory = Path.Combine(Path.GetTempPath(), "raid-windows-" + Guid.NewGuid().ToString("N"));
        var store = new RaidEntryStore(directory);
        That(!store.IsOpen("luke", At(DayOfWeek.Tuesday, 18, 0)) && !store.IsOpen("hyperspace", At(DayOfWeek.Tuesday, 18, 0)) && !store.IsOpen("prey", At(DayOfWeek.Tuesday, 18, 0)),
            "新目录默认三张团本都关");
        store.Replace("""{"luke":[{"weekday":2,"start":"18:00","end":"22:00"}],"hyperspace":[],"prey":[{"weekday":6,"start":"12:00","end":"24:00"}]}""");
        var again = new RaidEntryStore(directory);
        That(again.IsOpen("luke", At(DayOfWeek.Tuesday, 18, 0)) && !again.IsOpen("luke", At(DayOfWeek.Tuesday, 22, 0)), "重新打开后卢克时段仍在");
        That(!again.IsOpen("hyperspace", At(DayOfWeek.Tuesday, 19, 0)) && again.IsOpen("prey", At(DayOfWeek.Saturday, 12, 0)) && !again.IsOpen("prey", At(DayOfWeek.Saturday, 0, 0)),
            "超时空与普雷的时段互不影响");
        Directory.Delete(directory, true);
        Console.WriteLine($"PASS {Count - before} 团本每周入场时段断言");
    }
}
