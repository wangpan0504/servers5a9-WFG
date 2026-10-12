using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DfoServer.Game.Currency.AccountWallet AccountWallet() => owner.Execute(() =>
        Selected?.Inventory.AccountWallet ?? repository.LoadAccountWallet(owner));
    public InventoryResult DeleteInventory(InventoryDeleteRequest request, InventoryService inventory) => MutateInventory(
        character => inventory.Delete(character,request,PreservesTrainingItems),
        requireTown: request.List is InventoryListType.PersonalCargo or InventoryListType.AccountCargo);
    public InventoryResult MutateInventory(Func<Character, InventoryResult> operation, ushort invalidStateError = 1,
        bool requireTown = true) => owner.Execute(() =>
    {
        if (!Ready || requireTown && Phase != SessionPhase.Town || Selected is null || HasPendingQuestScene || formalDungeon?.PendingTutorialClear is not null)
            return InventoryResult.Reject(invalidStateError);
        var result = operation(Selected);
        return CommitInventory(Selected, result);
    });

    InventoryResult CommitInventory(Character character, InventoryResult result)
    {
        Selected = InventoryService.Commit(repository, owner, character, result);
        return result with { Receipt = result.Accepted && result.Plan is not null
            ? new(Selected, owner.Generation, SelectionEpoch)
                { Phase = Phase, DungeonIdentity = formalDungeon?.Run.Snapshot.Identity, PreviousQuests = character.ActiveMainQuests } : null };
    }

    public bool CanPublishInventory(InventoryCommitReceipt receipt) => owner.Execute(() =>
        Ready && receipt.Generation == owner.Generation && Phase == receipt.Phase &&
        receipt.DungeonIdentity == formalDungeon?.Run.Snapshot.Identity &&
        SelectionEpoch == receipt.SelectionEpoch && Selected?.Id == receipt.Character.Id &&
        ReferenceEquals(Selected, receipt.Character));

    public InventoryResult PlanAndCommitInventory(Func<Character, InventoryResult> plan, ushort invalidStateError = 1,
        bool requireTown = true, SessionPhase? requiredPhase = null) => PlanAndCommitInventory(plan, invalidStateError, requireTown, false, requiredPhase);

    public InventoryResult RepairEquipment(RepairEquipmentRequest request, InventoryRepairService repair) =>
        PlanAndCommitInventory(character => repair.Repair(character, request), 19, requireTown: true, allowSettlement: true);

    public InventoryResult DisjointEquipment(DisjointRequest request, InventoryDisjointService disjoint, out DisjointReceipt? receipt)
    {
        DisjointReceipt? plannedReceipt = null;
        var result = PlanAndCommitInventory(character => disjoint.Disjoint(character, request, out plannedReceipt), 19,
            requireTown: true, allowSettlement: true);
        receipt = result.Accepted ? plannedReceipt : null;
        return result;
    }

    InventoryResult PlanAndCommitInventory(Func<Character, InventoryResult> plan, ushort invalidStateError,
        bool requireTown, bool allowSettlement, SessionPhase? requiredPhase = null)
    {
        var capture = owner.Execute(() => (Character: CanPlan() ? Selected : null,
            SelectionEpoch, Phase, Dungeon: formalDungeon, Identity: formalDungeon?.Run.Snapshot.Identity));
        if (capture.Character is not { } character)
            return InventoryResult.Reject(invalidStateError);

        // 只读资源解析和批量规划不占用所属账号锁；此回调不能写库或修改在线状态。
        var result = plan(character);
        return owner.Execute(() =>
        {
            // 换角、接管及 GM/其他业务替换快照后，不能把旧随机结果提交给新的状态，也不自动重抽。
            if (!CanPlan() || SelectionEpoch != capture.SelectionEpoch || Phase != capture.Phase ||
                !ReferenceEquals(formalDungeon, capture.Dungeon) || formalDungeon?.Run.Snapshot.Identity != capture.Identity ||
                !ReferenceEquals(Selected, character))
                return InventoryResult.Reject(invalidStateError);
            return CommitInventory(character, result);
        });

        bool CanPlan()
        {
            if (!Ready || Selected is null || HasPendingQuestScene ||
                requiredPhase is { } expected && Phase != expected || formalDungeon?.PendingTutorialClear is not null) return false;
            // 既有城镇用例保持原资格；只有专用跨场景取放核对未完成事务与副本上下文。
            if (requireTown) return Phase == SessionPhase.Town || allowSettlement && CanUseSettlementNpcServices();
            return !HasPendingQuestScene && pendingQuestCompletion is null && pendingQuestLifecycle is null &&
                pendingQuestQuickClear is null && pendingDungeonContinuation is null && !HasPendingQuestReplay &&
                formalDungeon?.Ground.HasPendingPickup != true &&
                (Phase == SessionPhase.Town || Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
                    formalDungeon is { HasPendingFatigue: false } dungeon && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement);
        }
    }
}
