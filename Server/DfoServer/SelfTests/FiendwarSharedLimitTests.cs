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

// 真实开战事务与合成已结算事实的持久化合同；不代表完整战斗、客户端或GM重置验收。
internal static class FiendwarSharedLimitTests
{
    internal static void Run(string root, TcpTestResources resources)
    {
        int before = Count;
        foreach (bool firstGuide in new[] { false, true })
            CrossMode(Path.Combine(root, firstGuide ? "guide-normal" : "normal-guide"), resources, firstGuide);
        AccountEntries(Path.Combine(root, "mixed-account-admissions"), resources);
        Console.WriteLine($"PASS {Count - before} 漩涡双模式共享次数持久化合同断言");
    }

    static void CrossMode(string root, TcpTestResources resources, bool firstGuide)
    {
        var firstDay = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.FromHours(8));
        using var fixture = new Fixture(root, resources, firstDay);
        var member = fixture.Add("shared", "FwShare");
        var first = fixture.Admit(member, firstGuide);
        var firstReceipt = fixture.Clear(first);
        That(firstReceipt is { CountsAsClear: true, Eligible: true, RaidType: RaidKind.Fiendwar, Phase: 0 } &&
            firstReceipt.Week == 20261103 && firstReceipt.Offers.Length == 2,
            "首个模式真实结算写type2周计次与完整奖励资格");
        That(fixture.Repository.FiendwarGuideRoute(member, fixture.Now) == 1,
            "任一模式通关都推进同角色的下一次引导路线");
        fixture.Rejected(member, !firstGuide, "fiendwar-daily-character-clear-limit");
        fixture.Rejected(member, firstGuide, "fiendwar-daily-character-clear-limit");
        That(fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE state=2") == 1 &&
            fixture.Scalar("SELECT count(*) FROM account_fiendwar_guides") == (firstGuide ? 1 : 0),
            "同日切模式不能另通一次；失败引导不额外占周绑定");

        fixture.MoveTo(firstDay.AddDays(1).AddSeconds(-1));
        fixture.Rejected(member, !firstGuide, "fiendwar-daily-character-clear-limit");
        fixture.MoveTo(firstDay.AddDays(1));
        var second = fixture.Admit(member, !firstGuide);
        var secondReceipt = fixture.Clear(second);
        That(secondReceipt is { CountsAsClear: true, Eligible: true } && secondReceipt.Week == firstReceipt.Week &&
            !secondReceipt.Offers.Single(row => row.Kind == 1).Grants.SequenceEqual(firstReceipt.Offers.Single(row => row.Kind == 1).Grants),
            "业务日06点后另一模式可用剩余周次数，冻结各自真实模式奖励");
        That(fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE raid_type=2 AND state=2 AND clear_week=20261103") == 2 &&
            fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE json_extract(phase0_receipt,'$.Eligible')=1") == 2,
            "普通和引导共同累计两次最终通关及两份领奖资格，未领取也消耗额度");
        fixture.Frozen(member, firstReceipt);
        fixture.Frozen(member, secondReceipt);

        fixture.MoveTo(firstDay.AddDays(2));
        fixture.Rejected(member, firstGuide, "fiendwar-weekly-clear-limit");
        fixture.Rejected(member, !firstGuide, "fiendwar-weekly-clear-limit");
        fixture.MoveTo(firstDay.AddDays(7).AddSeconds(-1));
        fixture.Fund(member);
        fixture.Rejected(member, !firstGuide, "fiendwar-weekly-clear-limit");
        fixture.MoveTo(firstDay.AddDays(7));
        fixture.Fund(member);
        That(FiendwarRewards.Cycle.Key(fixture.Now) == 20261110 && fixture.Repository.FiendwarGuideRoute(member, fixture.Now) == 0,
            "周二UTC+8的06点恰好换周，引导路线重置且沿同一周期");
        var newWeek = fixture.Clear(fixture.Admit(member, !firstGuide));
        That(newWeek is { CountsAsClear: true, Eligible: true, Week: 20261110 } &&
            fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE state=2 AND clear_week=20261103") == 2 &&
            fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE state=2 AND clear_week=20261110") == 1,
            "新周恢复共享额度，保留旧周两次真实计数");
        fixture.Frozen(member, firstReceipt);
        fixture.Frozen(member, secondReceipt);
    }

    static void AccountEntries(string root, TcpTestResources resources)
    {
        var day = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.FromHours(8));
        using var fixture = new Fixture(root, resources, day);
        var member = fixture.Add("entries", "FwEntries");
        for (int index = 0; index < 10; index++)
        {
            fixture.Admit(member, guide: (index & 1) != 0);
            That(fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE account='entries' AND raid_type=2") == index + 1,
                "混合模式每笔真实入场都占账号日额度，放弃旧实例不退次数");
        }
        That(fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE account='entries' AND state=1") == 9 &&
            fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE account='entries' AND state=0") == 1 &&
            fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE account='entries' AND state=2") == 0,
            "十次未通关入场由原开战事务关闭旧实例，不伪造成功或领奖次数");
        fixture.Rejected(member, false, "fiendwar-daily-account-enter-limit");
        fixture.Rejected(member, true, "fiendwar-daily-account-enter-limit");
        var sameAccount = fixture.Add("entries", "FwAlt", slot: 1);
        fixture.Rejected(sameAccount, false, "fiendwar-daily-account-enter-limit");
        fixture.Rejected(sameAccount, true, "fiendwar-daily-account-enter-limit");
        That(fixture.Repository.FiendwarGuideRoute(member, fixture.Now) == 0 &&
            fixture.Scalar("SELECT count(*) FROM account_fiendwar_guides WHERE account='entries'") == 1,
            "反复进入同一引导角色不占第二槽，未通关也不推进路线");
        var other = fixture.Add("independent", "FwOther");
        fixture.Admit(other, true);
        That(fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE account='independent'") == 1,
            "另一个账号的独立日额度不受原账号十次影响");
        fixture.MoveTo(day.AddDays(1).AddSeconds(-1));
        fixture.Rejected(member, true, "fiendwar-daily-account-enter-limit");
        fixture.MoveTo(day.AddDays(1));
        fixture.Admit(member, true);
        That(fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE account='entries'") == 11 &&
            fixture.Scalar("SELECT count(*) FROM character_raid_runs WHERE account='entries' AND admitted_at=" + day.AddDays(1).ToUnixTimeSeconds()) == 1,
            "次日06点恢复账号入场额度，保留昨日十次历史");
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

        internal void Fund(RaidActor actor)
        {
            // 门票自身的周删除不能掩盖通关周期断言；换周前后沿真实库存服务补新票。
            var items = resources.ItemResources;
            var granted = actor.Session.PlanAndCommitInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(normal.Resources!.Script.EntryCost!.ItemId, 2), new(normal.Resources.FiendwarGuide!.Script.EntryCost!.ItemId, 2)],
                items.Items, items.GoldLimits, items.EpicPieces, Now));
            That(granted.Accepted, "跨周限额夹具沿原库存事务准备尚未过期的两模式门票");
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
            var battle = new AntonRaidBattle(plan.Definition.Resources!.Script, kind: RaidKind.Fiendwar,
                completion: plan.Policy, clock: clock, fiendwar: plan.Definition.Resources.Fiendwar,
                fiendwarRoute: guide ? Repository.FiendwarGuideRoute(actor, Now) : 0);
            var committed = Repository.CommitRaidStart(RaidKind.Fiendwar, plan.Definition, battle.Instance, plan.Rows);
            actor.Session.InstallRaidStart(plan.Rows[0], committed[0]);
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

        internal void Frozen(RaidActor actor, RaidPhaseEligibility receipt)
        {
            foreach (var offer in receipt.Offers)
            {
                var saved = Repository.ReadLukeRewardRecovery(actor.Owner, actor.Session.Selected!,
                    new(actor.Character, receipt.Instance, 0, offer.Kind, offer.Card) { RaidType = RaidKind.Fiendwar });
                That(!saved.Claimed && saved.Event == receipt.Event && saved.Anchor == receipt.Anchor &&
                    saved.Prepared is { RaidType: RaidKind.Fiendwar, Phase: 0 } && saved.Prepared.Week == receipt.Week &&
                    saved.Prepared.Source == receipt.Source && saved.Prepared.Grants.SequenceEqual(offer.Grants),
                    "跨模式及换周后按原type2身份读取两种冻结奖，不按当前模式或时间重抽");
            }
        }

        internal long Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        string Snapshot()
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            var result = new List<object?[]>();
            foreach (string table in new[] { "characters", "character_inventory_items", "character_inventory_slots", "character_raid_runs", "character_fiendwar_recovery", "account_fiendwar_guides", "character_raid_reward_receipts" })
            {
                using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
                using var rows = command.ExecuteReader();
                while (rows.Read()) { var values = new object[rows.FieldCount]; rows.GetValues(values); result.Add(values); }
            }
            return JsonSerializer.Serialize(result);
        }

        public void Dispose()
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
        }
    }
}
