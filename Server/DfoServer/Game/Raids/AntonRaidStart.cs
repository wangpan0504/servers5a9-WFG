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
internal sealed record RaidLifecycleAdmission(string Source, DateTimeOffset At, int? Week = null, int? WeeklyLimit = null);
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
    TownCatalog towns, EpicPieceCatalog pieces, TimeProvider? clock = null)
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
        return result.Change;
    }

    public RaidStartResult Start(RaidActor actor, RaidParty expected, RaidDefinition definition)
    {
        if (expected.Leader != actor.Character) return new(8, "not-leader");
        if (expected.Started is not null) return new(0, "already-started", Replay: true);
        if (definition.Type != 0 || definition.Resources?.Script is not { Issues.IsEmpty: true, EntryCost: { } cost, WaitingRoom: { } room })
            return new(19, "anton-start-definition-unavailable");
        if (definition.Resources.MissingItems.Contains(cost.ItemId)) return new(19, "entry-cost-resource-missing");
        if (expected.Members.Length < definition.StartMinimum) return new(22, "too-few-members");
        if (expected.Members.Length > definition.MemberMax || expected.Members.Length is < 1 or > 20 ||
            expected.Members.Any(m => m.Squad is < 1 or > 5) || expected.Members.GroupBy(m => m.Squad).Any(g => g.Count() > 4))
            return new(19, "squads-unassigned");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var reservations = PlanReservations(expected, cost, room, towns, pieces, now, out var rejection);
        if (rejection is not null) return new(19, rejection);
        // 初始状态沿已解析的真实阶段规则；无奖励策略、MAP登记或计时驱动不授予对应资格。
        var battle = new AntonRaidBattle(definition.Resources.Script);
        try
        {
            return AccountOwners.Lease.ExecuteRaid(reservations.Select(r => (r.Actor.Owner, r.Expected.Id)).ToArray(), () =>
            {
                if (registry.Find(actor)?.Started is not null) return new RaidStartResult(0, "already-started", Replay: true);
                var combat = new List<Game.Session.GameSession.RaidCombatStartBinding>();
                foreach (var reservation in reservations)
                {
                    var binding = reservation.Actor.Session.PrepareRaidCombatStart(reservation.Actor, battle);
                    if (binding is null) return new RaidStartResult(19, "raid-combat-binding-conflict");
                    combat.Add(binding);
                }
                var result = registry.CommitStart(actor, expected, reservations, cost, () => parties.CreateRaidSquads(expected, () =>
                {
                    var instance = battle.Instance;
                    var committed = store.CommitRaidStart(RaidKind.Anton, definition, instance, reservations);
                    for (int i = 0; i < reservations.Length; i++)
                        reservations[i].Actor.Session.InstallRaidStart(reservations[i], committed[i]);
                    foreach (var binding in combat) binding.Install();
                    return (new RaidStarted(instance, now, expected.Members.OrderBy(m => m.Slot)
                        .Select(m => (m.Actor.Character, m.Slot, m.Squad)).ToImmutableArray()), committed);
                }));
                return result.Receipt is { } receipt ? result with { Receipt = receipt with { Battle = battle.View } } : result;
            });
        }
        catch (InvalidOperationException) when (reservations.Any(r => !r.Actor.IsCurrent))
        { return new(19, "member-owner-changed"); }
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
        var character = actor.Session.CaptureRaidStart(actor);
        long count = character?.Inventory.Items.Where(i => i.Position.List == InventoryListType.Main && i.Core.ItemId == cost.ItemId)
            .Sum(i => (long)i.Core.Count) ?? 0;
        // 客户端仅在不足状态用有符号u16比较数量；足额时尾值不参与该比较。
        return new(actor.Character, committed ? (byte)2 : count >= cost.Count ? (byte)1 : (byte)0,
            (short)Math.Min(count, short.MaxValue));
    }
}
