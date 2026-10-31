using System.Collections.Immutable;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidClockTests
{
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        PositiveTimerPreparation(actors);
        var clock = new Clock(); var store = new Store();
        var errors = new List<string>();
        var service = new LukeRaidClocks(clock, store, (stage, _) => errors.Add(stage), 1);
        try
        {
            var battle = Battle(false); var timer = service.Reserve(battle)!;
            clock.Advance(100);
            That(battle.View.Dungeons[10] == RaidDungeonState.Open, "SQL前停用预留不启动倒计时");
            timer.Activate(); var first = battle.View;
            clock.Advance(4);
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[noise]", 1);
            battle.Publish(battle.View);
            That(first.TimerAnchors.SequenceEqual(battle.View.TimerAnchors), "其它revision和广播不重置原SET单调锚点");
            clock.Advance(1);
            That(battle.View.Dungeons[10] == RaidDungeonState.Close && battle.View.Timers.IsEmpty, "自然5秒到期只消费一次原代并执行已支持状态规则");
            var expired = battle.View; clock.Advance(100);
            That(ReferenceEquals(expired, battle.View), "已到期timer不循环推进");
            var next = Battle(false);
            That(service.Reserve(next) is null, "活跃实例即使暂时无timer仍占原Runtime有界容量");
            foreach (var actor in actors) battle.DetachStart(actor);
            var nextTimer = service.Reserve(next)!; That(nextTimer is not null, "最后原成员退出释放原实例时钟容量");
            nextTimer!.Activate(); clock.Advance(1);
            long oldGeneration = next.View.TimerGenerations[new(1, 10)];
            next.SetMechanism(next.Instance, next.View.Revision, "[reset]", 1);
            clock.FireQueued(); clock.Advance(8);
            That(next.View.Dungeons[10] == RaidDungeonState.Open && next.View.Timers.IsEmpty, "RESET取消旧回调，迟到已排队回调不能关闭目标");
            next.SetMechanism(next.Instance, next.View.Revision, "[init]", 0);
            That(next.View.TimerGenerations[new(1, 10)] > oldGeneration, "重新SET签发新代而不复用旧deadline");
            clock.FireQueued(); clock.Advance(4);
            That(next.View.Dungeons[10] == RaidDungeonState.Open, "旧回调不能提前消费新代");
            clock.Advance(1); That(next.View.Dungeons[10] == RaidDungeonState.Close, "新代到期按自己的5秒执行");
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        int callbacks = clock.Callbacks; clock.Advance(100);
        That(clock.Callbacks == callbacks && errors.Count == 0, "Runtime收尾后无孤儿时钟或错误");

        var retryClock = new Clock(); var retryStore = new Store { Failures = 5 };
        var retryErrors = new List<string>();
        var retryService = new LukeRaidClocks(retryClock, retryStore, (stage, _) => retryErrors.Add(stage));
        try
        {
            var battle = new AntonRaidBattle(Script(true), kind: RaidKind.Luke, completion: Completion(), clock: retryClock);
            foreach (var actor in actors) That(battle.JoinStart(actor), "到期终结登记原成员");
            using var timer = retryService.Reserve(battle)!; timer.Activate();
            retryClock.Advance(5);
            var pending = battle.PendingLukeSettlement();
            That(pending is not null && battle.View.State == AntonRaidBattleState.Fighting, "SQL失败保持密封pending，不提前Reward");
            battle.DetachStart(actors[^1]);
            That(battle.PendingLukeSettlement() is not null, "部分原成员离开不取消已密封候选的自动重试");
            foreach (int delay in new[] { 1, 2, 4, 8 })
            {
                retryClock.Advance(delay);
                That(ReferenceEquals(pending, battle.PendingLukeSettlement()) && battle.View.State == AntonRaidBattleState.Fighting,
                    "多次存储失败仍同事件/anchor/计划且不释放活跃时钟");
            }
            retryClock.Advance(16);
            That(retryStore.Calls == 6 && battle.View.State == AntonRaidBattleState.Reward && battle.PendingLukeSettlement() is null,
                "超过旧四次阈值仍可按同密封候选恢复提交，不重放timer");
            That(retryStore.Seen.All(candidate => ReferenceEquals(candidate, pending)) && retryErrors.All(stage => stage == "settlement"),
                "重试只属于持久候选，不重新解释或重复签发阶段事件");
            int calls = retryStore.Calls; retryClock.Advance(100);
            That(retryStore.Calls == calls, "提交成功后不再重做阶段SQL");
        }
        finally { retryService.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var manualClock = new Clock(); var manualStore = new Store { Failures = 1 };
        var manualService = new LukeRaidClocks(manualClock, manualStore, (_, _) => { });
        try
        {
            var battle = new AntonRaidBattle(Script(true), kind: RaidKind.Luke, completion: Completion(), clock: manualClock);
            foreach (var actor in actors) That(battle.JoinStart(actor), "请求先恢复场景登记原成员");
            using var timer = manualService.Reserve(battle)!; timer.Activate(); manualClock.Advance(5);
            var candidate = battle.PendingLukeSettlement()!;
            battle.AcceptLukeSettlement(candidate, manualStore.CommitLukePhase(candidate));
            That(timer.RetryRemaining is null && battle.View.State == AntonRaidBattleState.Reward,
                "正常请求先提交同一候选后立即清理旧退避，不延迟后续phase的SET");
            int callbacksBefore = manualClock.Callbacks; manualClock.Advance(1);
            That(manualClock.Callbacks == callbacksBefore && manualStore.Calls == 2, "已被请求恢复的旧重试不再唤醒或重复SQL");
        }
        finally { manualService.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        var faultClock = new Clock();
        var faultStore = new Store { OnCommit = () => throw new InvalidDataException("fixture identity mismatch") };
        var faultService = new LukeRaidClocks(faultClock, faultStore, (_, _) => { });
        try
        {
            var battle = new AntonRaidBattle(Script(true), kind: RaidKind.Luke, completion: Completion(), clock: faultClock);
            foreach (var actor in actors) That(battle.JoinStart(actor), "损坏回执场景登记原成员");
            using var timer = faultService.Reserve(battle)!; timer.Activate(); faultClock.Advance(5);
            That(battle.View.ClockFaulted && battle.View.State == AntonRaidBattleState.Fighting && battle.PendingLukeSettlement() is null,
                "身份/回执损坏故障封闭，不冒充Reward且回城入口不重复执行坏SQL");
            faultClock.Advance(100);
            That(faultStore.Calls == 1, "确定性损坏不进行无效自动重试");
            foreach (var actor in actors) battle.DetachStart(actor);
        }
        finally { faultService.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        PhaseTimeout(actors);
        PhaseReservedOperations(actors);
        RecipientFailure(actors);
        Shutdown(actors);
        Console.WriteLine($"Luke raid clocks: {Count - before} assertions passed");

        AntonRaidBattle Battle(bool terminal)
        {
            var result = new AntonRaidBattle(Script(terminal), kind: RaidKind.Luke, clock: clock);
            foreach (var actor in actors) That(result.JoinStart(actor), "定时实例只引用当前原成员");
            return result;
        }
    }

    static void PositiveTimerPreparation(RaidActor[] actors)
    {
        Bytes(Network.Builders.RaidCombatPackets.LukePositiveTimer(1, 5066, 0, 150),
            [1, 0xca, 0x13, 0, 0, 0, 0, 0, 0, 150, 0, 0, 0], "596正数13B独立字节期望，明确中间raw0");
        Bytes(Network.Builders.RaidCombatPackets.LukePositiveTimer(2, 5063, 0, 86400),
            [2, 0xc7, 0x13, 0, 0, 0, 0, 0, 0, 0x80, 0x51, 1, 0], "596恢复类与一天上界独立字节期望");
        foreach (var (kind, dungeon, raw, seconds) in new (sbyte, int, uint, uint)[]
            {(-1, 10, 0, 1), (0, 10, 0, 1), (3, 10, 0, 1), (1, 0, 0, 1), (1, -1, 0, 1),
             (1, 10, 1, 1), (1, 10, 0, 0), (2, 10, 0, 0), (1, 10, 0, 86401)})
        {
            bool rejected = false;
            try { Network.Builders.RaidCombatPackets.LukePositiveTimer(kind, dungeon, raw, seconds); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            That(rejected, "596拒绝非法kind/key/raw/时长，零秒不能冒充RESET");
        }
        var clock = new Clock();
        var service = new LukeRaidClocks(clock, new Store(), (_, _) => { });
        try
        {
            var battle = new AntonRaidBattle(Script(false), kind: RaidKind.Luke, clock: clock);
            foreach (var actor in actors) That(battle.JoinStart(actor), "596准备只引用当前领域actor，不安装网络策略");
            var key = new RaidTimer(1, 10);
            long generation = battle.View.TimerGenerations[key];
            using var lease = service.Reserve(battle)!;
            That(battle.LukeRuleTimerSeconds(battle.Instance, 0, key, generation) is null, "费用提交前预留时钟不准备显示");
            lease.Activate();
            var beforeQuery = battle.View;
            That(battle.LukeRuleTimerSeconds(battle.Instance, 0, key, generation) == 5, "激活后取原单调deadline");
            That(ReferenceEquals(beforeQuery, battle.View), "剩余时间查询不发布新View或推进规则");
            That(battle.LukeRuleTimerSeconds(Guid.NewGuid(), 0, key, generation) is null &&
                battle.LukeRuleTimerSeconds(battle.Instance, 1, key, generation) is null &&
                battle.LukeRuleTimerSeconds(battle.Instance, 0, key, generation + 1) is null &&
                battle.LukeRuleTimerSeconds(battle.Instance, 0, new(2, 10), generation) is null &&
                battle.LukeRuleTimerSeconds(battle.Instance, 0, new(1, 999), generation) is null,
                "不同实例/phase/代/key及未登记资源拒绝");
            clock.AdvanceMillisecondsWithoutCallbacks(4100);
            That(battle.LukeRuleTimerSeconds(battle.Instance, 0, key, generation) == 1, "正剩余0.9秒向上取整1，不回放初始5秒");
            clock.AdvanceMillisecondsWithoutCallbacks(900);
            That(battle.LukeRuleTimerSeconds(battle.Instance, 0, key, generation) is null, "正好截止但回调未执行也不准备零秒");
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[reset]", 1);
            That(battle.LukeRuleTimerSeconds(battle.Instance, 0, key, generation) is null, "服务端RESET只返回不可准备，不假装客户端已清理");
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[init]", 0);
            long next = battle.View.TimerGenerations[key];
            That(next > generation && battle.LukeRuleTimerSeconds(battle.Instance, 0, key, generation) is null &&
                battle.LukeRuleTimerSeconds(battle.Instance, 0, key, next) == 5, "重设签发新代，旧代不能借当前时长复活");
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[reset]", 0);
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[reset]", 1);
            That(!battle.View.Timers.ContainsKey(key) &&
                battle.LukeRuleTimerSeconds(battle.Instance, 0, key, next) is null, "未过期新代RESET确实删除timer并拒绝准备");
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[init]", 0);
            next = battle.View.TimerGenerations[key];
            That(battle.LukeRuleTimerSeconds(battle.Instance, 0, key, next) == 5, "离场检查前恢复有效正数timer");
            foreach (var actor in actors) battle.DetachStart(actor);
            That(battle.LukeRuleTimerSeconds(battle.Instance, 0, key, next) is null, "最后成员离开不再准备，不发送猜测删除包");
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    static void PhaseReservedOperations(RaidActor[] actors)
    {
        foreach (bool commit in new[] { false, true })
        {
            var clock = new Clock(); var store = new Store();
            var service = new LukeRaidClocks(clock, store, (_, _) => { }, 1);
            try
            {
                var script = AntonRaidBattleTests.Script(10, 20);
                script = script with { Phases = script.Phases.SetItem(0, script.Phases[0] with
                    { Dungeons = script.Phases[0].Dungeons.Add(script.Phases[0].Dungeons[0] with { Id = 11 }) }) };
                var battle = new AntonRaidBattle(script, kind: RaidKind.Luke,
                    clock: clock, revival: new("fixture/revival"), phaseTimeout: new("fixture/local-seconds", [5, 5]));
                foreach (var actor in actors) That(battle.JoinStart(actor), "跨deadline预留登记原成员");
                using var timer = service.Reserve(battle)!; timer.Activate();
                var run = Guid.NewGuid();
                var rows = actors.Take(4).Select(actor => (Actor: actor,
                    Identity: new Game.Dungeons.DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
                using var entry = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 10, rows)!;
                entry.Install();
                using var revival = battle.ReserveLukeRevival(rows[0].Actor, rows[0].Identity, 1)!;
                var lateRun = Guid.NewGuid();
                var late = actors.Skip(4).Select(actor => (Actor: actor,
                    Identity: new Game.Dungeons.DungeonRunIdentity(actor.Character, 0, 1, lateRun, 1))).ToArray();
                using var pending = battle.ReserveDungeon(new(battle.Instance, 0, 2, RaidKind.Luke), 11, late);
                That(pending is not null && revival is not null, "两队分别持截止前入场/复活预留");
                clock.Advance(5);
                if (commit) { pending!.Install(); revival!.Commit(); revival.Commit(); }
                else { pending!.Dispose(); revival!.Dispose(); }
                foreach (var row in late) battle.ReleaseDungeon(row.Actor, lateRun);
                foreach (var row in rows) battle.ReleaseDungeon(row.Actor, run);
                That(battle.View.PhaseExpired && !battle.HasPendingTransition && store.Calls == 0 &&
                    battle.ReserveLukeRevival(rows[0].Actor, rows[0].Identity, 2) is null,
                    "截止前复活预留成功/失败均可收尾，重复确认不奖励，新复活拒绝");
                foreach (var actor in actors) battle.DetachStart(actor);
                var next = new AntonRaidBattle(AntonRaidBattleTests.Script(10, 20), kind: RaidKind.Luke);
                foreach (var actor in actors) next.JoinStart(actor);
                using var nextLease = service.Reserve(next);
                That(nextLease is not null, "失败团最后原成员离开释放容量，不伪装自动重开");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }

    static void PhaseTimeout(RaidActor[] actors)
    {
        var clock = new Clock(); var store = new Store();
        var service = new LukeRaidClocks(clock, store, (_, _) => { });
        try
        {
            var program = AntonRaidBattleTests.Script(10, 20);
            program = program with { Symbols=program.Symbols.Add("[deadline observed]",901),
                Phases=program.Phases.SetItem(0,program.Phases[0] with {Rules=program.Phases[0].Rules.Add(new(
                    [new("[phase end]",900,null,null,null,null,[0])],
                    [new("[set]",901,null,"[deadline observed]",null,null,[1]),new("[fail phase]",902,null,null,null,null,[])]))}) };
            var battle = new AntonRaidBattle(program, kind: RaidKind.Luke,
                clock: clock, phaseTimeout: new("fixture/local-phase-seconds", [10, 20]));
            foreach (var actor in actors) That(battle.JoinStart(actor), "阶段时钟登记原成员");
            using var timer = service.Reserve(battle)!;
            clock.Advance(100);
            That(battle.View.PhaseClockGeneration == 0 && !battle.View.PhaseExpired, "费用SQL前没有phase锚点");
            timer.Activate();
            That(battle.View.Timers.IsEmpty && battle.LukePhaseSeconds(battle.Instance, 0, 1) == 10,
                "无DGN计时器也按实际Activate启动阶段deadline");
            clock.Advance(9);
            battle.Publish(battle.View);
            That(battle.LukePhaseSeconds(battle.Instance, 0, 1) == 1, "广播不续期，返回实际剩余而非总时长");
            clock.AdvanceWithoutCallbacks(1);
            That(battle.SetMechanism(battle.Instance, battle.View.Revision, "[hp]", 0) is null,
                "调度延迟时机制入口也在deadline拒绝，不抢先封存成功");
            clock.FireQueued();
            That(battle.View.PhaseExpired && battle.View.State == AntonRaidBattleState.Fighting &&
                battle.View.Dungeons.Values.All(value => value == RaidDungeonState.Hide), "失败独立标记并封图，不猜原生state");
            That(battle.View.Symbols["[deadline observed]"]==1 && battle.View.Deferred.IsEmpty,
                "阶段deadline先执行原PhaseEnd规则，再由FAIL事实触发退场，不遗留无人消费的待决动作");
            That(store.Calls == 0 && battle.PendingLukeSettlement() is null && !battle.HasPendingTransition,
                "超时不调用奖励SQL，也不等待成功receipt屏障");
            That(battle.LukePhaseSeconds(battle.Instance, 0, 1) is null, "已失败代不再发布597");
            var expired = battle.View; clock.Advance(100);
            That(ReferenceEquals(expired, battle.View), "重复时钟不会再次超时或推进阶段");
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        var retryClock = new Clock(); var retryStore = new Store { Failures = 2 };
        var retryService = new LukeRaidClocks(retryClock, retryStore, (_, _) => { });
        try
        {
            var battle = new AntonRaidBattle(Script(true), kind: RaidKind.Luke, completion: Completion(),
                clock: retryClock, phaseTimeout: new("fixture/local-phase-seconds", [6, 20]));
            foreach (var actor in actors) That(battle.JoinStart(actor), "成功先封存场景登记原成员");
            using var timer = retryService.Reserve(battle)!; timer.Activate(); retryClock.Advance(5);
            var pending = battle.PendingLukeSettlement(); retryClock.Advance(1);
            That(pending is not null && ReferenceEquals(pending, battle.PendingLukeSettlement()) && !battle.View.PhaseExpired,
                "成功已封存，SQL重试跨阶段deadline不得倒退为失败");
            retryClock.Advance(2);
            That(battle.View.State == AntonRaidBattleState.Reward && !battle.View.PhaseExpired, "同候选成功后进入Reward");
            var candidate = retryStore.Seen[0];
            foreach (var actor in actors)
                battle.ConfirmReward(actor, new(battle.Instance, 0, 0, 1, actor.Character, candidate.Policy.Rewards.Source,
                    [new(100, 1)], false, candidate.Week) { RaidType = RaidKind.Luke });
            That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision)?.After.Phase == 1 &&
                battle.LukePhaseSeconds(battle.Instance, 1, 2) == 20, "实际P2Initialize提交才建立第二阶段锚点");
            retryClock.FireQueued();
            That(!battle.View.PhaseExpired && battle.LukePhaseSeconds(battle.Instance, 0, 1) is null,
                "旧回调与旧phase不能提前结束或显示新阶段");
            retryClock.Advance(20);
            That(battle.View.PhaseExpired && retryStore.Calls == 3, "P2没有PhaseEnd规则仍走独立本服失败，不新增奖励SQL");
        }
        finally { retryService.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    static void RecipientFailure(RaidActor[] actors)
    {
        var clock = new Clock(); var store = new Store(); int notices = 0, errors = 0;
        var previous = actors.Select(actor => actor.Session.RaidBattleNoticeArrived).ToArray();
        var service = new LukeRaidClocks(clock, store, (stage, _) => { That(stage == "recipient", "接收者异常独立分类"); errors++; });
        try
        {
            var battle = new AntonRaidBattle(Script(true), kind: RaidKind.Luke, completion: Completion(), clock: clock);
            foreach (var actor in actors)
            {
                That(battle.JoinStart(actor), "发布异常场景登记原成员");
                actor.Session.RaidBattleNoticeArrived = (_, _) => notices++;
            }
            actors[0].Session.RaidBattleNoticeArrived = (_, _) => throw new InvalidOperationException("fixture stale owner");
            using var timer = service.Reserve(battle)!; timer.Activate(); clock.Advance(5);
            That(store.Calls == 1 && !battle.View.ClockFaulted && battle.View.State == AntonRaidBattleState.Reward &&
                errors == 1 && notices == actors.Length - 1, "单接收者失败不阻塞其余成员或回滚已提交Reward");
            clock.Advance(100);
            That(store.Calls == 1 && errors == 1 && notices == actors.Length - 1, "发布失败不重复SQL或阶段动画");
            var candidate = store.Seen.Single();
            foreach (var actor in actors)
                battle.ConfirmReward(actor, new(battle.Instance, 0, 0, 1, actor.Character, candidate.Policy.Rewards.Source,
                    [new(100, 1)], false, candidate.Week) { RaidType = RaidKind.Luke });
            That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision)?.After.Phase == 1, "原领取事实后进入新phase");
            var next = battle.View; clock.FireQueued();
            That(ReferenceEquals(next, battle.View) && store.Calls == 1 && errors == 1 && notices == actors.Length - 1,
                "旧phase排队回调不执行新phase规则、重放SQL或广播");
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            for (int index = 0; index < actors.Length; index++) actors[index].Session.RaidBattleNoticeArrived = previous[index];
        }
    }

    static void Shutdown(RaidActor[] actors)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int notices = 0;
        var previous = actors.Select(actor => actor.Session.RaidBattleNoticeArrived).ToArray();
        var store = new Store { OnCommit = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("fixture release timeout"); } };
        var service = new LukeRaidClocks(TimeProvider.System, store, (_, _) => { });
        try
        {
            var battle = new AntonRaidBattle(Script(true, 1), kind: RaidKind.Luke, completion: Completion(), clock: TimeProvider.System);
            foreach (var actor in actors)
            {
                That(battle.JoinStart(actor), "停服竞态登记同实例成员");
                actor.Session.RaidBattleNoticeArrived = (_, _) => Interlocked.Increment(ref notices);
            }
            using var timer = service.Reserve(battle)!; timer.Activate();
            That(entered.Wait(TimeSpan.FromSeconds(5)), "真实ITimer自然到期进入原存储用例，非直接调用Expire");
            var stopping = service.DisposeAsync().AsTask();
            That(!stopping.IsCompleted, "停服必须等待已经进入的同步持久化回调，不能提前释放Runtime");
            release.Set(); stopping.GetAwaiter().GetResult();
            That(store.Calls == 1 && notices == 0, "停服等待一次提交收尾，不在取消后发布或重试");
        }
        finally
        {
            release.Set(); service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            for (int index = 0; index < actors.Length; index++) actors[index].Session.RaidBattleNoticeArrived = previous[index];
        }
    }

    static LukeRaidCompletionPolicy Completion() => new("fixture/clock-terminal", 2,
        new(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
        Enumerable.Range(0, 2).Select(phase => new LukePhaseCompletionPolicy(0, 2,
            new("fixture/clock-phase" + phase, [new(0, 1, [new(100, 1)], [])], new(0, (byte)phase, 1, 0, 0, 0)))).ToImmutableArray(),
        LukeParticipationCutoff.TerminalCommittedFacts);

    static RaidScript Script(bool terminal, int seconds = 5)
    {
        var raw = AntonRaidBattleTests.Script(10, 20);
        RaidClause C(string tag, int token, int? dungeon = null, string? symbol = null,
            RaidDungeonState? state = null, RaidTimer? timer = null, params int[] integers) =>
            new(tag, token, dungeon, symbol, state, timer, integers.ToImmutableArray());
        var timer = new RaidTimer(1, 10);
        return raw with
        {
            Symbols = raw.Symbols.Add("[init]", 200).Add("[reset]", 201).Add("[noise]", 202),
            Phases = raw.Phases.SetItem(0, raw.Phases[0] with { Rules =
            [
                new([C("[if]", 100, symbol: "[init]", integers: [0])],
                    [C("[set]", 101, symbol: "[init]", integers: [1]), C("[set timer]", 102, timer: timer, integers: [1, 10, seconds])]),
                new([C("[on change symbol]", 103, symbol: "[reset]"), C("[if]", 104, symbol: "[reset]", integers: [1])],
                    [C("[reset timer]", 105, timer: timer, integers: [1, 10])]),
                new([C("[check timer end]", 106, timer: timer)],
                    [terminal ? C("[clear phase]", 107) : C("[set dungeon state]", 108, 10, state: RaidDungeonState.Close)])
            ] })
        };
    }

    sealed class Store : ILukeRaidLifecycleStore
    {
        public bool IsRetryable(Exception error) => error is TimeoutException;
        internal Action? OnCommit;
        internal int Failures;
        internal int Calls;
        internal readonly List<AntonRaidBattle.LukePhaseSettlement> Seen = [];
        public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate)
        {
            Calls++; Seen.Add(candidate); OnCommit?.Invoke();
            if (Calls <= Failures) throw new TimeoutException("fixture settlement retry");
            return new(candidate.Members.Select(member => new RaidPhaseEligibility(candidate.Instance, RaidKind.Luke,
                candidate.Phase, candidate.Event, candidate.Anchor.ToUnixTimeSeconds(), member.Character, false, true,
                "fixture/clock", candidate.Policy.Rewards.Source, candidate.Week, candidate.Policy.Rewards.Offers)).ToImmutableArray());
        }
    }

    internal sealed class Clock(DateTimeOffset? origin = null) : TimeProvider
    {
        readonly List<Timer> timers = [];
        readonly object gate = new();
        long stamp;
        internal int Callbacks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() { lock (gate) return stamp; }
        public override DateTimeOffset GetUtcNow() => (origin ?? DateTimeOffset.Parse("2026-10-25T00:00:00Z")).AddMilliseconds(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state); lock (gate) timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        internal void AdvanceMillisecondsWithoutCallbacks(int milliseconds) { lock (gate) stamp += milliseconds; }
        internal void AdvanceWithoutCallbacks(int seconds) { lock (gate) stamp += seconds * 1000L; }
        internal void Advance(int seconds)
        {
            AdvanceWithoutCallbacks(seconds);
            for (int count = 0; count < 128; count++)
            {
                Timer? next;
                lock (gate)
                {
                    next = timers.FirstOrDefault(timer => !timer.Stopped && !timer.Inert && timer.Due <= stamp);
                    if (next is null) return;
                    next.Due = long.MaxValue; Callbacks++;
                }
                next.Callback(next.State);
            }
            throw new InvalidOperationException("clock fixture exceeded bounded callbacks");
        }
        internal void FireQueued()
        {
            Timer[] queued; lock (gate) queued = timers.Where(timer => !timer.Stopped && !timer.Inert).ToArray();
            foreach (var timer in queued) timer.Callback(timer.State);
        }
        sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long Due = long.MaxValue;
            internal bool Stopped;
            // 共享公会轮询（NPC到期1s、殿堂等待5s）与卢克阶段无关：接受注册但永不进入本时钟的到期推进与排队触发，
            // 保证推进时钟只会触发卢克自己的到期；其余周期定时器仍必须拒绝。
            internal bool Inert;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                {
                if (Stopped) return false;
                Inert = period != Timeout.InfiniteTimeSpan && dueTime == period &&
                    (period == TimeSpan.FromSeconds(1) || period == TimeSpan.FromSeconds(5));
                That(period == Timeout.InfiniteTimeSpan || Inert, "除共享公会轮询外只有one-shot timer，不叠加周期任务");
                Due = Inert || dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(clock.stamp + (long)Math.Ceiling(dueTime.TotalMilliseconds));
                return true;
                }
            }
            public void Dispose() { lock (clock.gate) { Stopped = true; Due = long.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
