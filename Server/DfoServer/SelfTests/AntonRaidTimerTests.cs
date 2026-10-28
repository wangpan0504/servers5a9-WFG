using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 真实PVF规则与可控时钟；清图/选定孵化目标为显式领域事实，不冒充自然随机或实机战斗。
internal static class AntonRaidTimerTests
{
    internal static void Run(string database, TcpTestResources resources)
    {
        var repository = new CharacterRepository(database);
        using var owner = new AccountOwners().Acquire("timer-rules", () => { });
        var created = repository.Create(owner, CharacterCreation.Prepare("TimerRules", resources.Characters.Get(0),
            resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
        var session = new GameSession(repository, resources.Towns, owner);
        session.Select(created.Slot); session.MarkReady();
        var actor = new RaidActor(session, owner, session.SelectionEpoch, created.Id, created.Name, new(2, 19));
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var script = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main, resources.ItemResources.Items).Find(0)!.Resources!.Script;
        That(script.Headers.Single(row => row.Tag == "[phase time over]").Values.Select(row => row.Number).SequenceEqual(new[] { 2400, 2400 }) &&
            script.Headers.Single(row => row.Tag == "[phase break time]").Values.Single().Number == 600,
            "PHASE TIME OVER与休息只读匹配2400/2400、600；秒制仅本服约定");
        foreach (int dungeon in new[] { 221, 222, 223, 224 }) Hatchery(dungeon);
        TimeoutRetry();

        void Hatchery(int dungeon)
        {
            var clock = new LukeRaidClockTests.Clock(); var store = new Store();
            var battle = Battle(clock, false);
            var errors = new List<string>();
            var service = new LukeRaidClocks(clock, store, (stage, _) => errors.Add(stage));
            try
            {
                using var lease = service.Reserve(battle)!; lease.Activate();
                Clear(battle, 210); Clear(battle, 213); Clear(battle, 215); Settle(battle, store); Rewards(battle);
                clock.Advance(179);
                That(!battle.View.Deferred.Values.Any(row => row.Tag == "[open random hatchery]"), "180前不能预开随机目标");
                clock.Advance(1);
                int token = battle.View.Deferred.Single(row => row.Value.Tag == "[open random hatchery]").Key;
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Hide, "UNIMPLEMENTED：180只产生随机待决，不猜目标或权重");
                That(battle.CompleteDeferred(battle.Instance, battle.View.Revision, token, dungeon) is not null,
                    "显式选择HATCHERY INFO登记目标，仅验证其后已证计时规则");
                var first = battle.View;
                That(first.Timers[new(1, dungeon)] == 120 && first.Timers[new(3, dungeon)] == 120, "open同时设timer1/3各120");
                clock.Advance(119);
                That(battle.View.Symbols[$"[{dungeon} hatchery fail]"] == 0, "120前不触发孵化失败");
                clock.Advance(1);
                That(battle.View.Symbols[$"[{dungeon} hatchery fail]"] == 1 &&
                    battle.View.Symbols[$"[{dungeon} hatchery named create]"] == 1 &&
                    battle.View.Timers[new(1, dungeon)] == 45 && battle.View.Timers[new(3, dungeon)] == 50,
                    "120到期沿PVF分别置失败/精英创建符号并续45/50，不伪造刷怪");
                long failGeneration = battle.View.TimerGenerations[new(1, dungeon)];
                long namedGeneration = battle.View.TimerGenerations[new(3, dungeon)];
                clock.Advance(44);
                That(battle.View.TimerGenerations[new(1, dungeon)] == failGeneration, "45续期边界前不重复消费");
                clock.Advance(1);
                That(battle.View.TimerGenerations[new(1, dungeon)] > failGeneration &&
                    battle.View.TimerGenerations[new(3, dungeon)] == namedGeneration, "45到期只续timer1，timer3仍独立");
                clock.Advance(5);
                That(battle.View.TimerGenerations[new(3, dungeon)] > namedGeneration, "50到期独立续timer3");
                Clear(battle, dungeon);
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Close && battle.View.Timers[new(2, dungeon)] == 240,
                    "clear关闭目标并设timer2=240");
                clock.Advance(239);
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Close, "240前仍关闭，旧timer1/3不能使close目标失败");
                clock.Advance(1);
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Open && battle.View.Timers[new(1, dungeon)] == 120 &&
                    battle.View.Timers[new(3, dungeon)] == 120, "240预留自然重开并重设120的新代");
                Clear(battle, dungeon); Clear(battle, 219);
                clock.Advance(240); clock.FireQueued();
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Hide && battle.View.Dungeons[220] == RaidDungeonState.Open &&
                    !battle.View.Deferred.Values.Any(row => row.DungeonId == dungeon), "219隐藏使旧240预留失效，迟到回调不能重开孵化场");
                Clear(battle, 220); Settle(battle, store); Rewards(battle);
                int callbacks = clock.Callbacks; clock.Advance(3600);
                That(clock.Callbacks == callbacks && errors.Count == 0, "Completed回收唯一租约，无孤儿回调");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        void TimeoutRetry()
        {
            var clock = new LukeRaidClockTests.Clock(); var store = new Store { Failures = 1 };
            var battle = Battle(clock, true);
            var service = new LukeRaidClocks(clock, store, (_, _) => { });
            try
            {
                using var lease = service.Reserve(battle)!; clock.Advance(50); lease.Activate();
                clock.AdvanceWithoutCallbacks(2400);
                That(!battle.AllowsAntonEntry(210), "deadline已到即拒绝新入图，不等回调或借revision续期");
                Clear(battle, 210, accepted: false); clock.FireQueued();
                var candidate = battle.PendingAntonSettlement();
                That(candidate is { TimedOut: true, Phase: 0 } && !battle.View.PhaseExpired && battle.HasPendingTransition,
                    "失败SQL忙时封存同一候选，提交前不发布到期");
                clock.Advance(1);
                That(battle.View.PhaseExpired && !battle.HasPendingTransition && ReferenceEquals(candidate, store.Seen[1]) &&
                    store.Seen.Count == 2, "有界退避重试原事件，持久化后才发布失败");
                clock.Advance(600);
                That(store.Seen.Count == 2 && battle.View.State == AntonRaidBattleState.Fighting,
                    "UNIMPLEMENTED：失败无Reward、自动休息或重开，不发送猜测失败状态");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        AntonRaidBattle Battle(TimeProvider clock, bool timeout)
        {
            var battle = new AntonRaidBattle(script, clock: clock, antonPhaseProgress: true,
                antonRewards: new(script, resources.ItemResources.Items),
                phaseTimeout: timeout ? new("fixture/anton-local-seconds", [2400, 2400]) : null);
            That(battle.JoinStart(actor), "计时领域测试绑定当前actor和资源奖励"); return battle;
        }
        void Rewards(AntonRaidBattle battle)
        {
            var view = battle.View; var policy = battle.RewardPolicy(actor, view.Phase)!;
            foreach (var offer in policy.Offers)
                battle.ConfirmReward(actor, new(battle.Instance, view.Phase, offer.Kind, offer.Card, actor.Character,
                    policy.Source, offer.Grants, view.Phase == 1, battle.RewardWeek ?? 0));
            That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null, "领域确认全部牌种后推进，不代表实际资产发放");
        }
    }

    static void Clear(AntonRaidBattle battle, int dungeon, bool accepted = true)
    {
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
        bool bound = battle.BindDungeon(identity, checked((uint)dungeon));
        var result = bound ? battle.CompleteDungeon(new(identity, checked((uint)dungeon), [Guid.NewGuid()])
            { ClearMap = new("boss", new(0, 0), 77, "fixture/anton-timer.map") }) : null;
        That(accepted ? result is not null : !bound && result is null, "领域清图只接受当前开放且未到期目标");
    }

    static void Settle(AntonRaidBattle battle, Store store)
    {
        var candidate = battle.PendingAntonSettlement()!;
        battle.AcceptAntonSettlement(candidate, store.CommitAntonPhase(candidate));
    }

    sealed class Busy : Exception;
    sealed class Store : ILukeRaidLifecycleStore, IAntonRaidLifecycleStore
    {
        internal int Failures;
        internal readonly List<AntonRaidBattle.AntonPhaseSettlement> Seen = [];
        public bool IsRetryable(Exception error) => error is Busy;
        public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate) => throw new InvalidOperationException("不是Luke夹具");
        public ImmutableArray<RaidPhaseEligibility> CommitAntonPhase(AntonRaidBattle.AntonPhaseSettlement candidate)
        {
            Seen.Add(candidate);
            if (Failures-- > 0) throw new Busy();
            return candidate.Members.Select(row => new RaidPhaseEligibility(candidate.Instance, RaidKind.Anton, candidate.Phase,
                candidate.Event, candidate.Anchor.ToUnixTimeSeconds(), row.Character, !candidate.TimedOut && candidate.Phase == 1,
                !candidate.TimedOut, candidate.TimedOut ? "phase-timeout" : "eligible", row.Policy.Source, candidate.Week, row.Policy.Offers)).ToImmutableArray();
        }
    }
}
