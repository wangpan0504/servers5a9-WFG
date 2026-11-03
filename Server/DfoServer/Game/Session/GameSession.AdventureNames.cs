using DfoServer.Game.Adventure;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public string ReadAdventureName() => repository.ReadAdventureName(owner);
    public bool AdventureNameExists(string name) => repository.AdventureNameExists(owner, name);

    public AdventureNameResult ChangeAdventureName(AdventureNameService service, string name, bool initial,
        ushort slot, ushort mode, DateTimeOffset now)
    {
        bool CanPlan() => CheckState(UserState.CharacterSelect) && initial ||
            Ready && Phase == SessionPhase.Town && CanPlanAdventureRepresentative();
        var capture = owner.Execute(() => (Allowed: CanPlan(), Selected, SelectionEpoch, Phase,
            Snapshot: repository.ReadAdventure(owner, service.Catalog, now)));
        if (!capture.Allowed) return new(false, initial ? (ushort)3 : (ushort)19);
        int sourceId = !initial && capture.Selected is { } sourceCharacter
            ? sourceCharacter.Inventory.Find(new(InventoryListType.Main, unchecked((short)slot)))?.Core.ItemId ?? 0 : 0;
        if (!initial && capture.Selected is { } character && !CanUseStackableItem(character, sourceId)) return new(false, 19);
        var prepared = service.Prepare(capture.Snapshot, capture.Selected, name, initial, slot, mode, now);
        if (prepared.Plan is not { } plan) return new(false, prepared.Error);
        return owner.Execute(() =>
        {
            if (!CanPlan() || !ReferenceEquals(Selected, capture.Selected) || SelectionEpoch != capture.SelectionEpoch || Phase != capture.Phase)
                return new AdventureNameResult(false, 19);
            if (!initial && capture.Selected is { } current && !CanUseStackableItem(current, sourceId))
                return new AdventureNameResult(false, 19);
            var committed = repository.CommitAdventureName(owner, plan, service.Catalog, now);
            if (!committed.Accepted) return new(false, committed.Error);
            InventoryResult? inventory = null;
            if (plan.Inventory is { } changed)
            {
                Selected = committed.Character!;
                inventory = new(true, 0, changed)
                {
                    Receipt = new(Selected, owner.Generation, SelectionEpoch)
                        {
                            Phase = Phase,
                            PreviousQuests = capture.Selected!.ActiveMainQuests,
                            PreviousSkillSwitchInventory = capture.Selected!.SkillSwitchInventory
                        }
                };
            }
            return new AdventureNameResult(true, 0, committed.Snapshot, inventory)
            {
                Changed = plan.After != plan.Before.State, Selected = Selected, Generation = owner.Generation,
                Epoch = SelectionEpoch, Phase = Phase
            };
        });
    }

    public bool CanPublishAdventureName(AdventureNameResult result) => owner.Execute(() => result.Accepted &&
        result.Generation == owner.Generation && result.Epoch == SelectionEpoch && result.Phase == Phase &&
        ReferenceEquals(result.Selected, Selected));
}
