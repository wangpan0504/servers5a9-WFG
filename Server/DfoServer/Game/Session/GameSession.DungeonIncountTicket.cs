using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DungeonIncountTicketResult UseDungeonIncountTicket(InventorySlot slot, DungeonIncountTicketService service,
        IDungeonIncountTicketStore store, bool blocked = false, uint? requestedAction = null)
    {
        var capture = owner.Execute(() => (Guard: UseGuard(), Character: CanUseConsumable() && Phase == SessionPhase.Town ? Selected : null));
        DungeonIncountTicketResult Reject(string reason) => new(ConsumableUseOutcome.Rejected, reason, capture.Guard);
        if (owner.Execute(() => pendingConsumableUse is not null))
            return new(ConsumableUseOutcome.CloseConnection, "commit-unknown", capture.Guard);
        if (blocked || capture.Character is not { } expected) return Reject("invalid-state");
        int sourceId = expected.Inventory.Find(slot)?.Core.ItemId ?? 0;
        if (!CanUseStackableItem(expected, sourceId)) return Reject("usable-place");
        DungeonIncountTicketPlan plan;
        try { plan = service.Prepare(expected, slot, requestedAction); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        { return Reject("invalid-source-or-definition"); }
        return owner.Execute(() =>
        {
            if (pendingConsumableUse is not null || !CanPublishConsumable(capture.Guard) || !CanUseConsumable() || Phase != SessionPhase.Town)
                return new DungeonIncountTicketResult(ConsumableUseOutcome.CloseConnection, "state-changed", capture.Guard);
            if (!CanUseStackableItem(expected, sourceId)) return Reject("usable-place");
            var now = service.Clock.GetUtcNow();
            if (plan.Period != DungeonIncountPolicy.Period(now, plan.WeeklyResetDay) ||
                plan.Source.IsExpired(now.ToUnixTimeSeconds()) || plan.Inventory.CharacterPrecondition!.IsExpired(now.ToUnixTimeSeconds()))
                return Reject("source-or-period-expired");
            if (plan.Reset && plan.Row is { } resetRow)
            {
                var usage = repository.LoadDungeonIncount(owner, expected.Id, resetRow.CountKey, plan.Period);
                if (DungeonIncountPolicy.DailyRemaining(resetRow, usage) > 0)
                    return Reject("reset-requires-exhausted-count");
            }
            pendingConsumableUse = plan.Inventory;
            // 提交结果不明时保留门闩，关闭连接后重选只读实际存档，不能再发成功扣券。
            DungeonIncountCommit committed;
            try { committed = store.CommitDungeonIncountTicket(owner, plan, now); }
            catch (DungeonTicketRejectedException rejected)
            {
                pendingConsumableUse = null;
                return Reject(rejected.Message);
            }
            catch (InventoryQualificationExpiredException)
            {
                pendingConsumableUse = null;
                return Reject("source-or-period-expired");
            }
            pendingConsumableUse = null;
            Selected = committed.Character;
            var inventory = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(committed.Character, owner.Generation, SelectionEpoch)
                { Phase = Phase, DungeonIdentity = formalDungeon?.Run.Snapshot.Identity,
                    PreviousQuests = expected.ActiveMainQuests, PreviousSkillSwitchInventory = expected.SkillSwitchInventory }
            };
            return new DungeonIncountTicketResult(ConsumableUseOutcome.Committed, "committed", UseGuard(), inventory,
                plan.Row is { } row ? DungeonIncountPolicy.Views(row, committed.Usage).ToImmutableArray() : []);
        });
    }
}
