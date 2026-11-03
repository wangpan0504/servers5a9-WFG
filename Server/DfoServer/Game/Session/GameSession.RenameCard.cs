using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryCommitReceipt? UseCharacterRenameCard(ushort slot, ItemCatalog items, TimeProvider clock, bool blocked)
    {
        if (blocked || slot > short.MaxValue || repository is not ICharacterRenameCardStore store) return null;
        var capture = CaptureInventoryPlan(requireTown: true);
        if (capture is null) return null;
        int sourceId = capture.Character.Inventory.Find(new(InventoryListType.Main, (short)slot))?.Core.ItemId ?? 0;
        if (!CanUseStackableItem(capture.Character, sourceId, items)) return null;
        string place = towns.IsRoom(capture.Character.Position, Game.Quests.QuestEligibility.Completed(capture.Character)) ? "[seria room]" : "[village]";
        // 资源读取在锁外，回锁只接受原owner、选角、阶段和完整角色快照。
        var planned = CharacterRenameCard.Prepare(capture.Character, slot, items, place, clock);
        if (!planned.Accepted || planned.Plan is not { } inventory) return null;
        return owner.Execute<InventoryCommitReceipt?>(() =>
        {
            if (!InventoryPlanIsCurrent(capture, requireTown: true) || !CanUseStackableItem(capture.Character, sourceId, items)) return null;
            var committed = store.ActivateRenameCard(owner, capture.Character, inventory);
            if (committed is null) return null;
            Selected = committed;
            return InventoryReceipt(capture.Character);
        });
    }

    public bool RenameCardSelectionBlocked(ushort index) => owner.Execute(() => PublishedRosterEntry(index)?.RenamePending == true);

    public CharacterRenameFailure CompleteCharacterRename(byte index, string oldName, string newName, byte reason) => owner.Execute(() =>
    {
        if (Selected is not null || Phase != SessionPhase.Unselected || reason != 1 ||
            repository is not ICharacterRenameCardStore store || PublishedRosterEntry(index) is not { RenamePending: true } entry)
            return CharacterRenameFailure.Unavailable;
        if (!CharacterName.IsValid(newName)) return CharacterRenameFailure.InvalidName;
        var current = repository.LoadCharacter(owner, entry.Slot);
        if (current.Id != entry.Id || current.Name != entry.Name || oldName != current.Name)
            return CharacterRenameFailure.Unavailable;
        var result = store.CompleteRenameCard(owner, current, newName);
        if (result == CharacterRenameFailure.None) SelectionEpoch++;
        return result;
    });
}
