using System.Collections.Immutable;
using DfoServer.Game.Party;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Raids;

// 必须显式提供已解释的执行脚本及来源；原资源Issues不会被清空或默认为Anton策略。
// 当前生产装配没有该策略。测试的合成脚本只证明启动事务，不证明原服阶段规则。
internal sealed record LukeRaidStartPolicy(string Source, RaidScript ExecutionScript)
{
    public LukeRaidRunPolicy? Run { get; init; }
}

internal sealed class LukeRaidStart(RaidRegistry registry, PartyService parties, IRaidStartStore store,
    TownCatalog towns, EpicPieceCatalog pieces, LukeRaidStartPolicy policy, TimeProvider? clock = null)
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
        // 构造可执行聚合可能拒绝不支持规则，必须在SQL之前完成，不能付费后才发现语义缺失。
        if (PrepareBattle(out var preparedBattle) is { } executionRejection) return new(19, executionRejection);
        var battle = preparedBattle!;
        var instance = battle.Instance;
        try
        {
            return RaidOwnerGroup.Execute(RaidKind.Luke, reservations.Select(r => (r.Actor.Owner, r.Expected.Id)).ToArray(), () =>
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
        }
        catch (InvalidOperationException) when (reservations.Any(r => !r.Actor.IsCurrent))
        { return new(19, "member-owner-changed"); }
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
        try { battle = new AntonRaidBattle(policy.ExecutionScript, Guid.NewGuid(), RaidKind.Luke); }
        catch (InvalidDataException) { return "luke-execution-policy-invalid"; }
        catch (KeyNotFoundException) { return "luke-execution-policy-reference-invalid"; }
        catch (IndexOutOfRangeException) { return "luke-execution-policy-field-invalid"; }
        return battle.View.State == AntonRaidBattleState.Fighting && battle.View.Phase == 0
            ? null : "luke-execution-policy-start-state-invalid";
    }

    string? PolicyRejection(RaidDefinition definition)
    {
        var script = policy.ExecutionScript;
        var original = definition.Resources!.Script;
        if (string.IsNullOrWhiteSpace(policy.Source) || !script.Issues.IsEmpty || script.Phases.IsDefaultOrEmpty ||
            script.Phases.Length != definition.PhaseMax || original.Phases.Length != definition.PhaseMax ||
            !script.Symbols.OrderBy(p => p.Key).SequenceEqual(original.Symbols.OrderBy(p => p.Key)))
            return "luke-execution-policy-unavailable";
        if (policy.Run is { } run)
        {
            if (definition.Resources.EntryCatalog is not { } catalog) return "luke-run-catalog-unavailable";
            if (run.Rejection(catalog) is { } rejection) return rejection;
            // 首个Run用例没有奖励协调者；不能开放会自动进入空奖励窗口的执行策略。
            if (script.Phases.Any(phase => phase.Rules.Any(rule => rule.Behavior.Any(clause => clause.Tag == "[clear phase]"))))
                return "luke-run-reward-coordination-unavailable";
        }
        for (int index = 0; index < script.Phases.Length; index++)
        {
            var phase = script.Phases[index];
            var ids = phase.Dungeons.Select(d => d.Id).ToHashSet();
            if (ids.Count != phase.Dungeons.Length || !ids.Order().SequenceEqual(original.Phases[index].Dungeons.Select(d => d.Id).Order()) ||
                phase.Dungeons.Any(d => !script.Symbols.ContainsKey(d.HpSymbol))) return "luke-execution-policy-dungeon-mismatch";
            bool Symbol(RaidClause clause) => clause.Symbol is not null && script.Symbols.ContainsKey(clause.Symbol);
            bool Dungeon(RaidClause clause) => clause.DungeonId is { } id && ids.Contains(id);
            bool State(RaidClause clause) => clause.State is { } state && Enum.IsDefined(state);
            foreach (var rule in phase.Rules)
            {
                if (rule.Trigger.Any(clause => clause.Operator is not null || !(clause.Tag switch
                {
                    "[if]" => Symbol(clause) && clause.RawIntegers.Length == 1,
                    "[on change symbol]" => Symbol(clause),
                    "[on change dungeon state]" => Dungeon(clause),
                    "[check dungeon state]" => Dungeon(clause) && State(clause),
                    "[check timer end]" => clause.Timer is not null,
                    _ => false
                })) || rule.Behavior.Any(clause => clause.Operator is not null || !(clause.Tag switch
                {
                    "[set]" => Symbol(clause) && clause.RawIntegers.Length == 1,
                    "[set dungeon state]" => Dungeon(clause) && State(clause),
                    "[set timer]" => clause.Timer is not null && clause.RawIntegers.Length == 3 && clause.RawIntegers.All(v => v >= 0),
                    "[reset timer]" => clause.Timer is not null && clause.RawIntegers.Length == 2,
                    "[clear phase]" => clause.RawIntegers.IsEmpty,
                    _ => false
                }))) return "luke-execution-policy-rule-unsupported";
            }
        }
        return null;
    }

    RaidStartResult Replay(RaidActor actor, RaidParty expected,
        ImmutableArray<PartySnapshot> bindings, Func<bool> commandsCurrent, RaidCost cost, RaidWaitingRoom room) =>
        RaidOwnerGroup.Execute(RaidKind.Luke, expected.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(), () =>
        {
            if (!commandsCurrent() || !registry.IsCurrent(expected) || expected.Started is not { } started)
                return new RaidStartResult(19, "raid-replay-stale");
            var captures = expected.Members.OrderBy(m => m.Slot).Select(m => (m.Actor, Character: m.Actor.Session.CaptureRaidStart(m.Actor))).ToArray();
            var battle = actor.Session.RaidCombatView();
            if (battle?.Instance != started.InstanceId || battle.Phase != started.Phase || battle.State != AntonRaidBattleState.Fighting ||
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
