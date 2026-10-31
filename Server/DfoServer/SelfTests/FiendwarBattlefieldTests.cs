using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FiendwarBattlefieldTests
{
    internal static void RunResources(FiendwarScript script)
    {
        int before = Count;
        var pacing = new FiendwarPacing();
        var engine = new FiendwarBattlefield(script, pacing);
        var start = DateTimeOffset.FromUnixTimeSeconds(1793410200);
        var instance = Guid.NewGuid();
        var initial = engine.Create(instance, start, 9527).State;
        That(initial.Instance == instance && initial.Deadline - start == TimeSpan.FromHours(1) && initial.Symbols["[fiendwar init]"] == 1,
            "漩涡启动规则真实执行，总60分钟不放缓");
        That(initial.Areas.Where(row => row.Value.Enemy is not null).Select(row => row.Key).Order().SequenceEqual(new[] { 14, 16, 18 }) &&
            initial.Merchant is { End: var end } && end > start && !initial.Failed && !initial.Cleared,
            "三路初始兵线及随机商人均来自原规则");
        That(initial.Timers[new("top_create_enemy", 19)].End - start == TimeSpan.FromSeconds(600) &&
            initial.Timers[new("move_enemy", 16)].End - start == TimeSpan.FromSeconds(260), "出兵与推进统一放缓2倍");
        var view = new AntonRaidBattleView(instance, 0, 0, AntonRaidBattleState.Fighting,
            initial.Symbols, System.Collections.Immutable.ImmutableDictionary<int, RaidDungeonState>.Empty,
            System.Collections.Immutable.ImmutableDictionary<RaidTimer, int>.Empty)
        {
            Fiendwar = initial,
            FiendwarDungeonTimers = System.Collections.Immutable.ImmutableDictionary<int, FiendwarTimer>.Empty
                .Add(15, new(start, start.AddSeconds(720)))
        };
        var display = RaidCombatProjection.FiendwarTimerSnapshot(view);
        That(display[(16, 0)] == new FiendwarTimerRow(16, 0, 1793410460, 260) &&
            display[(19, 2)] == new FiendwarTimerRow(19, 2, 1793410800, 600) &&
            display[(15, 12)] == new FiendwarTimerRow(15, 12, 1793410920, 720),
            "原生推进/设施/局部时钟分别发布1528三槽，局部字段为deadline而非start");
        var afterExit = RaidCombatProjection.FiendwarTimerSnapshot(view with { FiendwarDungeonTimers = view.FiendwarDungeonTimers.Clear() });
        That(RaidCombatProjection.FiendwarTimerDelta(display, afterExit) is [{ Area: 15, Kind: 12, End: 0, Duration: 0 }],
            "局部退场只清该区域战斗timer，不重置其它兵线时钟");
        That(RaidCombatProjection.FiendwarTimerDelta(display, display).Length == 0,
            "重复快照不重发原生倒计时");
        var endedDisplay = RaidCombatProjection.FiendwarTimerSnapshot(view with { Fiendwar = initial with { Failed = true } });
        That(endedDisplay.IsEmpty && RaidCombatProjection.FiendwarTimerDelta(display, endedDisplay)
            .All(row => row.End == 0 && row.Duration == 0), "炸团对已显示时钟发送完整清除行");
        var monsters = engine.Tick(initial, start.AddSeconds(1)).State;
        That(monsters.Areas.Values.Count(row => row.Monster is not null) == 3 && initial.Areas.Values.All(row => row.Monster is null),
            "原中立怪生成规则执行，计算不会修改输入快照");
        var replay = engine.Tick(initial, start.AddSeconds(1)).State;
        That(monsters.RandomState == replay.RandomState && monsters.NextUnit == replay.NextUnit &&
            monsters.Areas.All(row => row.Value == replay.Areas[row.Key]), "相同冻结随机状态重放结果相同，不随重发重抽");
        var radar = engine.CompleteArea(initial, 15, start.AddSeconds(10)).State;
        That(radar.Symbols["[radar state]"] == 1 && radar.Symbols["[movie radar on]"] == 1 &&
            radar.Symbols["[battery state]"] == 0, "雷达真实完成只激活自己的效果");
        var battery = engine.CompleteArea(initial, 17, start.AddSeconds(10)).State;
        That(battery.Symbols["[battery state]"] == 1 && battery.Timers.ContainsKey(new("activate_battery", 17)), "炮塔独立冷却规则保留");
        var one = engine.CompleteArea(initial, 27, start.AddSeconds(20)).State;
        That(one.Symbols["[top command center state]"] == 0 && one.Timers[new("top_restore_center", 27)].End == start.AddSeconds(1460) &&
            one.Symbols["[top boss state]"] == 0, "一兵营仍恢复且不提前解锁罗什");
        var two = engine.CompleteArea(one, 28, start.AddSeconds(25)).State;
        var all = engine.CompleteArea(two, 29, start.AddSeconds(30)).State;
        That(all.Symbols["[top boss state]"] == 1 && all.Symbols["[bot boss state]"] == 1 &&
            all.Timers.Keys.All(k => !k.Kind.EndsWith("_restore_center", StringComparison.Ordinal)),
            "三兵营成果汇合后解锁双侧并取消恢复timer");
        var lateRestore = engine.SetSymbol(all, "[top command center state]", 1, start.AddSeconds(31)).State;
        That(lateRestore.Symbols["[top command center state]"] == 0, "解锁后的旧恢复输入不能锁回入口");
        var lost = engine.Tick(initial, start.AddMinutes(30)).State;
        That(lost.Failed && !lost.Cleared && lost.Timers.IsEmpty && lost.Merchant is null,
            "无人回防三路推进到基地会失败，不因统一放缓免除失败");
        var timeout = engine.Tick(all, start.AddHours(1)).State;
        That(timeout.Failed && !timeout.Cleared && engine.Tick(timeout, start.AddHours(2)).State == timeout,
            "全团超时失败且终态幂等");
        Console.WriteLine($"PASS {Count - before} 漩涡战场规则断言");
    }
}
