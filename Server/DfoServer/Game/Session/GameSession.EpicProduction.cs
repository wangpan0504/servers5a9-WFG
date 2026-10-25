using DfoServer.Game.Characters;
using DfoServer.Game.EpicProduction;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public EpicProductionCommit? PlanAndCommitEpicProduction(Func<Character,EpicProductionPlan?> plan)
    {
        var capture=owner.Execute(()=>(Character:CanPlan()?Selected:null,SelectionEpoch,Phase));
        if (capture.Character is not { } character) return null;
        var pending=plan(character);
        return owner.Execute(()=>
        {
            if (pending is null || !CanPlan() || capture.SelectionEpoch!=SelectionEpoch || capture.Phase!=Phase ||
                !ReferenceEquals(Selected,character) || !ReferenceEquals(pending.Expected,character)) return null;
            Selected=repository.CommitEpicProduction(owner,pending);
            var receipt=new InventoryCommitReceipt(Selected,owner.Generation,SelectionEpoch)
            {
                Phase=Phase,PreviousQuests=character.ActiveMainQuests,PreviousSkillSwitchInventory=character.SkillSwitchInventory
            };
            return new EpicProductionCommit(pending,Selected,owner.Generation,SelectionEpoch,
                new InventoryResult(true,0,pending.Inventory) { Receipt=receipt });
        });
        bool CanPlan()=>Ready && Phase==SessionPhase.Town && Selected is not null && pendingRental is null &&
            !HasPendingTownQuestCommit && !HasPendingFormalCard && formalDungeon?.PendingTutorialClear is null &&
            formalDungeon?.PendingInventoryDiscard is null;
    }
    public bool CanPublishEpicProduction(EpicProductionCommit receipt)=>owner.Execute(()=>Ready && Phase==SessionPhase.Town &&
        receipt.Generation==owner.Generation && receipt.SelectionEpoch==SelectionEpoch && ReferenceEquals(Selected,receipt.Character));
}
