using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
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
        That(!initial.FacilitiesDestroyed && radar.FacilitiesDestroyed && battery.FacilitiesDestroyed &&
            engine.SetSymbol(radar, "[radar state]", 0, start.AddSeconds(11)).State.FacilitiesDestroyed,
            "未拆设施成就保存不可逆历史，设施恢复或状态归零不洗掉本次已拆事实");
        var warning = initial with { Areas = initial.Areas.SetItem(14, initial.Areas[14] with { Warning = true }) };
        That(!engine.Occupy(warning, 14, true, start.AddSeconds(11)).State.BaseThreatened,
            "非基地前线的敌军及普通区域倒计时Warning不能充当基地危险");
        foreach (int area in new[] { 6, 7, 8 })
        {
            var threatened = engine.Occupy(initial with { Areas = initial.Areas.SetItem(area,
                new(Enemy: new(100, 1))) }, area, true, start.AddSeconds(11)).State;
            That(threatened.BaseThreatened && engine.CompleteArea(threatened, area, start.AddSeconds(12)).State.BaseThreatened,
                "原生三个基地前线任一出现敌军即锁存，清理敌军不洗掉已发生的危险");
            var neutral = engine.Occupy(initial with { Areas = initial.Areas.SetItem(area,
                new(Monster: new(101, 1), Warning: true)) }, area, true, start.AddSeconds(11)).State;
            That(!neutral.BaseThreatened, "前线中立怪与Warning本身不等于敌军威胁");
        }
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

    internal static async Task RunTimerProjectionAsync(FiendwarScript team, FiendwarScript guide)
    {
        int before = Count;
        foreach (var script in new[] { team, guide })
        {
            var clock = new LukeRaidClockTests.Clock(DateTimeOffset.FromUnixTimeSeconds(1793410200));
            var battle = new AntonRaidBattle(script.Script, kind: RaidKind.Fiendwar, clock: clock, fiendwar: script);
            var view = battle.View;
            var deadline = view.Fiendwar!.Deadline;
            uint total = script.Guide ? 1800u : 3600u;
            using var owner = new AccountOwners().Acquire("fiendwar-timer-" + script.Guide, () => { });
            var session = new GameSession(null!, null!, owner);

            async Task<List<byte[]>> Capture(Action<GameConnectionContext> post, Action beforeWrite)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var stream = new GameSenderBudgetTests.HeldStream();
                var remainingFrames = new List<byte[]>();
                await using var sender = new GameConnectionSender(owner, stream, stop, 0,
                    (_, opcode, _, body, _) => { if (opcode == 597) remainingFrames.Add(body.ToArray()); });
                var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
                var held = sender.Send(0, 2, [1], "fiendwar_timer_fixture_hold");
                await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try { post(context); beforeWrite(); }
                finally { stream.Release.TrySetResult(); }
                await held.WaitAsync(TimeSpan.FromSeconds(5));
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                That(sender.PendingBytes == 0, "漩涡计时及跳过的旧快照完整释放发送预算");
                return remainingFrames;
            }

            bool actorCurrent = true;
            var initial = await Capture(context =>
            {
                RaidCombatProjection.PostFiendwarSnapshot(context, battle, view, () => true);
                RaidCombatProjection.PostFiendwarSnapshot(context, battle, view, () => actorCurrent);
                var otherBattle = new AntonRaidBattle(script.Script, kind: RaidKind.Fiendwar, clock: clock, fiendwar: script);
                RaidCombatProjection.PostFiendwarSnapshot(context, battle, otherBattle.View, () => true);
            }, () => { clock.AdvanceMillisecondsWithoutCallbacks(7250); actorCurrent = false; });
            That(initial.Count == 1 && initial[0].Length == 5 && initial[0][0] == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(initial[0].AsSpan(1)) == total - 7,
                "团队60分钟/引导30分钟使用597槽0；慢队列按写帧时实际余秒，旧actor和另一实例不发布");

            var restored = await Capture(context =>
            {
                RaidCombatProjection.PostFiendwarSnapshot(context, battle, view, () => true);
                RaidCombatProjection.PostFiendwarSnapshot(context, battle, view, () => true);
            }, () => clock.AdvanceWithoutCallbacks(5));
            That(restored.Count == 2 && restored.All(body => body.Length == 5 && body[0] == 0 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1)) == total - 12) && battle.View.Fiendwar!.Deadline == deadline,
                "重返新连接及重复战况快照恢复同一截止，不重给完整攻略时间");

            var stale = await Capture(context => RaidCombatProjection.PostFiendwarSnapshot(context, battle, view, () => true),
                () => That(battle.SetMechanism(battle.Instance, view.Revision, "[radar state]",
                    1 - view.Symbols["[radar state]"]) is not null && battle.View.Revision > view.Revision,
                    "真实机制更新替换原战况revision"));
            That(stale.Count == 0, "排队后成为旧revision的计时快照不能覆盖新战况");

            That(battle.View is { State: AntonRaidBattleState.Fighting, PhaseExpired: false,
                Fiendwar: { Failed: false, Cleared: false } }, "到期回归从仍存活的正式战场开始");
            var expired = await Capture(context => RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true),
                () => clock.AdvanceWithoutCallbacks(checked((int)total)));
            That(expired.Count == 0, "排队期间真实deadline到期不续时、不猜发终局清理包");
        }
        Console.WriteLine($"PASS {Count - before} 漩涡双模式原生总计时投影断言");
    }
}
