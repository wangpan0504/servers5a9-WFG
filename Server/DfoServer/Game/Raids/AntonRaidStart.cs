using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Raids;

internal sealed record RaidStarted(Guid InstanceId, DateTimeOffset StartedAt,
    ImmutableArray<(ushort Character, byte Slot, byte Squad)> Participants, byte Phase = 0);
internal sealed record RaidLifecycleAdmission(string Source, DateTimeOffset At, int? Week = null, int? WeeklyLimit = null,
    int? DailyAccountLimit = null, int? FiendwarGuideRoute = null);
internal sealed record RaidStartReservation(RaidActor Actor, Character Expected, Location Position, InventoryPlan Inventory)
{
    public RaidLifecycleAdmission? Lifecycle { get; init; }
}
internal sealed record RaidStartReceipt(RaidParty Party, ImmutableArray<RaidStartReservation> Reservations,
    ImmutableArray<Character> Characters, PartyNotice[] Squads, RaidCost Cost)
{
    public bool Resync { get; init; }
    public AntonRaidBattleView? Battle { get; init; }
}
internal sealed record RaidStartResult(ushort Error, string Reason, RaidStartReceipt? Receipt = null, bool Replay = false);
internal sealed record RaidCostStatus(ushort Character, byte State, short Count);

// 全团费用与位置沿同一外层事务提交；回执只用于本次发布，不存另一份在线资产。
internal interface IRaidStartStore
{
    int FiendwarGuideRoute(RaidActor actor, DateTimeOffset now);
    ImmutableArray<Character> CommitRaidStart(RaidKind kind, RaidDefinition definition, Guid instance, IReadOnlyList<RaidStartReservation> reservations);
}

// 发起连接已持自己的门禁；其余门禁只尝试获取，忙时立即拒绝，避免与既有组队命令相互等待。
internal sealed class RaidStartCommands : IDisposable
{
    readonly List<SemaphoreSlim> held = [];
    public static RaidStartCommands? TryEnter(RaidParty party, Game.Session.GameSession initiator)
    {
        var scope = new RaidStartCommands();
        foreach (var member in party.Members.OrderBy(m => m.Actor.Owner.Account, StringComparer.Ordinal))
        {
            if (ReferenceEquals(member.Actor.Session, initiator)) continue;
            var gate = member.Actor.Session.CommandGate;
            if (!gate.Wait(0)) { scope.Dispose(); return null; }
            scope.held.Add(gate);
        }
        return scope;
    }
    public void Dispose() { foreach (var gate in held.AsEnumerable().Reverse()) gate.Release(); held.Clear(); }
}

internal sealed class AntonRaidStart(RaidRegistry registry, PartyService parties, IRaidStartStore store,
    TownCatalog towns, EpicPieceCatalog pieces, TimeProvider? clock = null, bool phaseProgress = false,
    AntonResourceRewards? rewards = null, LukeRaidClocks? timers = null, PreyGuideRewards? preyRewards = null,
    FiendwarRewards? fiendwarRewards = null)
{
    public RaidChange? Leave(RaidActor actor, ushort target)
    {
        var result = actor.Owner.Execute(() =>
        {
            var departingBattle = actor.Session.CurrentRaidBattle(actor);
            var beforeBattle = departingBattle?.View;
            var change = registry.Leave(actor, target);
            if (change is { Before.Started: { } started, Removed: { } departed })
                departed.Session.DetachRaidCombatStart(departed, started.InstanceId);
            PartyNotice[] notices = change?.Before.Started is not null && change.Removed is { } removed
                ? parties.DetachNotices(removed.Session) : [];
            return (Change: change, Notices: notices, Battle: departingBattle, BeforeBattle: beforeBattle);
        });
        if (result.Notices.Length != 0) parties.Publish(result.Notices);
        if (result.Battle is { Kind: RaidKind.Luke } battle && !ReferenceEquals(battle.View, result.BeforeBattle)) battle.Publish(battle.View);
        return result.Change is { Before.Type: 2, Removed: { } removedActor } change
            ? change with { TownReturn = removedActor.Session.ReturnFiendwarLobby(removedActor) } : result.Change;
    }

    public RaidStartResult Start(RaidActor actor, RaidParty expected, RaidDefinition definition)
    {
        if (expected.Leader != actor.Character) return new(8, "not-leader");
        if (expected.Started is not null) return new(0, "already-started", Replay: true);
        bool prey = definition.Type == 3 && expected.Guide && expected.Capacity == 1 && expected.Members.Length == 1 && preyRewards is not null;
        bool fiendwar = definition.Type == 2 && fiendwarRewards is not null && definition.Resources?.Fiendwar is not null;
        if (fiendwar && expected.Guide)
        {
            if (expected.Capacity != 1 || expected.Members.Length != 1 ||
                definition.Resources?.FiendwarGuide is not { ResourcesReady: true, EntryCatalog.Ready: true, Fiendwar.Guide: true } guideResources)
                return new(19, "fiendwar-guide-definition-unavailable");
            definition = definition with { MemberMax = 1, StartMinimum = 1, Resources = guideResources };
        }
        if (definition.Type != 0 && !prey && !fiendwar || definition.Resources?.Script is not { Issues.IsEmpty: true, EntryCost: { } cost, WaitingRoom: { } room })
            return new(19, "anton-start-definition-unavailable");
        if (definition.Resources.MissingItems.Contains(cost.ItemId)) return new(19, "entry-cost-resource-missing");
        if (expected.Members.Length < definition.StartMinimum) return new(22, "too-few-members");
        if (expected.Members.Length > definition.MemberMax || expected.Members.Length is < 1 or > 20 ||
            (!fiendwar && (expected.Members.Any(m => m.Squad is < 1 or > 5) || expected.Members.GroupBy(m => m.Squad).Any(g => g.Count() > 4))))
            return new(19, "squads-unassigned");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var reservations = PlanReservations(expected, cost, room, towns, pieces, now, out var rejection);
        if (rejection is not null) return new(19, rejection);
        // 资源政策只由Runtime装配；不改变显式领域夹具或关闭阶段推进时的行为。
        if (!prey && !fiendwar && rewards is not null)
            reservations = reservations.Select(row => row with { Lifecycle = new( AntonResourceRewards.Source, now,
                Game.Dungeons.DungeonIncountPolicy.Period(now).Week, RaidRewardRules.WeeklyClearLimit) }).ToImmutableArray();
        // etc/raid/anton.etc [PHASE TIME OVER]；沿Luke现有秒制与激活锚点，是本服约定，非客户端原厂证明。
        LukeRaidPhaseTimeoutPolicy? timeout = null;
        if (!prey && !fiendwar && rewards is not null)
        {
            var limits = definition.Resources.Script.Headers.Where(row => row.Tag == "[phase time over]").ToArray();
            if (limits is not [var limit]) return new(19, "anton-phase-timeout-unavailable");
            timeout = new("anton-local-phase-seconds-v1;etc/raid/anton.etc", limit.Values.Select(row => row.Number).ToImmutableArray());
            if (!timeout.Matches(definition.Resources.Script)) return new(19, "anton-phase-timeout-unavailable");
        }
        var completion = prey ? preyRewards!.Create(actor.Session.PremiumPcRoom) : null;
        if (completion is not null)
        {
            int daily = definition.Resources.Script.Headers.Single(row => row.Tag == "[daily account enter limit]").Values.Single().Number;
            reservations = reservations.Select(row => row with { Lifecycle = new(completion.Source, now,
                completion.Cycle.Key(now), completion.WeeklyClearLimit, daily) }).ToImmutableArray();
            int total = definition.Resources.Script.Headers.Single(row => row.Tag == "[phase total time over]").Values.Single().Number;
            timeout = new(PreyGuideRewards.Source, [total, total]);
        }
        int guideRoute = fiendwar && expected.Guide ? store.FiendwarGuideRoute(actor, now) : 0;
        ImmutableDictionary<ushort, ImmutableArray<RaidPhaseRewardPolicy>>? memberRewards = null;
        if (fiendwar)
        {
            if (expected.Members.Any(member => RaidAdmissionPlan.Rejection(member.Actor.Session.ChannelEntry,
                member.Actor.Session.Selected!) is not null || parties.Current(member.Actor.Session) is not null))
                return new(19, "fiendwar-member-not-ready");
            // 在全团owner/事务之外抽取每人完整结果，SQL重试与网络重发均复用原冻结产物。
            var policies = expected.Members.ToDictionary(member => member.Actor.Character,
                member => expected.Guide ? fiendwarRewards!.CreateGuide(member.Actor.Session.PremiumPcRoom) :
                    fiendwarRewards!.Create(member.Actor.Session.PremiumPcRoom));
            completion = policies[actor.Character];
            memberRewards = policies.ToImmutableDictionary(row => row.Key,
                row => row.Value.Phases.Select(phase => phase.Rewards).ToImmutableArray());
            reservations = reservations.Select(row => row with { Lifecycle = new(completion.Source, now,
                completion.Cycle.Key(now), completion.WeeklyClearLimit, definition.Resources.Fiendwar!.DailyAccountLimit, expected.Guide ? guideRoute : null) }).ToImmutableArray();
        }
        var kind = fiendwar ? RaidKind.Fiendwar : prey ? RaidKind.Prey : RaidKind.Anton;
        var battle = new AntonRaidBattle(definition.Resources.Script, kind: kind, completion: completion,
            antonPhaseProgress: !prey && !fiendwar && phaseProgress, clock: clock, antonRewards: prey || fiendwar ? null : rewards, phaseTimeout: timeout,
            fiendwar: fiendwar ? definition.Resources.Fiendwar : null, memberRewards: memberRewards, fiendwarRoute: guideRoute);
        battle.PrepareFiendwarPayments(reservations);
        var timerLease = timers?.Reserve(battle);
        if (timers is not null && timerLease is null) return new(19, "anton-clock-capacity");
        bool installed = false;
        try
        {
            var startResult = RaidOwnerGroup.Execute(kind, reservations.Select(r => (r.Actor.Owner, r.Expected.Id)).ToArray(), () =>
            {
                if (registry.Find(actor)?.Started is not null) return new RaidStartResult(0, "already-started", Replay: true);
                var combat = new List<Game.Session.GameSession.RaidCombatStartBinding>();
                foreach (var reservation in reservations)
                {
                    var binding = reservation.Actor.Session.PrepareRaidCombatStart(reservation.Actor, battle);
                    if (binding is null) return new RaidStartResult(19, "raid-combat-binding-conflict");
                    combat.Add(binding);
                }
                (RaidStarted Start, ImmutableArray<Character> Characters) CommitMembers()
                {
                    var instance = battle.Instance;
                    var committed = store.CommitRaidStart(kind, definition, instance, reservations);
                    for (int i = 0; i < reservations.Length; i++)
                        reservations[i].Actor.Session.InstallRaidStart(reservations[i], committed[i]);
                    foreach (var binding in combat) binding.Install();
                    return (new RaidStarted(instance, now, expected.Members.OrderBy(m => m.Slot)
                        .Select(m => (m.Actor.Character, m.Slot, m.Squad)).ToImmutableArray()), committed);
                }
                var result = registry.CommitStart(actor, expected, reservations, cost, () =>
                {
                    if (!fiendwar) return parties.CreateRaidSquads(expected, CommitMembers);
                    if (expected.Members.Any(member => parties.Current(member.Actor.Session) is not null)) return null;
                    var committed = CommitMembers();
                    return (committed.Start, committed.Characters, Array.Empty<PartyNotice>());
                });
                return result.Receipt is { } receipt ? result with { Receipt = receipt with { Battle = battle.View } } : result;
            });
            if (startResult.Receipt is not null)
            {
                installed = true;
                timerLease?.Activate();
                return startResult with { Receipt = startResult.Receipt with { Battle = battle.View } };
            }
            return startResult;
        }
        catch (RaidLifecycleRejectedException error) { return new(19, error.Reason); }
        catch (InvalidOperationException) when (reservations.Any(r => !r.Actor.IsCurrent))
        { return new(19, "member-owner-changed"); }
        finally { if (!installed) timerLease?.Dispose(); }
    }

    internal static ImmutableArray<RaidStartReservation> PlanReservations(RaidParty expected, RaidCost cost, RaidWaitingRoom room,
        TownCatalog towns, EpicPieceCatalog pieces, DateTimeOffset now, out string? rejection)
    {
        if (room.Town is < 0 or > 255 || room.Area is < 0 or > 255 ||
            !towns.Areas.TryGetValue(((byte)room.Town, (byte)room.Area), out var area))
        { rejection = "waiting-room-landing-unavailable"; return []; }
        var plans = ImmutableArray.CreateBuilder<RaidStartReservation>(expected.Members.Length);
        foreach (var member in expected.Members.OrderBy(m => m.Slot))
        {
            var captured = member.Actor.Session.CaptureRaidStart(member.Actor);
            if (captured is null) { rejection = "member-state-changed"; return []; }
            var completed = QuestCompletion(captured);
            var target = new Location(area.Town, area.Area, 0, 0);
            if (towns.DestinationAdmission(target, captured.Level, completed) != "ready") { rejection = "waiting-room-access-denied"; return []; }
            // 复用城镇入口算法读取真实门或到达标记，不猜候战室坐标。
            var landing = towns.Entrance(captured.Position, target, completed) ?? area.Map(completed).Links
                .Where(l => l.Gate.Door).OrderBy(l => l.Town).ThenBy(l => l.Area)
                .Select(l => towns.Entrance(new(l.Town, l.Area, 0, 0), target, completed)).FirstOrDefault(p => p is not null);
            if (landing is null) { rejection = "waiting-room-landing-unavailable"; return []; }
            // 数量取匹配 PVF 的 ENTRY COST，集中在资源声明；不解释658的0/1为扣费命令。
            var payment = InventoryMaterialConsumptionService.Plan(captured.Inventory, [new(cost.ItemId, cost.Count)], pieces, now);
            if (!payment.Accepted || payment.Plan is null) { rejection = "entry-cost-insufficient"; return []; }
            plans.Add(new(member.Actor, captured, landing, payment.Plan));
        }
        rejection = null;
        return plans.ToImmutable();
    }

    static IReadOnlySet<int> QuestCompletion(Character character) => DfoServer.Game.Quests.QuestEligibility.Completed(character);

    public static RaidCostStatus Cost(RaidActor actor, RaidCost cost, bool committed = false)
    {
        return actor.Session.CaptureRaidCost(actor, cost, committed);
    }
}
