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
    sealed class HatcheryRandom(int target) : Random
    {
        int calls;
        public override int Next(int maxValue)
        {
            int omitted = target == 224 ? 0 : 3;
            return calls++ == 0 ? omitted : Array.IndexOf(new[] { 221, 222, 223, 224 }.Where((_, i) => i != omitted).ToArray(), target);
        }
    }

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
        That(script.Phases[0].Dungeons.All(row => row.PartyMax == (row.Id == 216 ? 1 : 5)),
            "匹配PVF的PARTY MAX原值216=1、其它P1图=5，不解释并发语义");
        foreach (int dungeon in new[] { 221, 222, 223, 224 }) Hatchery(dungeon);
        Energy();
        PhaseReset();
        foreach (int dungeon in new[] { 212, 214 }) TimedPhaseReset(dungeon);
        PendingHatcheryAfterVolcano();
        TimeoutRetry();
        IntermissionPreparation();
        RewardWaitDetached();

        void Energy()
        {
            var clock = new LukeRaidClockTests.Clock(); var store = new Store();
            var battle = Battle(clock, false);
            var service = new LukeRaidClocks(clock, store, (_, _) => { });
            try
            {
                using var lease = service.Reserve(battle)!; lease.Activate();
                Clear(battle, actor, 210); Clear(battle, actor, 213); Clear(battle, actor, 215);
                Settle(battle, store); Rewards(battle, clock);
                for (int index = 0; index < 4; index++)
                {
                    var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
                    using var reservation = battle.ReserveDungeon(new(battle.Instance, 1, 1), 218, [(actor, identity)])!;
                    reservation.Install();
                    var map = new DfoServer.GameWorld.Dungeons.DungeonMap("boss", new(0, 0), 77, "fixture/energy.map");
                    var snapshot = new DungeonRunSnapshot(identity, SinglePlayerDungeonPhase.Settlement, map, []);
                    That(!battle.ConfirmAntonEnergy(actor, snapshot), "未清图报告不能扣屏障");
                    That(battle.CompleteAntonReservedDungeon(new(identity, 218, [Guid.NewGuid()]) { ClearMap = map }) is not null,
                        "能量测试沿正式Run清图");
                    That(!battle.ConfirmAntonEnergy(actor, snapshot with { Identity = identity with { Epoch = 2 } }), "旧清图事实不能确认其它房间代");
                    That(battle.ConfirmAntonEnergy(actor, snapshot) && battle.ConfirmAntonEnergy(actor, snapshot) &&
                        battle.View.Symbols["[black volcano barrier]"] == Math.Max(0, 10000 - 3000 * (index + 1)),
                        "每个可信Run只接受一次Last，钳制0且不从117重复扣能量");
                    battle.ReleaseDungeon(actor, identity.InstanceId);
                    That(!battle.ConfirmAntonEnergy(actor, snapshot), "已释放Run不能再次报告");
                }
                That(battle.View.Symbols["[movie barrier break]"] == 1, "归零执行PVF破壁规则");
                clock.Advance(1);
                That(battle.View.Symbols["[black volcano barrier]"] == 17, "首次归零后沿86每秒恢复17");
                clock.Advance(179);
                int previous = battle.View.Symbols["[black volcano barrier]"];
                clock.Advance(1);
                That(battle.View.Symbols["[black volcano barrier]"] == previous + 34, "感染场开放后恢复34");
                for (int tick = 0; tick < 600; tick++) clock.Advance(1);
                That(battle.View.Symbols["[black volcano barrier]"] == 10000 &&
                    battle.View.Symbols["[movie barrier recover]"] == 1, "充电钳制满值并执行PVF恢复规则");
                Clear(battle, actor, 219);
                int stopped = battle.View.Symbols["[black volcano barrier]"];
                clock.Advance(240); clock.FireQueued();
                That(battle.View.Symbols["[black volcano barrier]"] == stopped && battle.View.Dungeons[220] == RaidDungeonState.Open,
                    "火山通关取消充电，迟到回调不能污染心脏阶段");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        void RewardWaitDetached()
        {
            var clock = new LukeRaidClockTests.Clock(); var store = new Store();
            var battle = Battle(clock, false);
            var service = new LukeRaidClocks(clock, store, (_, error) => throw error);
            try
            {
                using var lease = service.Reserve(battle)!; lease.Activate();
                Clear(battle, actor, 210); Clear(battle, actor, 213); Clear(battle, actor, 215);
                Settle(battle, store);
                clock.Advance(10);
                var settled = battle.View;
                That(settled.AntonCardsReady, "2秒演出事件与20秒截止分别处理");
                // 同候选重放不得续期；最后一人离团后仍保留原Reward兜底时钟。
                SettleReplay();
                That(battle.DetachStart(actor), "结算中移除最后成员");
                clock.Advance(9);
                That(battle.View.State == AntonRaidBattleState.Reward, "离团不能提前放弃全部奖励");
                clock.Advance(1);
                That(battle.View is { State: AntonRaidBattleState.Intermission, Phase: 0, AntonWaitSeconds: 595 },
                    "最后成员离团或断线也能到期推进，不能永久卡Reward");
                That(battle.AntonEligibility(actor.Character, 0)!.Eligible,
                    "放弃不篡改已持久化资格或伪装实际发奖");
                var advanced = battle.View;
                clock.FireQueued(); clock.Advance(1);
                That(ReferenceEquals(advanced, battle.View), "空团推进后回收时钟，旧回调不得重复推进");

                void SettleReplay()
                {
                    var candidate = store.Seen.Single();
                    battle.AcceptAntonSettlement(candidate, store.CommitAntonPhase(candidate));
                    That(ReferenceEquals(settled, battle.View), "原候选重复回执不改变结算视图");
                }
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        void IntermissionPreparation()
        {
            var clock = new LukeRaidClockTests.Clock(); var store = new Store();
            var battle = Battle(clock, false);
            var service = new LukeRaidClocks(clock, store, (_, error) => throw error);
            try
            {
                using var lease = service.Reserve(battle)!; lease.Activate();
                Clear(battle, actor, 210); Clear(battle, actor, 213);
                // 保留已安装原擎天Run，核对86不强制退场及95旧Run隔离。
                var heldRun = Clear(battle, actor, 215, releaseLast: false); Settle(battle, store);
                var reward = battle.View; var policy = battle.RewardPolicy(actor, 0)!;
                foreach (var offer in policy.Offers)
                    battle.ConfirmReward(actor, new(battle.Instance, 0, offer.Kind, offer.Card, actor.Character,
                        policy.Source, offer.Grants, false, battle.RewardWeek ?? 0));
                battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision);
                That(battle.PrepareAntonNextPhase(actor), "休息可由当前成员进入已授权团长准备入口");
                var preparing = battle.View;
                That(!battle.PrepareAntonNextPhase(actor with { Epoch = actor.Epoch + 1 }) && ReferenceEquals(preparing, battle.View), "旧选角epoch无权提前开始或续期");
                That(!battle.PrepareAntonNextPhase(actor) && ReferenceEquals(preparing, battle.View), "重复开始不续准备deadline");
                clock.Advance(3);
                That(battle.View is { Phase: 1, State: AntonRaidBattleState.Fighting }, "86不强制旧Run退场，准备完成仍可开启P2");
                var started = battle.View;
                var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
                using var blocked = battle.ReserveDungeon(new(battle.Instance, 1, 1), 218, [(actor, identity)]);
                That(blocked is null, "本人仍在旧Run时不能并行安装新Run");
                var released = battle.ReleaseDungeon(actor, heldRun);
                var afterRelease = battle.View;
                That(released is not null && ReferenceEquals(released.Before, started) && ReferenceEquals(released.After, afterRelease) &&
                    afterRelease.Revision == checked(started.Revision + 1) && afterRelease == (started with { Revision = afterRelease.Revision }),
                    "旧Run合法退场仅递增一次revision，保留完整P2状态、计时锚点与目标代");
                clock.FireQueued();
                That(ReferenceEquals(afterRelease, battle.View), "旧Run退场和旧回调不重置P2或释放新占用");
                Clear(battle, actor, 218);
                That(battle.View.Phase == 1 && battle.View.Symbols["[218 dgn hp]"] == 0, "旧Run正常退出后保留P2合法进图能力");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        void Hatchery(int expectedDungeon)
        {
            var clock = new LukeRaidClockTests.Clock(); var store = new Store();
            var battle = Battle(clock, false, expectedDungeon);
            var errors = new List<string>();
            var service = new LukeRaidClocks(clock, store, (stage, _) => errors.Add(stage));
            try
            {
                using var lease = service.Reserve(battle)!; lease.Activate();
                Clear(battle, actor, 210); Clear(battle, actor, 213); Clear(battle, actor, 215); Settle(battle, store); Rewards(battle, clock);
                clock.Advance(179);
                That(!battle.View.Deferred.Values.Any(row => row.Tag == "[open random hatchery]"), "180前不能预开随机目标");
                clock.Advance(1);
                var opened = new[] { 221, 222, 223, 224 }.Where(id => battle.View.Dungeons[id] == RaidDungeonState.Open).ToArray();
                int dungeon = battle.View.Symbols["[infection dungeon index]"];
                That(dungeon == expectedDungeon && opened.Length == 3 && opened.Contains(dungeon) && battle.View.Symbols["[is exist infection dungeon]"] == 1 &&
                    !battle.View.Deferred.Values.Any(row => row.Tag == "[open random hatchery]"),
                    "86移植：180自然四选三，感染从已开三图选一，一次消费待决");
                That(opened.All(id => battle.View.Timers[new(1, id)] == 120 && battle.View.Timers[new(3, id)] == 120),
                    "三张随机目标各有独立120计时");
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
                Clear(battle, actor, dungeon);
                That(battle.View.Symbols["[is exist infection dungeon]"] == 0 && battle.View.Symbols["[infection dungeon index]"] == dungeon,
                    "清感染场关闭感染效果，保留原身份供240重开");
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Close && battle.View.Timers[new(2, dungeon)] == 240,
                    "clear关闭目标并设timer2=240");
                clock.Advance(239);
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Close, "240前仍关闭，旧timer1/3不能使close目标失败");
                clock.Advance(1);
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Open && battle.View.Timers[new(1, dungeon)] == 120 &&
                    battle.View.Timers[new(3, dungeon)] == 120, "240预留自然重开并重设120的新代");
                That(battle.View.Symbols["[is exist infection dungeon]"] == 1 && battle.View.Symbols["[infection dungeon index]"] == dungeon,
                    "240重开恢复同一感染身份，不重新抽选");
                int normal = opened.First(id => id != dungeon);
                Clear(battle, actor, normal);
                That(battle.View.Symbols["[is exist infection dungeon]"] == 1, "普通孵化场关闭不清感染效果");
                Clear(battle, actor, dungeon); Clear(battle, actor, 219);
                clock.Advance(240); clock.FireQueued();
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Hide && battle.View.Dungeons[220] == RaidDungeonState.Open &&
                    !battle.View.Deferred.Values.Any(row => row.DungeonId == dungeon), "219隐藏使旧240预留失效，迟到回调不能重开孵化场");
                Clear(battle, actor, 220); Settle(battle, store); Rewards(battle, clock);
                int callbacks = clock.Callbacks; clock.Advance(3600);
                That(clock.Callbacks == callbacks && errors.Count == 0, "Completed回收唯一租约，无孤儿回调");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        void PhaseReset()
        {
            var battle = Battle(new LukeRaidClockTests.Clock(), false);
            Clear(battle, actor, 210); Clear(battle, actor, 213);
            int token = battle.View.Deferred.Single(row => row.Value.Tag == "[reserve dungeon state]" && row.Value.DungeonId == 216).Key;
            That(battle.SetMechanism(battle.Instance, battle.View.Revision, "[1phase fail]", 1) is not null,
                "显式领域失败符号消费真实重置规则，不伪装timer原厂单位已证");
            That(battle.View.Dungeons[210] == RaidDungeonState.Open && battle.View.Symbols["[210 dgn hp]"] == 4 &&
                new[] { 211, 212, 213, 214, 215, 216 }.All(id => battle.View.Dungeons[id] == RaidDungeonState.Hide) &&
                battle.View.Symbols["[dgn smoke open]"] == 0 && battle.View.Symbols["[1phase fail]"] == 0 &&
                battle.View.Symbols["[kill all character]"] == 1 && battle.View.Timers.IsEmpty,
                "P1失败重开HP4黑雾并取消旧图计时；强杀仅是符号，不冒充角色死亡");
            That(battle.CompleteDeferred(battle.Instance, battle.View.Revision, token) is null,
                "旧舰炮预留不能越过P1重置提前开图");
            Clear(battle, actor, 210);
            That(battle.View.Symbols["[213 dgn hp]"] == 2 && battle.View.Symbols["[215 dgn hp]"] == 2 &&
                battle.View.Dungeons[213] == RaidDungeonState.Open && battle.View.Dungeons[215] == RaidDungeonState.Open,
                "重新四次黑雾后恢复两条腿的声明HP，不能继承上轮已清腿");
        }

        // 证据：etc/raid/anton.etc 的 OPEN→timer1=300、timer1到期→1PHASE FAIL；
        // 现场2026-10-29 21:03:22→21:08:22对应300秒。隔离两张震颤，避免只证明同时到期。
        void TimedPhaseReset(int failingDungeon)
        {
            var clock = new LukeRaidClockTests.Clock(); var store = new Store();
            var battle = Battle(clock, false);
            var errors = new List<string>();
            var service = new LukeRaidClocks(clock, store, (stage, _) => errors.Add(stage));
            try
            {
                using var lease = service.Reserve(battle)!; lease.Activate();
                // 开战与黑雾归零不是同一起点；尚未开放震颤时不能启动其攻略计时。
                clock.Advance(37);
                That(battle.View.Timers.IsEmpty, "黑雾未归零时不启动震颤300或211重开300");
                Clear(battle, actor, 210);
                That(battle.View.Timers[new(1, failingDungeon)] == 300 &&
                    battle.View.Timers[new(2, 211)] == 300 && !battle.View.Timers.ContainsKey(new(1, 211)),
                    "210归零同时开始震颤攻略300与211重开300，尚未开始211攻略480");
                Clear(battle, actor, failingDungeon == 212 ? 214 : 212);
                var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
                using var run = battle.ReserveDungeon(new(battle.Instance, 0, 1), 215, [(actor, identity)]);
                That(run is not null, "失败前保留图内擎天Run以核对迟到清图"); run!.Install();
                var fact = new DungeonClearFact(identity, 215, [Guid.NewGuid()])
                    { ClearMap = new("boss", new(0, 0), 77, "fixture/anton-timer.map") };
                clock.Advance(299);
                That(battle.View.Dungeons[210] == RaidDungeonState.Hide &&
                    battle.View.Dungeons[failingDungeon] == RaidDungeonState.Open &&
                    battle.View.Symbols["[kill all character]"] == 0, "299秒前不得提前重置或签发强杀符号");
                clock.Advance(1);
                var reset = battle.View;
                That(reset.Dungeons[210] == RaidDungeonState.Open && reset.Symbols["[210 dgn hp]"] == 4 &&
                    new[] { 211, 212, 213, 214, 215, 216 }.All(id => reset.Dungeons[id] == RaidDungeonState.Hide) &&
                    reset.Symbols["[kill all character]"] == 1 && reset.Symbols["[1phase fail]"] == 0 &&
                    reset.Timers.IsEmpty && reset.Deferred.IsEmpty && store.Seen.Count == 0,
                    "单张震颤300自然到期重置P1，取消211重开及旧预留，不生成阶段奖励或失败结算");
                That(battle.CompleteAntonReservedDungeon(fact) is null && ReferenceEquals(reset, battle.View),
                    "重置后仍在旧擎天内的迟到117事实不能扣HP或推进阶段");
                battle.ReleaseDungeon(actor, identity.InstanceId);
                Clear(battle, actor, 210);
                var reopened = battle.View;
                That(reopened.Symbols["[215 dgn hp]"] == 2 &&
                    battle.CompleteAntonReservedDungeon(fact) is null && ReferenceEquals(reopened, battle.View),
                    "重新开腿后旧Run仍不得消费新目标代HP");
                That(errors.Count == 0, "自然重置没有时钟错误；强杀符号不等同客户端死亡验收");
            }
            finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        void PendingHatcheryAfterVolcano()
        {
            var clock = new LukeRaidClockTests.Clock(); var store = new Store();
            var battle = Battle(clock, false);
            var service = new LukeRaidClocks(clock, store, (_, _) => { });
            try
            {
                using var lease = service.Reserve(battle)!; lease.Activate();
                Clear(battle, actor, 210); Clear(battle, actor, 213); Clear(battle, actor, 215); Settle(battle, store); Rewards(battle, clock);
                clock.Advance(179);
                Clear(battle, actor, 219);
                var heart = battle.View;
                That(!heart.Deferred.Values.Any(row => row.Tag == "[open random hatchery]") && !heart.Timers.ContainsKey(new(3, 219)),
                    "火山先清撤销180开场计时，不留下随机待决");
                clock.Advance(240); clock.FireQueued();
                That(ReferenceEquals(heart, battle.View) && heart.Dungeons[220] == RaidDungeonState.Open &&
                    new[] { 218, 219, 221, 222, 223, 224 }.All(id => heart.Dungeons[id] == RaidDungeonState.Hide),
                    "火山关闭链优先于迟到机制输入与回调，心脏HP不被修改");
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
                Clear(battle, actor, 210, accepted: false); clock.FireQueued();
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

        AntonRaidBattle Battle(TimeProvider clock, bool timeout, int? hatchery = null)
        {
            var battle = new AntonRaidBattle(script, clock: clock, antonPhaseProgress: true, antonHatcheryRandom: hatchery is { } id ? new HatcheryRandom(id) : null,
                antonRewards: new(script, resources.ItemResources.Items, resources.ItemResources.Boosters),
                phaseTimeout: timeout ? new("fixture/anton-local-seconds", [2400, 2400]) : null);
            That(battle.JoinStart(actor), "计时领域测试绑定当前actor和资源奖励"); return battle;
        }
        void Rewards(AntonRaidBattle battle, LukeRaidClockTests.Clock clock)
        {
            var view = battle.View; var policy = battle.RewardPolicy(actor, view.Phase)!;
            foreach (var offer in policy.Offers)
                battle.ConfirmReward(actor, new(battle.Instance, view.Phase, offer.Kind, offer.Card, actor.Character,
                    policy.Source, offer.Grants, view.Phase == 1, battle.RewardWeek ?? 0));
            That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null, "领域确认全部牌种后推进，不代表实际资产发放");
            if (view.Phase == 0)
            {
                That(battle.View is { State: AntonRaidBattleState.Intermission, Phase: 0, AntonWaitSeconds: 595 }, "86移植：领奖齐进入595秒休息");
                clock.Advance(594);
                That(battle.View.State == AntonRaidBattleState.Intermission && !battle.View.AntonPreparing, "休息到期前不进入准备");
                clock.Advance(1);
                That(battle.View.AntonPreparing && battle.View.Phase == 0, "休息自然到期仍先准备，不直接进入P2");
                clock.Advance(3); clock.FireQueued();
                That(battle.View is { Phase: 1, State: AntonRaidBattleState.Fighting }, "准备3秒后初始化P2；迟到回调不能重置新阶段");
            }
        }
    }

    // 真实规则计时回归也沿正式共享Run扣HP，不再借旧BindDungeon夹具跳过门槛。
    static Guid Clear(AntonRaidBattle battle, RaidActor actor, int dungeon, bool accepted = true, bool releaseLast = true)
    {
        var initial = battle.View;
        Guid lastRun = Guid.Empty;
        int count = Math.Max(1, initial.Symbols[$"[{dungeon} dgn hp]"]);
        for (int index = 0; index < (accepted ? count : 1); index++)
        {
            var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
            using var reservation = battle.ReserveDungeon(new(battle.Instance, battle.View.Phase, 1), checked((uint)dungeon), [(actor, identity)]);
            if (!accepted)
            {
                That(reservation is null && ReferenceEquals(initial, battle.View), "到期目标不预留正式Run或改变HP");
                return Guid.Empty;
            }
            That(reservation is not null, "真实资源目标允许正式Run预留");
            reservation!.Install();
            var fact = new DungeonClearFact(identity, checked((uint)dungeon), [Guid.NewGuid()])
                { ClearMap = new("boss", new(0, 0), 77, "fixture/anton-timer.map") };
            That(battle.CompleteAntonReservedDungeon(fact) is not null, "正式Run消费一次真实资源HP");
            if (index + 1 < count)
                That(battle.View.Dungeons[dungeon] == RaidDungeonState.Open &&
                    battle.View.Symbols[$"[{dungeon} dgn hp]"] == count - index - 1,
                    "真实多次门槛未归零前保持Open，不触发后续状态");
            lastRun = identity.InstanceId;
            if (releaseLast || index + 1 < count) battle.ReleaseDungeon(actor, identity.InstanceId);
        }
        return lastRun;
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
