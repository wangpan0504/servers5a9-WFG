using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public ConsumableUseResult UseExperienceConsumable(InventorySlot slot, ExperienceConsumableService service, bool blocked = false)
    {
        var capture = owner.Execute(() => (Guard: UseGuard(), Character: CanUseConsumable() ? Selected : null));
        ConsumableUseResult Reject(string reason) => new(ConsumableUseOutcome.Rejected, reason, Guard: capture.Guard);
        if (owner.Execute(() => pendingConsumableUse is not null))
            return new(ConsumableUseOutcome.CloseConnection, "commit-unknown", Guard: capture.Guard);
        if (blocked || capture.Character is not { } expected) return Reject("invalid-state");
        ExperienceConsumablePlan plan;
        try { plan = service.Prepare(expected, slot); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return Reject("invalid-source-or-resource"); }
        return owner.Execute(() =>
        {
            var now = service.Clock.GetUtcNow();
            if (pendingConsumableUse is not null)
                return new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "commit-unknown", Guard: capture.Guard);
            if (!CanUseConsumable() || !CanPublishConsumable(capture.Guard))
                return new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "state-changed", Guard: capture.Guard);
            if (!CanUseDungeonItem(plan.Source.Core.ItemId)) return Reject("grief-item-not-allowed-or-exhausted");
            if (plan.Definition.VillageOnly && Phase != SessionPhase.Town) return Reject("village-only");
            if (plan.Definition.SeriaRoomOnly && !towns.IsRoom(expected.Position, QuestEligibility.Completed(expected)))
                return Reject("seria-room-only");
            if (usedConsumables.TryGetValue(plan.Definition.CoolGroup, out var previous) && previous.EnforceCooldown &&
                previous.Clock.GetElapsedTime(previous.Timestamp).TotalMilliseconds < plan.Definition.CoolTime)
                return Reject("cooldown");
            if (plan.Inventory.ChangedSlots.Any(position => plan.Inventory.Before.Find(position) is { } item && item.IsExpired(now.ToUnixTimeSeconds())))
                return Reject("source-or-material-expired");
            if (itemCycles is not null)
            {
                var limited = itemCycles.ApplyUsage(expected, new(true, 0, plan.Inventory), 0, plan.Source.Core.ItemId, 1);
                if (!limited.Accepted) return Reject("server-item-usage-limit");
                plan = plan with { Inventory = limited.Plan! };
            }
            pendingConsumableUse = plan.Inventory;
            // 提交结果不明时保留原未决标记，由原连接关闭后重新装载；不重做扣物或补发增量。
            var character = repository.CommitExperienceConsumable(owner, plan, now);
            pendingConsumableUse = null;
            Selected = character;
            CommitDungeonItemUse(plan.Source.Core.ItemId);
            if (plan.Definition.CoolTime > 0)
                usedConsumables[plan.Definition.CoolGroup] = new(null, plan.Source.Uid, service.Clock.GetTimestamp(), plan.Definition.CoolTime, service.Clock);
            var inventory = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(character, owner.Generation, SelectionEpoch)
                {
                    Phase = Phase,
                    DungeonIdentity = formalDungeon?.Run.Snapshot.Identity,
                    PreviousQuests = expected.ActiveMainQuests,
                    PreviousSkillSwitchInventory = expected.SkillSwitchInventory
                }
            };
            return new ConsumableUseResult(ConsumableUseOutcome.Committed, "committed", inventory, UseGuard())
                { SourceUid = plan.Source.Uid, Experience = plan };
        });
    }
}
