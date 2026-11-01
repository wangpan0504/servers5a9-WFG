using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 使用真实Session/owner、原发奖规划及明确合成存储；真实SQL/断线由原recovery TCP覆盖。
internal static class LukeAutomaticRewardTests
{
    internal static void RunCore()
    {
        int before = Count;
        foreach (string mode in new[] { "io", "invalid", "claim", "recovery", "clock", "busy-clock" }) Run(mode);
        Console.WriteLine($"LukeAutomaticRewardTests core: {Count - before} passed");
    }

    static void Run(string mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-luke-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        var owners = new AccountOwners();
        using var owner = owners.Acquire("auto-fixture", () => { });
        var clock = new LukeRaidClockTests.Clock();
        var origin = new Location(32, 4, 10, 10);
        var map = new TownMap("fixture/wait.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
        var towns = new TownCatalog(new() { [(32, 4)] = new(32, 4, 1, [], [], [], origin, map, [], []) },
            origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var repository = new CharacterRepository(Path.Combine(root, "game.db"));
        repository.Create(owner, new("AutoLuke", origin, origin) { Level = 90, CompletedQuests = [2903, 4872] });
        var channel = new DfoServer.GameWorld.Channels.ChannelEntry(new(2, 20), 32, "[raid]");
        var session = new GameSession(repository, towns, owner, entry: channel);
        LukeRaidClocks? timerService = null;
        try
        {
            That(RaidAdmissionPlan.Rejection(channel, repository.LoadCharacter(owner, 0)) is null,
                "原领奖角色具备90级及2903/4872完成资格，不绕过真实Luke选角门禁");
            session.Select(0); session.MarkReady();
            var actor = session.CaptureRaidActor(new RaidRegistry())!;
            That(CurrencyWallet.RewardKind(0) == CurrencyKind.Gold,
                $"{mode}: 字面奖励0明确为金币，不混用复活币1");
            var offers = ImmutableArray.Create(new RaidRewardOffer(1, 0, [new(0, 7)], []),
                new RaidRewardOffer(0, 0, [new(0, 11)], []));
            var policies = Enumerable.Range(0, 2).Select(phase => new RaidPhaseRewardPolicy("fixture/auto",
                offers, new(0, (byte)phase, 0, 0, 0, 0))).ToImmutableArray();
            var completion = new LukeRaidCompletionPolicy("fixture/auto", 2,
                new(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
                policies.Select(policy => new LukePhaseCompletionPolicy(0, 2, policy)).ToImmutableArray(),
                LukeParticipationCutoff.TerminalCommittedFacts);
            var script = AntonRaidBattleTests.Script(10, 20) with { WaitingRoom = new(32, 4), Headers =
                [new("[phase break time]", 1, [new(0, 600, null)]), new("[start delay time]", 2, [new(0, 3, null)]),
                 new("[movie end time]", 3, [new(0, 65, null)])] };
            var battle = new AntonRaidBattle(script, kind: RaidKind.Luke, clock: clock,
                flow: LukeRaidFlowPolicy.Shared95, completion: completion);
            var binding = session.PrepareRaidCombatStart(actor, battle);
            That(binding is not null, "自动领奖夹具绑定原Session和双kind");
            binding!.Install();
            var run = Guid.NewGuid();
            var identity = new DungeonRunIdentity(actor.Character, 0, 1, run, 1);
            using (var reservation = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 10, [(actor, identity)])!)
                reservation.Install();
            battle.CompleteLukeDungeon([(actor, new DungeonClearFact(identity, 10, [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map") })]);
            var pending = battle.PendingLukeSettlement()!;
            That(pending is not null, "真实清图领域路径产生冻结阶段候选");
            var eligibility = new RaidPhaseEligibility(battle.Instance, RaidKind.Luke, 0, pending!.Event,
                pending.Anchor.ToUnixTimeSeconds(), actor.Character, false, true, "fixture", policies[0].Source,
                pending.Week, offers);
            battle.AcceptLukeSettlement(pending, new([eligibility]));
            battle.ReleaseDungeon(actor, run);
            That(battle.View.State == AntonRaidBattleState.Reward, "清图及原Run退出后开放领奖");
            Reject<InvalidOperationException>(() => battle.ConfirmAutomaticLukeReward(eligibility with { Phase = 1 }, 1),
                "错误阶段确认仍硬拒绝，不生成已领标记");
            That(!battle.HasConfirmedLukeReward(actor.Character, 0, 1), "非法确认不替代资产义务");
            var original = session.Selected!;
            var online = new OnlineSessionDirectory();
            online.Register(owner, session);
            var store = new Store(eligibility);
            var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
            var gold = new GoldLimitCatalog("[gold limit from level]\n0 1000000\n[/gold limit from level]");
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var automatic = new LukeAutomaticRewards(store, owners, online,
                (_, id) => id == original.Id ? store.Latest ?? original : null, items, gold, pieces, clock);
            Exception publication = mode == "invalid" ? new InvalidOperationException("fixture projection") : new IOException("fixture transport");
            var business = new InvalidDataException("fixture claim");
            int publications = 0, oldWindow = 0;
            session.AutomaticRaidRewardArrived = _ =>
            {
                oldWindow++;
                if (++publications == 1) throw publication;
                return Task.CompletedTask;
            };
            if (mode == "recovery")
            {
                session.ReturnToRoster();
                repository.Create(owner, new("AutoOther", origin, origin) { Level = 90, CompletedQuests = [2903, 4872] });
                That(RaidAdmissionPlan.Rejection(channel, repository.LoadCharacter(owner, 1)) is null,
                    "切换角色也具备完整Luke资格，换角回归不在准入阶段误失败");
                session.Select(1); session.MarkReady();
                session.AutomaticLukeRecoveryArrived = _ =>
                {
                    if (++publications == 1) throw publication;
                    return Task.CompletedTask;
                };
            }
            if (mode == "claim") store.BeforeCommit = prepared => { if (prepared.Kind == 0) throw business; };
            // 首次读取建立原185秒截止锚点，再推进真实TimeProvider，不直接设置phase。
            That(battle.DueLukeRewards().Length == 0, "截止前没有自动资格");
            var errors = new List<(string Stage, Exception Error)>();
            int finalNotices = 0;
            session.RaidBattleNoticeArrived = (view, _) =>
            {
                if (view.State == AntonRaidBattleState.Intermission) finalNotices++;
            };
            timerService = new LukeRaidClocks(clock, new UnusedStore(),
                (stage, error) => errors.Add((stage, error)), settleLukeRewards: automatic.SettleAsync);
            bool clockMode = mode is "clock" or "busy-clock";
            if (clockMode) timerService.Reserve(battle)!.Activate();
            if (mode == "busy-clock")
            {
                That(session.CommandGate.Wait(0), "busy-clock: 夹具真实取得命令门禁");
                try
                {
                    clock.Advance(186);
                    That(store.Commits.Count == 0 && publications == 0 && errors.Count == 0 &&
                        !battle.HasConfirmedLukeReward(actor.Character, 0, 1) &&
                        !battle.HasConfirmedLukeReward(actor.Character, 0, 0) && battle.View.State == AntonRaidBattleState.Reward,
                        "busy-clock: 门禁忙时不提交、不投影、不确认且保持Reward");
                    That(clock.PendingDeadlines.Count(row => row.Instance == battle.Instance && row.Due == clock.GetTimestamp() + 1000) == 1,
                        "busy-clock: 原Luke timer已登记一秒重排，不靠手动Settle恢复");
                }
                finally { session.CommandGate.Release(); }
                clock.Advance(1);
            }
            else clock.Advance(186);
            Exception? observed = null;
            if (!clockMode)
                try { automatic.SettleAsync(battle).GetAwaiter().GetResult(); }
                catch (Exception error) { observed = error; }
            if (mode == "claim")
            {
                That(ReferenceEquals(observed, business) &&
                    observed.Data["LukeRewardPublicationFailures"] is AggregateException saved &&
                    saved.InnerExceptions.Count == 1 && ReferenceEquals(saved.InnerExceptions[0], publication),
                    "后续资产失败保留原异常和先前展示错误，不误报展示专用成功");
                That(store.Commits.Count == 1 && battle.HasConfirmedLukeReward(actor.Character, 0, 1) &&
                    !battle.HasConfirmedLukeReward(actor.Character, 0, 0) && battle.View.State == AntonRaidBattleState.Reward,
                    "失败kind不确认，不跳过剩余资产义务");
                store.BeforeCommit = null;
                automatic.SettleAsync(battle).GetAwaiter().GetResult();
            }
            else if (clockMode)
                That(errors is [("luke-auto-reward-publish", LukeRewardPublicationException)] && finalNotices > 0,
                    "时钟报告专用展示错误后仍发布最终休息战况，不提前return");
            else
                That(observed is LukeRewardPublicationException failed && failed.InnerExceptions.Count == 1 &&
                    ReferenceEquals(failed.InnerExceptions[0], publication), "仅展示错误延后保留原异常，不吞编程错误");
            That(store.Commits.Select(row => row.Kind).SequenceEqual(new byte[] { 1, 0 }),
                $"{mode}: 两类回执顺序必须1,0，实际{string.Join(",", store.Commits.Select(row => row.Kind))}");
            That(store.Commits[0].Grants.SequenceEqual(new[] { new InventoryReward(0, 7) }) &&
                store.Commits[1].Grants.SequenceEqual(new[] { new InventoryReward(0, 11) }),
                $"{mode}: 各kind完整Grants分别为字面金币7/11，不能互换或修改属性");
            That(store.Latest is not null, $"{mode}: 两类结算产生实际角色快照");
            That(store.Latest!.Inventory.Gold == original.Inventory.Gold + 18,
                $"{mode}: 字面金币总额必须{original.Inventory.Gold + 18}，实际{store.Latest.Inventory.Gold}");
            That(CurrencyWallet.Get(store.Latest.Inventory, CurrencyKind.ReviveCoin) ==
                CurrencyWallet.Get(original.Inventory, CurrencyKind.ReviveCoin),
                $"{mode}: 金币奖励不能错误增加复活币");
            That(battle.HasConfirmedLukeReward(actor.Character, 0, 1), $"{mode}: 首kind已提交权益必须确认");
            That(battle.HasConfirmedLukeReward(actor.Character, 0, 0), $"{mode}: 第二kind已提交权益必须确认");
            That(battle.View.State == AntonRaidBattleState.Intermission,
                $"{mode}: 两类确认后必须进入Intermission，实际{battle.View.State}/phase{battle.View.Phase}");
            if (mode == "recovery")
                That(session.Selected!.Id != actor.Character && session.Selected.Inventory.Gold == 0 && oldWindow == 0,
                    "换角领取只更新原角色资产，不向新角色投旧团奖励窗口");
            if (clockMode) clock.Advance(1);
            else automatic.SettleAsync(battle).GetAwaiter().GetResult();
            That(store.Commits.Count == 2 && publications == 2, "重复settle不重发资产或重复投影");
            passed = true;
        }
        finally
        {
            timerService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            session.End();
            if (passed) TestFiles.DeleteDirectory(root, true);
        }
    }

    sealed class Store(RaidPhaseEligibility eligibility) : ILukeRewardRecoveryStore
    {
        internal readonly List<PreparedRaidReward> Commits = [];
        internal Character? Latest;
        internal Action<PreparedRaidReward>? BeforeCommit;
        public RaidRewardCommit? CommitRaidReward(AccountOwners.Lease lease, Character expected,
            PreparedRaidReward prepared, InventoryPlan inventory, DateTimeOffset now) => lease.Execute(() =>
        {
            BeforeCommit?.Invoke(prepared);
            That(prepared.Instance == eligibility.Instance && prepared.Character == eligibility.Character &&
                prepared.Phase == eligibility.Phase, "合成writer仍核冻结角色与阶段身份");
            Commits.Add(prepared);
            Latest = expected with { Inventory = inventory.After, Revision = expected.Revision + 1 };
            return new RaidRewardCommit(Latest, false, []);
        });
        public LukeRewardRecoverySnapshot ReadLukeRewardRecovery(AccountOwners.Lease lease, Character character,
            LukeRewardRecoveryTarget target) => lease.Execute(() =>
        {
            var offer = eligibility.Offers.Single(row => row.Kind == target.Kind && row.Card == target.Card);
            return new LukeRewardRecoverySnapshot(new(eligibility.Instance, eligibility.Phase, offer.Kind, offer.Card,
                character.Id, eligibility.Source, offer.Grants, false, eligibility.Week) { RaidType = RaidKind.Luke },
                eligibility.Event, eligibility.Anchor, Commits.Any(row => row.Kind == target.Kind), []);
        });
        public LukeRewardRecoveryPage ListLukeRewardRecovery(AccountOwners.Lease lease, Character character,
            Guid? afterInstance = null, int limit = 16) => throw new NotSupportedException();
    }

    sealed class UnusedStore : ILukeRaidLifecycleStore
    {
        public bool IsRetryable(Exception error) => false;
        public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate) => throw new NotSupportedException();
    }

}
