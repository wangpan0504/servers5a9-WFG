using DfoServer.Game.Characters;
using DfoServer.Game.Skills;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public SkillSwitchInventoryResult UpdateSkillSwitchInventory(
        SkillSwitchInventoryRequest request,
        SkillSwitchInventoryService service)
    {
        var captured = owner.Execute(() => (
            Character: Selected,
            SelectionEpoch,
            Phase,
            Allowed: Ready && Phase == SessionPhase.Town && pendingRental is null &&
                !HasPendingTownQuestCommit && !HasClassExperienceContext));
        if (!captured.Allowed || captured.Character is not { } character)
            return new(SkillSwitchInventoryFailure.UnsupportedState);
        SkillSwitchInventoryResult planned = service.Plan(character, request);
        return owner.Execute(() =>
        {
            if (!Ready || Phase != captured.Phase || Phase != SessionPhase.Town ||
                pendingRental is not null || HasPendingTownQuestCommit || HasClassExperienceContext ||
                SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, character))
                throw new InvalidOperationException("Buff强化登记规划状态已变化");
            Character committed = character;
            if (planned.Accepted)
            {
                var plan = planned.Plan ?? throw new InvalidOperationException("Buff强化登记成功结果缺少规划");
                if (plan.Changed)
                    committed = repository.CommitSkillSwitchInventory(owner, plan);
                Selected = committed;
            }
            var receipt = new SkillSwitchInventoryReceipt(
                committed,
                character.SkillSwitchInventory,
                owner.Generation,
                SelectionEpoch,
                Phase);
            return planned with { Receipt = receipt };
        });
    }

    Character ReconcileSkillSwitchInventory(Character character)
    {
        SkillSwitchInventoryState state =
            character.SkillSwitchInventory.Reconcile(character.Inventory);
        if (state.Same(character.SkillSwitchInventory))
            return character;
        return repository.CommitSkillSwitchInventory(owner,
            new(character, state, null, "skill_switch_selection_reconcile"));
    }

    public bool CanPublishSkillSwitchInventory(SkillSwitchInventoryReceipt receipt) => owner.Execute(() =>
        Ready && receipt.Generation == owner.Generation && Phase == receipt.Phase &&
        SelectionEpoch == receipt.SelectionEpoch && Selected?.Id == receipt.Character.Id &&
        ReferenceEquals(Selected, receipt.Character));
}
