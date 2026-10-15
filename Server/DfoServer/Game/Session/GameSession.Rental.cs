using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Rental;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    private sealed record PendingRental(QuestLifecyclePlan Plan, InventoryResult Result, int ItemId,
        ushort Job, ushort Tier, long Generation, long SelectionEpoch);
    private PendingRental? pendingRental;

    public InventoryResult RentEquipment(RentalService rental, int itemId, ushort job, ushort tier, DateTimeOffset now)
    {
        var capture = owner.Execute(() => (Character: CanRent() ? Selected : null, SelectionEpoch, Pending: pendingRental));
        if (capture.Character is not { } expected) return InventoryResult.Reject();
        InventoryResult result;
        QuestLifecyclePlan? plan;
        if (capture.Pending is { } pending)
        {
            // 返回丢失只能查原冻结operation，不重算期限、槽位、费用或新接取。
            if (pending.ItemId != itemId || pending.Job != job || pending.Tier != tier ||
                pending.Generation != owner.Generation || pending.SelectionEpoch != capture.SelectionEpoch ||
                !ReferenceEquals(pending.Plan.Expected, expected))
                return InventoryResult.Reject();
            result = pending.Result;
            plan = pending.Plan;
        }
        else
        {
            result = rental.Rent(expected, itemId, job, tier, now);
            plan = result.Accepted && result.Plan is { } inventory && questCatalog is not null
                ? QuestLifecyclePlan.ObserveRental(expected, inventory, questCatalog, now) : null;
        }
        return owner.Execute(() =>
        {
            if (!CanRent() || SelectionEpoch != capture.SelectionEpoch || !ReferenceEquals(Selected, expected) ||
                !ReferenceEquals(pendingRental, capture.Pending))
                return InventoryResult.Reject();
            if (plan is null) return CommitInventory(expected, result);
            pendingRental ??= new(plan, result, itemId, job, tier, owner.Generation, capture.SelectionEpoch);
            var commit = repository.CommitQuestLifecycle(owner, plan);
            // 旧回执只证明原事务已提交；当前状态继续变化时不能安装或发布旧增量。
            if (!commit.ProjectionCurrent) return InventoryResult.Reject();
            Selected = commit.Character;
            completed = QuestEligibility.Completed(commit.Character);
            pendingRental = null;
            return result with { Receipt = new(commit.Character, owner.Generation, capture.SelectionEpoch)
                { Phase = SessionPhase.Town, PreviousQuests = expected.ActiveMainQuests } };
        });

        bool CanRent() => pendingCommunication is null && Ready && Phase == SessionPhase.Town && Selected is not null && !HasPendingQuestScene &&
            !HasPendingFormalCard && pendingQuestCompletion is null && pendingQuestLifecycle is null &&
            pendingQuestQuickClear is null && pendingDungeonContinuation is null && !HasPendingQuestReplay &&
            formalDungeon?.PendingTutorialClear is null && formalDungeon?.PendingInventoryDiscard is null;
    }
}
