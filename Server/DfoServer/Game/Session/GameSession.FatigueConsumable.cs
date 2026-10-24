using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public ConsumableUseResult UseFatigueConsumable(InventorySlot slot, FatigueConsumableService service, bool blocked = false)
    {
        var capture = owner.Execute(() => (Guard: UseGuard(), Character: CanUseConsumable() ? Selected : null,
            InAssault: formalDungeon?.Assault is not null));
        ConsumableUseResult Reject(string reason) => new(ConsumableUseOutcome.Rejected, reason, Guard: capture.Guard);
        if (owner.Execute(() => pendingConsumableUse is not null))
            return new(ConsumableUseOutcome.CloseConnection, "commit-unknown", Guard: capture.Guard);
        if (blocked || capture.Character is not { } expected) return Reject("invalid-state");
        FatigueConsumablePlan plan;
        try { plan = service.Prepare(expected, slot, capture.Guard.Phase is SessionPhase.Dungeon or SessionPhase.Settlement, capture.InAssault); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return Reject("invalid-source-fatigue-or-cooldown"); }
        return owner.Execute(() =>
        {
            if (pendingConsumableUse is not null)
                return new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "commit-unknown", Guard: capture.Guard);
            if (!CanUseConsumable() || !CanPublishConsumable(capture.Guard))
                return new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "state-changed", Guard: capture.Guard);
            if (!CanUseDungeonItem(plan.Source.Core.ItemId)) return Reject("grief-item-not-allowed-or-exhausted");
            var now = service.Clock.GetUtcNow();
            if (usedConsumables.TryGetValue(plan.Definition.CoolGroup, out var previous) && previous.EnforceCooldown &&
                previous.Clock.GetElapsedTime(previous.Timestamp).TotalMilliseconds < plan.Definition.CoolTime)
                return Reject("cooldown");
            if (plan.Source.IsExpired(now.ToUnixTimeSeconds()) || plan.Inventory.CharacterPrecondition!.IsExpired(now.ToUnixTimeSeconds()))
                return Reject("source-or-quota-expired");
            pendingConsumableUse = plan.Inventory;
            var committed = repository.CommitFatigueConsumable(owner, plan, now);
            pendingConsumableUse = null;
            Selected = committed;
            CommitDungeonItemUse(plan.Source.Core.ItemId);
            if (plan.Definition.CoolTime > 0)
                usedConsumables[plan.Definition.CoolGroup] = new(null, plan.Source.Uid, service.Clock.GetTimestamp(), plan.Definition.CoolTime, service.Clock)
                    { Maintenance = plan.Definition.CooltimeMaintenance };
            var inventory = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(committed, owner.Generation, SelectionEpoch) { Phase = Phase, DungeonIdentity = formalDungeon?.Run.Snapshot.Identity,
                    PreviousQuests = expected.ActiveMainQuests, PreviousSkillSwitchInventory = expected.SkillSwitchInventory }
            };
            return new ConsumableUseResult(ConsumableUseOutcome.Committed, "committed", inventory, UseGuard())
                { SourceUid = plan.Source.Uid, FatigueRecovery = plan };
        });
    }
}
