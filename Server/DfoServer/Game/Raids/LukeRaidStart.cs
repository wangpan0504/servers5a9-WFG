using System.Collections.Immutable;
using DfoServer.Game.Party;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Raids;

// 必须显式提供已解释的执行脚本及来源；原资源Issues不会被清空或默认为Anton策略。
// 生产由LukeProductionPolicy装配完整原规则及本服差量；合成脚本仅证明对应事务。
internal sealed record LukeRaidStartPolicy(string Source, RaidScript ExecutionScript)
{
    public LukeRaidRunPolicy? Run { get; init; }
    public LukeRaidCompletionPolicy? Completion { get; init; }
    public LukeResourceCompletionProfile? ResourceCompletion { get; init; }
    public LukeRaidRevivalPolicy? Revival { get; init; }
    public LukeRaidClockPolicy? Timers { get; init; }
    public LukeRaidPhaseTimeoutPolicy? PhaseTimeout { get; init; }
    public LukeFacilityReportPolicy? Facilities { get; init; }
    public LukeRaidPacingPolicy? Pacing { get; init; }
    public LukeRaidFlowPolicy? Flow { get; init; }
    public LukeResourceRewards? ResourceRewards { get; init; }
    public LukeCardSetCatalog? Cards { get; init; }
}

internal sealed class LukeRaidStart(RaidRegistry registry, PartyService parties, IRaidStartStore store,
    TownCatalog towns, EpicPieceCatalog pieces, LukeRaidStartPolicy policy, TimeProvider? clock = null, ItemCatalog? rewardItems = null, LukeRaidClocks? timers = null)
{
    public RaidStartResult Start(RaidActor actor, RaidParty expected, RaidDefinition definition,
        ImmutableArray<PartySnapshot> bindings, Func<bool> commandsCurrent)
    {
        if (expected.Leader != actor.Character) return new(8, "not-leader");

        if (definition.Type != (byte)RaidKind.Luke || definition.MemberMax is < 1 or > 8 ||
            definition.MemberMax != expected.Capacity ||
            definition.Resources?.Script is not { EntryCost: { } cost, WaitingRoom: { } room } ||
            definition.Resources.MissingItems.Contains(cost.ItemId)) return new(19, "luke-start-definition-unavailable");
        if (expected.Started is not null)
        {
            try { return Replay(actor, expected, bindings, commandsCurrent, cost, room); }
            catch (InvalidOperationException) when (expected.Members.Any(m => !m.Actor.IsCurrent))
            { return new(19, "member-owner-changed"); }
        }
        if (expected.Members.Length < definition.StartMinimum) return new(22, "too-few-members");
        var roster = RaidPreparationPlan.Capture(expected, out var rejection);
        if (roster is null) return new(19, rejection!);
        if (PolicyRejection(definition) is { } policyRejection) return new(19, policyRejection);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var reservations = AntonRaidStart.PlanReservations(expected, cost, room, towns, pieces, now, out rejection);
        if (rejection is not null) return new(19, rejection);
        var admission = policy.Completion is { } completion
            ? new RaidLifecycleAdmission(completion.Source, now, completion.Cycle.Key(now), completion.WeeklyClearLimit)
            : new RaidLifecycleAdmission(policy.Source, now);
        reservations = reservations.Select(row => row with { Lifecycle = admission }).ToImmutableArray();
        // 构造可执行聚合可能拒绝不支持规则，必须在SQL之前完成，不能付费后才发现语义缺失。
        if (PrepareBattle(out var preparedBattle) is { } executionRejection) return new(19, executionRejection);
        var battle = preparedBattle!;
        var instance = battle.Instance;
        bool needsClock = policy.Timers is not null || policy.PhaseTimeout is not null;
        var timerLease = needsClock ? timers?.Reserve(battle) : null;
        if (needsClock && timerLease is null) return new(19, "luke-timer-capacity-unavailable");
        bool installed = false;
        try
        {
            var startResult = RaidOwnerGroup.Execute(RaidKind.Luke, reservations.Select(r => (r.Actor.Owner, r.Expected.Id)).ToArray(), () =>
            {
                if (!commandsCurrent() || !roster.IsCurrent(registry)) return new RaidStartResult(19, "raid-members-changed");
                var (batch, reason) = parties.PlanRaidPreparation(roster, bindings);
                if (batch is null) return new(19, reason!);
                var combat = new List<GameSession.RaidCombatStartBinding>();
                foreach (var reservation in reservations)
                {
                    var binding = reservation.Actor.Session.PrepareRaidCombatStart(reservation.Actor, battle);
                    if (binding is null) return new(19, "raid-combat-binding-conflict");
                    combat.Add(binding);
                }
                var started = new RaidStarted(instance, now, expected.Members.OrderBy(m => m.Slot)
                    .Select(m => (m.Actor.Character, m.Slot, m.Squad)).ToImmutableArray());
                if (!commandsCurrent()) return new(19, "raid-member-command-changed");
                var result = registry.CommitStart(actor, expected, reservations, cost, () => parties.CreateRaidSquads(expected, () =>
                {
                    var committed = store.CommitRaidStart(RaidKind.Luke, definition, instance, reservations);
                    for (int index = 0; index < reservations.Length; index++)
                        reservations[index].Actor.Session.InstallRaidStart(reservations[index], committed[index]);
                    foreach (var binding in combat) binding.Install();
                    return (started, committed);
                }, batch));
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
    // Runtime在装配时核验一次，命令与能力旗标共用同一可用服务，不凭策略非null亮按钮。
    internal string? UnavailableReason(RaidDefinition? definition)
    {
        if (definition is not { Type: (byte)RaidKind.Luke, MemberMax: >= 1 and <= 8, Resources.Script.EntryCost: { } cost,
            Resources.Script.WaitingRoom: not null } || definition.Resources.MissingItems.Contains(cost.ItemId))
            return "luke-start-definition-unavailable";
        return PolicyRejection(definition) ?? PrepareBattle(out _);
    }

    string? PrepareBattle(out AntonRaidBattle? battle)
    {
        battle = null;
        try { battle = new AntonRaidBattle(policy.ExecutionScript, Guid.NewGuid(), RaidKind.Luke, policy.Completion, clock, policy.Revival, policy.PhaseTimeout, policy.Run?.FixedRoute, facilities: policy.Facilities, cardRoute: policy.Run?.CardRoute, lukeEnergyObjects: policy.Run?.UseEnergyObjects == true, pacing: policy.Pacing, flow: policy.Flow, lukeRewards: policy.ResourceRewards, lukeCards: policy.Cards, lukeMazes: policy.Run?.Mazes ?? []); }
        catch (InvalidDataException) { return "luke-execution-policy-invalid"; }
        catch (KeyNotFoundException) { return "luke-execution-policy-reference-invalid"; }
        catch (IndexOutOfRangeException) { return "luke-execution-policy-field-invalid"; }
        catch (OverflowException) { return "luke-execution-policy-arithmetic-invalid"; }
        return battle.View.State == AntonRaidBattleState.Fighting && battle.View.Phase == 0
            ? null : "luke-execution-policy-start-state-invalid";
    }

    string? PolicyRejection(RaidDefinition definition)
    {
        if (policy.Pacing is { } pacing && (!pacing.Valid || policy.Run is null || policy.Timers is null ||
            policy.PhaseTimeout is null || policy.Completion is null)) return "luke-pacing-policy-unavailable";
        RaidScript script;
        try { script = policy.Pacing?.Adapt(policy.ExecutionScript) ?? policy.ExecutionScript; }
        catch (InvalidDataException) { return "luke-execution-policy-invalid"; }
        var original = definition.Resources!.Script;
        if (string.IsNullOrWhiteSpace(policy.Source) || !script.Issues.IsEmpty || script.Phases.IsDefaultOrEmpty ||
            script.Phases.Length != definition.PhaseMax || original.Phases.Length != definition.PhaseMax ||
            !script.Symbols.OrderBy(p => p.Key).SequenceEqual(original.Symbols.OrderBy(p => p.Key)))
            return "luke-execution-policy-unavailable";
        if (policy.Facilities is { } facilities && (!facilities.Valid || policy.Run is null ||
            definition.Resources.LukeObjects is not { BindingCount: > 0 })) return "luke-facility-bridge-policy-unavailable";
        if (policy.Run is { UseEnergyObjects: true } energy && (definition.Resources.EntryCatalog is not { } energyCatalog ||
            energy.EnergyRejection(energyCatalog, definition.Resources.LukeObjects) is not null))
            return "luke-energy-object-resource-unavailable";
        if (policy.Completion is { } completion && (!AntonRaidBattle.ValidLukeCompletion(completion) || policy.Run is null ||
            rewardItems is null || completion.Phases.Any(phase => phase.Rewards.Offers.Any(offer => offer.Grants.Any(grant =>
                grant.Count <= 0 || !rewardItems.TryGet(grant.ItemId, out _)))))) return "luke-completion-policy-unavailable";
        if (policy.Timers is { } timerPolicy && (string.IsNullOrWhiteSpace(timerPolicy.Source) || policy.Run is null || timers is null))
            return "luke-timer-policy-unavailable";
        if (policy.PhaseTimeout is { } phaseTimeout && (policy.Run is null || timers is null ||
            !phaseTimeout.Matches(original))) return "luke-phase-timeout-policy-unavailable";
        if (policy.PhaseTimeout is not null && policy.Timers is null && script.Phases.Any(phase =>
            phase.Rules.Any(rule => rule.Behavior.Any(clause => clause.Tag == "[set timer]"))))
            return "luke-rule-timer-policy-required";
        if (policy.Revival is { } revival && (string.IsNullOrWhiteSpace(revival.Source) || policy.Run is null ||
            original.Phases.Length != 2 || original.Phases[0].Declarations.Any(row => row.Tag == "[phase coin limit]") ||
            original.Phases[1].Declarations.Count(row => row.Tag == "[phase coin limit]" && row.Values.Length == 1 &&
                row.Values[0].Number == LukeRaidRevivalPolicy.PhaseLimit) != 1 ||
            definition.Resources.EntryCatalog is not { } revivalCatalog ||
            original.Phases[0].Dungeons.Any(row => revivalCatalog.Find(row.Id)?.CoinLimit != LukeRaidRevivalPolicy.DungeonLimit)))
            return "luke-revival-policy-unavailable";
        if (policy.Run is { } run)
        {
            if (definition.Resources.EntryCatalog is not { } catalog) return "luke-run-catalog-unavailable";
            if (run.Rejection(catalog) is { } rejection) return rejection;
            // 首个Run用例没有奖励协调者；不能开放会自动进入空奖励窗口的执行策略。
            if (policy.Completion is null && script.Phases.Any(phase => phase.Rules.Any(rule => rule.Behavior.Any(clause => clause.Tag == "[clear phase]"))))
                return "luke-run-reward-coordination-unavailable";
        }
        for (int index = 0; index < script.Phases.Length; index++)
        {
            var phase = script.Phases[index];
            var ids = phase.Dungeons.Select(d => d.Id).ToHashSet();
            if (ids.Count != phase.Dungeons.Length || !ids.Order().SequenceEqual(original.Phases[index].Dungeons.Select(d => d.Id).Order()) ||
                phase.Dungeons.Any(d => !script.Symbols.ContainsKey(d.HpSymbol))) return "luke-execution-policy-dungeon-mismatch";
            var mapIds = definition.Resources.Dungeons.Where(row => ids.Contains(row.Id)).SelectMany(row => row.MapIds).ToHashSet();
            foreach (var rule in phase.Rules)
                if (RuleRejection(rule, script, original, ids, mapIds, policy.Timers is not null, policy.Facilities?.UseResourceBudgets == true,
                    policy.Run?.UseEnergyObjects == true, policy.Pacing is not null && policy.PhaseTimeout is not null) is { } reason)
                    return policy.Pacing is null ? reason : $"{reason}:phase={index};actions={string.Join(',', rule.Behavior.Select(row => row.Symbol ?? row.Tag))}";
        }
        return null;
    }

    // 单条检查与开战守卫共用；null只说明条款支持，不代表整份策略、资源或原生流程已可运行。
    internal static string? RuleRejection(RaidRule rule, RaidScript script, RaidScript original,
        IReadOnlySet<int> ids, IReadOnlySet<int> mapIds, bool timersEnabled, bool facilityBudgetsEnabled = false,
        bool energyObjectsEnabled = false, bool safePhaseExitEnabled = false)
    {
        bool Symbol(RaidClause clause) => clause.Symbol is not null && script.Symbols.ContainsKey(clause.Symbol);
        bool Dungeon(RaidClause clause) => clause.DungeonId is { } id && ids.Contains(id);
        bool State(RaidClause clause) => clause.State is { } state && Enum.IsDefined(state);
        bool Timer(RaidClause clause) => clause.Timer is { Kind: 1 or 2 } timer &&
            original.Phases.Any(p => p.Dungeons.Any(d => d.Id == timer.DungeonId));
        if (timersEnabled && rule.Behavior.Any(clause => clause.Tag == "[set timer]" &&
            (clause.RawIntegers.Length != 3 || clause.RawIntegers[2] is <= 0 or > 86400)))
            return "luke-timer-duration-unavailable";
        if (rule.Behavior.Any(clause => clause.Tag == "[set]" && !SymbolWriteSupported(clause.Symbol, original,
            facilityBudgetsEnabled, energyObjectsEnabled, safePhaseExitEnabled)))
            return "luke-execution-symbol-consumer-unavailable";
        if (rule.Trigger.Any(clause => clause.Operator is not null || !(clause.Tag switch
        {
            "[if]" => Symbol(clause) && clause.RawIntegers.Length == 1,
            "[on change symbol]" => Symbol(clause),
            "[on change dungeon state]" => Dungeon(clause),
            "[check dungeon state]" => Dungeon(clause) && State(clause),
            "[check timer end]" => Timer(clause),
            "[on enter dungeon]" => Dungeon(clause),
            "[on giveup dungeon]" => Dungeon(clause),
            "[phase end]" => clause.RawIntegers is [>= 0 and < 2],
            "[on enter map]" => clause.MapId is { } map && mapIds.Contains(map),
            _ => false
        })) || rule.Behavior.Any(clause =>
            (clause.Tag == "[set]" ? clause.Operator is not (null or "[+]") : clause.Operator is not null) || !(clause.Tag switch
        {
            "[set]" => Symbol(clause) && clause.RawIntegers.Length == 1,
            "[set dungeon state]" => Dungeon(clause) && State(clause),
            "[set timer]" => Timer(clause) && clause.RawIntegers.Length == 3 && clause.RawIntegers.All(v => v >= 0) &&
                clause.RawIntegers[0] == clause.Timer!.Kind && clause.RawIntegers[1] == clause.Timer.DungeonId,
            "[reset timer]" => Timer(clause) && clause.RawIntegers.Length == 2 &&
                clause.RawIntegers[0] == clause.Timer!.Kind && clause.RawIntegers[1] == clause.Timer.DungeonId,
            "[clear phase]" => clause.RawIntegers.IsEmpty,
            "[fail phase]" => clause.RawIntegers.IsEmpty,
            "[reset sanctum object]" => facilityBudgetsEnabled && Dungeon(clause) && clause.DungeonId is 5054 or 5055 or 5056 &&
                clause.RawIntegers is [var reset] && reset == clause.DungeonId,
            _ => false
        }))) return "luke-execution-policy-rule-unsupported";
        return null;
    }

    // 583会触达原生消费者，不能因SET语法可解析就开放任意机制符号。
    // HP来自已登记DGN；阶段控制与计数仅认本版精确ID/名，新增字段须重新核消费者。
    static bool SymbolWriteSupported(string? symbol, RaidScript script, bool facilityBudgetsEnabled,
        bool energyObjectsEnabled, bool safePhaseExitEnabled)
    {
        if (symbol is null || !script.Symbols.TryGetValue(symbol, out int id)) return false;
        if (script.Phases.SelectMany(phase => phase.Dungeons).Any(dungeon => dungeon.HpSymbol == symbol)) return true;
        if (facilityBudgetsEnabled && (id, symbol) is (117, "[birth sanctum object]") or
            (118, "[extinction sanctum object]") or (119, "[spread sanctum object]")) return true;
        // 138/139按原值发布界面状态；Boss的113/114另由真实能源对象提交，不将两者映射或布尔化。
        if (energyObjectsEnabled && (id, symbol) is (138, "[energy control effect]") or (139, "[energy save effect]")) return true;
        // 统一节奏仅在失败终结保留102事实，快照屏蔽原生死亡，由PhaseExit安全退场。
        if (safePhaseExitEnabled && (id, symbol) == (102, "[kill all character]")) return true;
        return (id, symbol) is (104, "[1phase fail]") or (105, "[1phase success]") or
            (106, "[2phase init]") or (107, "[1phase init]") or (108, "[2phase dungeon type]") or
            (122, "[temple clear]") or (124, "[altar clear]") or (140, "[altar enterance clear]");
    }

    RaidStartResult Replay(RaidActor actor, RaidParty expected,
        ImmutableArray<PartySnapshot> bindings, Func<bool> commandsCurrent, RaidCost cost, RaidWaitingRoom room) =>
        RaidOwnerGroup.Execute(RaidKind.Luke, expected.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(), () =>
        {
            if (!commandsCurrent() || !registry.IsCurrent(expected) || expected.Started is not { } started)
                return new RaidStartResult(19, "raid-replay-stale");
            var captures = expected.Members.OrderBy(m => m.Slot).Select(m => (m.Actor, Character: m.Actor.Session.CaptureRaidStart(m.Actor))).ToArray();
            var battle = actor.Session.RaidCombatView();
            if (battle?.Instance != started.InstanceId || battle.Phase != started.Phase || battle.State != AntonRaidBattleState.Fighting || battle.ClockFaulted ||
                captures.Any(m => m.Character is null || m.Character.Position.Town != room.Town || m.Character.Position.Area != room.Area ||
                !ReferenceEquals(m.Actor.Session.RaidCombatView(), battle))) return new(19, "raid-replay-binding-changed");
            var notices = parties.RaidStartNotices(expected, bindings);
            if (notices is null) return new(19, "raid-replay-party-changed");
            var reservations = captures.Select(m => new RaidStartReservation(m.Actor, m.Character!, m.Character!.Position,
                new(m.Character.Inventory, m.Character.Inventory, "raid_start_resync"))).ToImmutableArray();
            return new(0, "already-started", new(expected, reservations, captures.Select(m => m.Character!).ToImmutableArray(), notices, cost)
                { Resync = true, Battle = battle }, Replay: true);
        });

}
