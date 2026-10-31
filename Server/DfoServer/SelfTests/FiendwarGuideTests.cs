using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FiendwarGuideTests
{
    internal static void RunResources(RaidResources resources)
    {
        int before = Count;
        var guide = resources.FiendwarGuide!;
        That(guide is { ResourcesReady: true, EntryCatalog.Ready: true, Fiendwar.Guide: true },
            "原版引导资源独立完整装配，不借用团队目录");
        var script = guide.Fiendwar!;
        That(script.Areas.Keys.Order().SequenceEqual(Enumerable.Range(33, 11)) &&
            guide.Dungeons.Select(row => row.Id).Order().SequenceEqual(Enumerable.Range(5350, 9)) &&
            guide.Dungeons.All(row => resources.EntryCatalog!.Find(row.Id) is null) &&
            resources.Dungeons.All(row => guide.EntryCatalog!.Find(row.Id) is null), "两个模式的区域和地下城目录互斥");
        That(script.Script.WaitingRoom == new RaidWaitingRoom(57, 33) && script.Script.EntryCost == new RaidCost(10309054, 1) &&
            script.Script.RetryItem == new RaidCost(10309054, 1) && script.TotalSeconds == 1800 && script.MemberMax == 1 &&
            script.MinimumMembers == 1 && script.MinimumClears == 1, "引导保留原候战33、β票、30分钟与单人资格");
        var now = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.FromHours(8));
        var engine = new FiendwarBattlefield(script, new(1, 1, 1, 1, 1));
        foreach (int route in new[] { 0, 1 })
        {
            var state = engine.Create(Guid.NewGuid(), now, 1234, route).State;
            That(state.Symbols["[single raid type]"] == route && state.Symbols["[fiendwar init]"] == 1 &&
                state.Deadline == now.AddMinutes(30) && state.Merchant is null && state.Timers.IsEmpty,
                "引导按原路线初始化，不生成团队兵线或商人计时");
            That(state.Symbols["[radar state]"] == route && state.Symbols["[battery state]"] == 1 - route,
                "引导两条路线各自动处理另一设施");
            int[] expected = route == 0 ? [36, 37, 39, 42, 43] : [35, 36, 37, 39, 41, 42, 43];
            That(state.Areas.Where(row => row.Value.Enemy is not null).Select(row => row.Key).Order().SequenceEqual(expected) &&
                state.Areas[43].Enemy?.Kind == 13 && state.Dungeons[5356] == "open", "引导原固定遭遇及单世界罗什正确开放");
            int facility = route == 0 ? 38 : 40;
            var completed = engine.CompleteArea(state, facility, now.AddSeconds(1)).State;
            That(completed.Symbols[route == 0 ? "[movie radar on]" : "[movie battery on]"] == 1,
                "真实清理引导路线设施触发原增益符号");
            var terminal = engine.CompleteArea(completed, 43, now.AddSeconds(2)).State;
            That(terminal.Cleared && !terminal.Failed && terminal.Dungeons[5356] == "hide",
                "引导按5356真实终局规则结束，不等待团队黑白世界");
            That(engine.Tick(state, now.AddMinutes(30)).State is { Failed: true, Cleared: false }, "引导保留原30分钟失败边界");
        }
        Console.WriteLine($"PASS {Count - before} 漩涡原版引导资源与路线断言");
    }
}
