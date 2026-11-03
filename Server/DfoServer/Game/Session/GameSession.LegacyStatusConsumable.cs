using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public ConsumableUseResult UseLegacyStatusConsumable(InventorySlot slot, LegacyStatusConsumableService service, bool blocked = false)
    {
        var capture = owner.Execute(() => (Guard: UseGuard(), Character: CanUseConsumable() ? Selected : null));
        ConsumableUseResult Reject(string reason) => new(ConsumableUseOutcome.Rejected, reason, Guard: capture.Guard);
        if (owner.Execute(() => pendingConsumableUse is not null))
            return new(ConsumableUseOutcome.CloseConnection, "commit-unknown", Guard: capture.Guard);
        if (blocked || capture.Character is not { } expected) return Reject("invalid-state");
        if (!CanUseStackableItem(expected, expected.Inventory.Find(slot)?.Core.ItemId ?? 0)) return Reject("usable-qualification");
        LegacyStatusConsumablePlan plan;
        try { plan = service.Prepare(expected, slot); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return Reject("invalid-source-resource-or-capacity"); }
        return owner.Execute(() =>
        {
            if (pendingConsumableUse is not null)
                return new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "commit-unknown", Guard: capture.Guard);
            if (!CanUseConsumable() || !CanPublishConsumable(capture.Guard))
                return new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "state-changed", Guard: capture.Guard);
            if (!CanUseDungeonItem(plan.Source.Core.ItemId)) return Reject("grief-item-not-allowed-or-exhausted");
            var now = service.Clock.GetUtcNow();
            if (!CanUseStackableItem(expected, plan.Source.Core.ItemId, places: plan.Definition.Places)) return Reject("usable-qualification");
            if (usedConsumables.TryGetValue(plan.Definition.CoolGroup, out var previous) && previous.EnforceCooldown &&
                previous.Clock.GetElapsedTime(previous.Timestamp).TotalMilliseconds < plan.Definition.CoolTime)
                return Reject("cooldown");
            if (plan.Source.IsExpired(now.ToUnixTimeSeconds())) return Reject("source-expired");
            if (itemCycles is not null)
            {
                var limited = itemCycles.ApplyUsage(expected, new(true, 0, plan.Inventory), 0, plan.Source.Core.ItemId, 1);
                if (!limited.Accepted) return Reject("server-item-usage-limit");
                plan = plan with { Inventory = limited.Plan! };
            }
            // 钱包奖励必须实际扣物，不沿练习战斗道具的免耗分支。
            pendingConsumableUse = plan.Inventory;
            var committed = repository.CommitLegacyStatusConsumable(owner, plan, now);
            pendingConsumableUse = null;
            Selected = committed;
            CommitDungeonItemUse(plan.Source.Core.ItemId);
            if (plan.Definition.CoolTime > 0)
                usedConsumables[plan.Definition.CoolGroup] = new(null, plan.Source.Uid, service.Clock.GetTimestamp(), plan.Definition.CoolTime, service.Clock);
            var inventory = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(committed, owner.Generation, SelectionEpoch)
                {
                    Phase = Phase, DungeonIdentity = formalDungeon?.Run.Snapshot.Identity,
                    PreviousQuests = expected.ActiveMainQuests, PreviousSkillSwitchInventory = expected.SkillSwitchInventory
                }
            };
            return new ConsumableUseResult(ConsumableUseOutcome.Committed, "committed", inventory, UseGuard())
                { SourceUid = plan.Source.Uid, LegacyStatus = plan };
        });
    }
}
