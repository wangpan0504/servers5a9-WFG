using DfoServer.Game.Characters;
using DfoServer.Game.CollectBox;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public CollectBoxResult PlanAndCommitCollectBox(Func<Character, CollectBoxResult> plan, ushort invalidStateError)
    {
        var capture = owner.Execute(() => (Character: CanPlan() ? Selected : null, SelectionEpoch, Phase));
        if (capture.Character is not { } character)
            return CollectBoxResult.Reject(invalidStateError);

        var result = plan(character);
        return owner.Execute(() =>
        {
            if (!CanPlan() || SelectionEpoch != capture.SelectionEpoch || Phase != capture.Phase ||
                !ReferenceEquals(Selected, character))
                return CollectBoxResult.Reject(invalidStateError);
            if (!result.Accepted)
                return result;
            var collectBoxPlan = result.Plan ?? throw new InvalidOperationException("宝珠收集箱成功结果缺少提交规划");
            Selected = repository.CommitCollectBox(owner, collectBoxPlan);
            var receipt = new CollectBoxCommitReceipt(Selected, owner.Generation, SelectionEpoch);
            var inventoryReceipt = new InventoryCommitReceipt(Selected, owner.Generation, SelectionEpoch)
            {
                Phase = Phase,
                PreviousQuests = character.ActiveMainQuests,
                PreviousSkillSwitchInventory = character.SkillSwitchInventory
            };
            return result with
            {
                Receipt = receipt,
                Inventory = result.Inventory with { Receipt = inventoryReceipt }
            };
        });

        bool CanPlan() => pendingRental is null && Ready && Phase == SessionPhase.Town && Selected is not null &&
            !HasPendingTownQuestCommit && !HasPendingFormalCard && formalDungeon?.PendingTutorialClear is null &&
            formalDungeon?.PendingInventoryDiscard is null;
    }

    public bool CanPublishCollectBox(CollectBoxCommitReceipt receipt) => owner.Execute(() =>
        Ready && Phase == SessionPhase.Town && receipt.Generation == owner.Generation &&
        receipt.SelectionEpoch == SelectionEpoch && Selected?.Id == receipt.Character.Id &&
        ReferenceEquals(Selected, receipt.Character));
}
