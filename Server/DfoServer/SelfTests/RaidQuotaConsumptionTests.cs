using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 只验证真实开战/阶段资格事务的GM刷新闭环；终局合同合成，不代表完整战斗和客户端验收。
internal static class RaidQuotaConsumptionTests
{
    internal static void Run(TcpTestResources resources, string root)
    {
        int before = Count;
        var current = DateTimeOffset.UtcNow;
        int week = FiendwarRewards.Cycle.Key(current);
        var firstDay = DateTimeOffset.ParseExact(week.ToString(), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None).Date;
        var at = new DateTimeOffset(firstDay.AddHours(7), TimeSpan.FromHours(8));
        SharedEpochs(Path.Combine(root, "quota-shared-phases"), resources, at);
        GuideSlots(Path.Combine(root, "quota-guide-slots"), resources, at);
        AccountAdmissions(Path.Combine(root, "quota-account-admissions"), resources, at);
        Console.WriteLine($"PASS {Count - before} 漩涡刷新后真实入场与阶段资格断言");
    }

    static void SharedEpochs(string root, TcpTestResources resources, DateTimeOffset at)
    {
        using var f = new Fixture(root, resources, at);
        var actor = f.Add("quota-phases", "QuotaPhases");
        var first = f.Clear(f.Admit(actor, false));
        That(first is { CountsAsClear: true, Eligible: true, RaidType: RaidKind.Fiendwar, Phase: 0 },
            "普通模式真实提交首份共享通关和阶段资格");
        f.Rejected(actor, true, "fiendwar-daily-character-clear-limit");
        f.MoveTo(at.AddDays(1));
        var second = f.Clear(f.Admit(actor, true));
        That(second is { CountsAsClear: true, Eligible: true } && second.Week == first.Week,
            "次日引导模式真实耗满同角色共享周两次通关与奖励额度");
        f.Rejected(actor, false, "fiendwar-weekly-clear-limit");
        f.Rejected(actor, true, "fiendwar-weekly-clear-limit");

        string oldRuns = f.Rows("SELECT * FROM character_raid_runs ORDER BY rowid");
        string oldBindings = f.Rows("SELECT * FROM account_fiendwar_guides ORDER BY rowid");
        var entry = f.Repository.ResetDungeonEntries(actor.Owner);
        That(entry.RaidEpochsAdvanced == 1 && f.Scalar("SELECT entry_epoch FROM account_raid_quota_epochs WHERE raid_type=2") == 1 &&
            f.Scalar("SELECT reward_epoch FROM account_raid_quota_epochs WHERE raid_type=2") == 0,
            "公开入场刷新只推进漩涡入场轴，不恢复奖励轴");
        That(f.Rows("SELECT * FROM character_raid_runs ORDER BY rowid") == oldRuns &&
            f.Rows("SELECT * FROM account_fiendwar_guides ORDER BY rowid") == oldBindings,
            "入场刷新保持旧通关时间、周、两轴、阶段JSON与引导绑定原文");
        var noReward = f.Clear(f.Admit(actor, false));
        That(noReward is { CountsAsClear: true, Eligible: false, Reason: "phase-eligibility-limit" } &&
            f.Scalar("SELECT count(*) FROM character_raid_runs WHERE entry_epoch=1 AND reward_epoch=0 AND state=2") == 1,
            "同日新入场真实通关只消费新入场世代，旧奖励轴仍拒绝阶段资格");

        string settled = f.Rows("SELECT * FROM character_raid_runs ORDER BY rowid");
        var reward = f.Repository.ResetRewardLimits(actor.Owner);
        That(reward.RaidEpochsAdvanced == 1 && f.Scalar("SELECT entry_epoch FROM account_raid_quota_epochs WHERE raid_type=2") == 1 &&
            f.Scalar("SELECT reward_epoch FROM account_raid_quota_epochs WHERE raid_type=2") == 1 &&
            f.Rows("SELECT * FROM character_raid_runs ORDER BY rowid") == settled,
            "公开奖励刷新只推进奖励轴并完整保留已拒资格JSON与历史时间");
        f.Rejected(actor, false, "fiendwar-daily-character-clear-limit");
        f.Rejected(actor, true, "fiendwar-daily-character-clear-limit");
        f.Repository.ResetDungeonEntries(actor.Owner);
        var restored = f.Clear(f.Admit(actor, true));
        That(restored is { CountsAsClear: true, Eligible: true } && restored.Week == first.Week &&
            f.Scalar("SELECT count(*) FROM character_raid_runs WHERE entry_epoch=2 AND reward_epoch=1 AND state=2") == 1,
            "两按钮分别刷新后同日真实引导入场恢复阶段资格，冻结新双轴");
        That(f.Rows("SELECT * FROM character_raid_runs WHERE entry_epoch=0 ORDER BY rowid") == oldRuns &&
            f.Rows("SELECT * FROM character_raid_runs WHERE entry_epoch<2 ORDER BY rowid") == settled &&
            f.Rows("SELECT * FROM account_fiendwar_guides WHERE entry_epoch=0 ORDER BY rowid") == oldBindings,
            "完整闭环结束仍保留全部旧run、原阶段JSON、锚点、周与世代及旧Guide绑定");
    }

    static void GuideSlots(string root, TcpTestResources resources, DateTimeOffset at)
    {
        using var f = new Fixture(root, resources, at);
        var first = f.Add("quota-guides", "QuotaGuideA");
        var second = f.Add("quota-guides", "QuotaGuideB", 1);
        var third = f.Add("quota-guides", "QuotaGuideC", 2);
        f.Admit(first, true);
        f.Admit(second, true);
        f.Rejected(third, true, "fiendwar-guide-account-weekly-bind-limit");
        string bindings = f.Rows("SELECT * FROM account_fiendwar_guides ORDER BY rowid");
        string runs = f.Rows("SELECT * FROM character_raid_runs ORDER BY rowid");
        var reset = f.Repository.ResetDungeonEntries(first.Owner);
        That(reset.RaidEpochsAdvanced == 1 && reset.RaidGuideBindingsArchived == 2 &&
            f.Rows("SELECT * FROM account_fiendwar_guides ORDER BY rowid") == bindings &&
            f.Rows("SELECT * FROM character_raid_runs ORDER BY rowid") == runs,
            "Guide两槽真实耗满后刷新仅归档额度，原两行绑定与入场身份不改");
        f.Admit(third, true);
        f.Admit(first, true);
        That(f.Scalar("SELECT count(*) FROM account_fiendwar_guides") == 4 &&
            f.Scalar("SELECT count(*) FROM account_fiendwar_guides WHERE entry_epoch=1") == 2 &&
            f.Rows("SELECT * FROM account_fiendwar_guides WHERE entry_epoch=0 ORDER BY rowid") == bindings,
            "新世代两槽均由真实Guide开战消费，旧两槽原文保留并共存四行");
        f.Rejected(second, true, "fiendwar-guide-account-weekly-bind-limit");
    }

    static void AccountAdmissions(string root, TcpTestResources resources, DateTimeOffset at)
    {
        using var f = new Fixture(root, resources, at);
        var actor = f.Add("quota-entries", "QuotaEntries");
        for (int index = 0; index < 10; index++)
            f.Admit(actor, (index & 1) != 0);
        That(f.Scalar("SELECT count(*) FROM character_raid_runs WHERE entry_epoch=0") == 10 &&
            f.Scalar("SELECT count(*) FROM character_raid_runs WHERE state=1") == 9 &&
            f.Scalar("SELECT count(*) FROM character_raid_runs WHERE state=2") == 0,
            "普通和Guide混合十次真实扣票入场耗满账号日额度，未伪造通关");
        f.Rejected(actor, false, "fiendwar-daily-account-enter-limit");
        f.Rejected(actor, true, "fiendwar-daily-account-enter-limit");
        string oldRuns = f.Rows("SELECT * FROM character_raid_runs ORDER BY rowid");
        string oldPayments = f.Rows("SELECT * FROM character_fiendwar_recovery ORDER BY rowid");
        var reset = f.Repository.ResetDungeonEntries(actor.Owner);
        That(reset.RaidEpochsAdvanced == 1 && f.Rows("SELECT * FROM character_raid_runs ORDER BY rowid") == oldRuns &&
            f.Rows("SELECT * FROM character_fiendwar_recovery ORDER BY rowid") == oldPayments,
            "账号日满额刷新不改任何旧入场时间、状态、两轴与退款合同");
        // 后续同角色开战会正常把旧在途实例置为放弃；只冻结此前已放弃九行作持久历史比较。
        string abandoned = f.Rows("SELECT * FROM character_raid_runs WHERE state=1 ORDER BY rowid");
        f.Admit(actor, false);
        f.Admit(actor, true);
        That(f.Scalar("SELECT count(*) FROM character_raid_runs WHERE entry_epoch=0") == 10 &&
            f.Scalar("SELECT count(*) FROM character_raid_runs WHERE entry_epoch=1") == 2 &&
            f.Rows("SELECT * FROM character_raid_runs WHERE entry_epoch=0 ORDER BY rowid LIMIT 9") == abandoned &&
            f.Rows("SELECT * FROM character_fiendwar_recovery WHERE instance_id IN " +
                "(SELECT instance_id FROM character_raid_runs WHERE entry_epoch=0) ORDER BY rowid") == oldPayments,
            "刷新后两模式均真实消费新账号日额度，旧十次保留且已终结九行原文不变");
    }

    // 只为结算持久化合同合成已完成的Battle事实；保留真实成员、来源、周策略及冻结奖励。
    // 不修改数据库状态，也不将此方法解释为完整原生战斗或客户端通关。
    internal static AntonRaidBattle.LukePhaseSettlement CaptureSettledContract(AntonRaidBattle battle, params RaidActor[] actors)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var participation = (Dictionary<(int Phase, ushort Character), int>)typeof(AntonRaidBattle)
            .GetField("lukeParticipation", flags)!.GetValue(battle)!;
        foreach (var actor in actors) participation[(battle.View.Phase, actor.Character)] = 1;
        typeof(AntonRaidBattle).GetField("pendingTransition", flags)!.SetValue(battle,
            battle.View with { State = AntonRaidBattleState.Reward });
        return AntonRaidBattle.LukePhaseSettlement.Capture(battle, battle.View.Phase);
    }

    sealed class Fixture : IDisposable
    {
        readonly TcpTestResources resources;
        readonly string path;
        readonly AccountOwners owners = new();
        readonly List<AccountOwners.Lease> leases = [];
        readonly HashSet<string> createdAccounts = new(StringComparer.Ordinal);
        readonly List<GameSession> sessions = [];
        readonly LukeRaidClockTests.Clock clock;
        readonly RaidRegistry registry = new();
        readonly RaidDefinition normal;
        internal CharacterRepository Repository { get; }
        internal DateTimeOffset Now => clock.GetUtcNow();

        internal Fixture(string root, TcpTestResources resources, DateTimeOffset now)
        {
            this.resources = resources;
            Directory.CreateDirectory(root);
            path = Path.Combine(root, "game.db");
            Repository = new(path, resources.Quests);
            clock = new(now);
            var declared = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
                SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(2)!;
            That(declared.Resources is { Fiendwar.WeeklyClearLimit: 2, Fiendwar.DailyAccountLimit: 10,
                FiendwarGuide.Fiendwar.WeeklyClearLimit: 2, FiendwarGuide.Fiendwar.DailyAccountLimit: 10 },
                "候选实际PVF两模式共享周二次和账号日十次声明");
            // 人数缩为单成员只隔离限额事务；票、候战区、模式及奖励仍用实际PVF，不声称正常8人开战。
            normal = declared with { StartMinimum = 1 };
        }

        internal RaidActor Add(string account, string name, byte slot = 0)
        {
            var owner = leases.SingleOrDefault(lease => lease.Account == account);
            if (owner is null) { owner = owners.Acquire(account, () => { }); leases.Add(owner); }
            if (createdAccounts.Add(account))
                new AuthenticationService(new AuthenticationRepository(path)).Create(account, "fixture-password");
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            var role = Repository.Create(owner, candidate with
                { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8672], InitialLearning = learning });
            var items = resources.ItemResources;
            InventoryService.Commit(Repository, owner, role, InventoryRewardGrantService.TryGrantBatch(role,
                [new(normal.Resources!.Script.EntryCost!.ItemId, 12), new(normal.Resources.FiendwarGuide!.Script.EntryCost!.ItemId, 12)],
                items.Items, items.GoldLimits, items.EpicPieces, Now));
            var session = new GameSession(Repository, resources.Towns, owner, questCatalog: resources.Quests,
                entry: new(new(2, 39), 45, "[raid]"), timeProvider: clock);
            sessions.Add(session);
            session.Select(slot); session.MarkReady();
            return session.CaptureRaidActor(registry) ?? throw new InvalidOperationException("共享限额夹具不能捕获原会话");
        }

        internal void MoveTo(DateTimeOffset now)
        {
            That(now >= Now, "限额夹具时钟只向前推进");
            clock.AdvanceWithoutCallbacks(checked((int)(now - Now).TotalSeconds));
        }

        (RaidDefinition Definition, LukeRaidCompletionPolicy Policy, ImmutableArray<RaidStartReservation> Rows) Plan(RaidActor actor, bool guide)
        {
            var definition = guide ? normal with { MemberMax = 1, StartMinimum = 1, Resources = normal.Resources!.FiendwarGuide } : normal;
            var script = definition.Resources!.Fiendwar!;
            var policy = new FiendwarRewards(script, resources.ItemResources.Items, resources.ItemResources.Boosters).Create(false, false, _ => 0);
            var party = new RaidParty(1, 2, "SharedLimits", "SharedLimits"u8.ToArray(), actor.Channel,
                actor.Character, [new(actor, 0)], guide ? 1 : normal.MemberMax, 0) { Guide = guide };
            var rows = AntonRaidStart.PlanReservations(party, definition.Resources.Script.EntryCost!,
                definition.Resources.Script.WaitingRoom!, resources.Towns, resources.ItemResources.EpicPieces, Now, out var reason);
            That(reason is null && rows.Length == 1, "共享限额用原费用和真实候战落点规划：" + reason);
            rows = rows.Select(row => row with { Lifecycle = new(policy.Source, Now, policy.Cycle.Key(Now), policy.WeeklyClearLimit,
                script.DailyAccountLimit, guide ? Repository.FiendwarGuideRoute(actor, Now) : null) }).ToImmutableArray();
            return (definition, policy, rows);
        }

        internal AntonRaidBattle Admit(RaidActor actor, bool guide)
        {
            var plan = Plan(actor, guide);
            var cost = plan.Definition.Resources!.Script.EntryCost!;
            int before = actor.Session.Selected!.Inventory.Items.Where(item => item.Core.ItemId == cost.ItemId).Sum(item => item.Core.Count);
            var battle = new AntonRaidBattle(plan.Definition.Resources!.Script, kind: RaidKind.Fiendwar,
                completion: plan.Policy, clock: clock, fiendwar: plan.Definition.Resources.Fiendwar,
                fiendwarRoute: guide ? Repository.FiendwarGuideRoute(actor, Now) : 0);
            var committed = Repository.CommitRaidStart(RaidKind.Fiendwar, plan.Definition, battle.Instance, plan.Rows);
            actor.Session.InstallRaidStart(plan.Rows[0], committed[0]);
            That(before - actor.Session.Selected!.Inventory.Items.Where(item => item.Core.ItemId == cost.ItemId).Sum(item => item.Core.Count) == cost.Count,
                "真实开战恰扣当前模式PVF声明的原门票数量");
            That(battle.JoinStart(actor), "真实原实例成员加入结算合同Battle");
            That(Scalar("SELECT count(*) FROM character_raid_runs WHERE instance_id='" + battle.Instance.ToString("N") + "' AND raid_type=2 AND state=0") == 1,
                "开战费用与原type2入场身份同事务落盘");
            return battle;
        }

        internal RaidPhaseEligibility Clear(AntonRaidBattle battle)
        {
            var actor = sessions.Select(session => session.CaptureRaidActor(registry)).Single(row => row is not null &&
                battle.RewardPolicy(row, 0) is not null)!;
            var candidate = CaptureSettledContract(battle, actor);
            var committed = Repository.CommitLukePhase(candidate);
            string frozen = JsonSerializer.Serialize(committed);
            That(JsonSerializer.Serialize(Repository.CommitLukePhase(candidate)) == frozen,
                "真实阶段提交重放保持同event、anchor、周与奖励，不重复消耗共享次数");
            That(battle.AcceptLukeSettlement(candidate, committed) is not null,
                "SQL资格回执经原Battle屏障安装，不直接改存储终态");
            return committed.Members.Single();
        }

        internal void Rejected(RaidActor actor, bool guide, string reason)
        {
            var plan = Plan(actor, guide);
            var selected = actor.Session.Selected;
            string before = Snapshot();
            string? actual = null;
            try { Repository.CommitRaidStart(RaidKind.Fiendwar, plan.Definition, Guid.NewGuid(), plan.Rows); }
            catch (RaidLifecycleRejectedException error) { actual = error.Reason; }
            That(actual == reason, "另一模式仍由原SQL守卫拒绝：" + reason + "/" + actual);
            That(ReferenceEquals(selected, actor.Session.Selected) && Snapshot() == before,
                "限额拒绝不扣任何模式门票、不改角色revision/历史/退款快照/引导绑定/奖励");
        }

        internal long Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        string Snapshot()
        {
            return string.Join("\n", new[] { "characters", "character_inventory_items", "character_inventory_slots", "character_raid_runs",
                "character_fiendwar_recovery", "account_fiendwar_guides", "character_raid_reward_receipts", "account_raid_quota_epochs" }
                .Select(table => Rows("SELECT * FROM " + table + " ORDER BY rowid")));
        }

        internal string Rows(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            var result = new List<object?[]>();
            using var command = db.CreateCommand(); command.CommandText = sql;
            using var rows = command.ExecuteReader();
            while (rows.Read()) { var values = new object[rows.FieldCount]; rows.GetValues(values); result.Add(values); }
            return JsonSerializer.Serialize(result);
        }

        public void Dispose()
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
        }
    }
}
