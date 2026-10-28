using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DungeonIncountTicketResult UseDungeonIncountTicket(InventorySlot slot, DungeonIncountTicketService service,
        IDungeonIncountTicketStore store, bool blocked = false)
    {
        var capture = owner.Execute(() => (Guard: UseGuard(), Character: CanUseConsumable() && Phase == SessionPhase.Town ? Selected : null));
        DungeonIncountTicketResult Reject(string reason) => new(ConsumableUseOutcome.Rejected, reason, capture.Guard);
        if (owner.Execute(() => pendingConsumableUse is not null))
            return new(ConsumableUseOutcome.CloseConnection, "commit-unknown", capture.Guard);
        if (blocked || capture.Character is not { } expected) return Reject("invalid-state");
        DungeonIncountTicketPlan plan;
        try { plan = service.Prepare(expected, slot); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return Reject("invalid-source-or-definition"); }
        return owner.Execute(() =>
        {
            if (pendingConsumableUse is not null || !CanPublishConsumable(capture.Guard) || !CanUseConsumable() || Phase != SessionPhase.Town)
                return new DungeonIncountTicketResult(ConsumableUseOutcome.CloseConnection, "state-changed", capture.Guard);
            var now = service.Clock.GetUtcNow();
            if (plan.Period != DungeonIncountPolicy.Period(now, plan.WeeklyResetDay) ||
                plan.Source.IsExpired(now.ToUnixTimeSeconds()) || plan.Inventory.CharacterPrecondition!.IsExpired(now.ToUnixTimeSeconds()))
                return Reject("source-or-period-expired");
            pendingConsumableUse = plan.Inventory;
            // 提交结果不明时保留门闩，关闭连接后重选只读实际存档，不能再发成功扣券。
            var committed = store.CommitDungeonIncountTicket(owner, plan, now);
            pendingConsumableUse = null;
            Selected = committed.Character;
            var inventory = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(committed.Character, owner.Generation, SelectionEpoch)
                { Phase = Phase, DungeonIdentity = formalDungeon?.Run.Snapshot.Identity,
                    PreviousQuests = expected.ActiveMainQuests, PreviousSkillSwitchInventory = expected.SkillSwitchInventory }
            };
            return new DungeonIncountTicketResult(ConsumableUseOutcome.Committed, "committed", UseGuard(), inventory,
                DungeonIncountPolicy.Views(plan.Row, committed.Usage).ToImmutableArray());
        });
    }
}
