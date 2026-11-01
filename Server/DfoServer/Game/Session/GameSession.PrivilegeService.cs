using DfoServer.Game.Inventory.Models;
using DfoServer.Game.PrivilegeServices;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public void InitializePrivilegeService()
    {
        var captured = owner.Execute(() => (Character: Selected, SelectionEpoch, Ready, Phase));
        if (captured.Character is not { } character) return;
        var result = PrivilegeServicePolicy.Login(character, timeProvider ?? TimeProvider.System);
        if (result.Plan is not { Changed: true }) return;
        owner.Execute(() =>
        {
            if (Ready != captured.Ready || Phase != captured.Phase || SelectionEpoch != captured.SelectionEpoch ||
                !ReferenceEquals(Selected, character)) return false;
            CommitInventory(character, result, 19);
            return true;
        });
    }

    public InventoryResult ClaimPrivilegeReward(PrivilegeServiceRewards rewards, uint mode, uint value) =>
        PlanAndCommitInventory(character => rewards.Claim(character, mode, value), 19,
            requireTown: false, allowSettlement: false);

    public PrivilegeServiceSnapshot? CapturePrivilegeService(bool initialization = false) => owner.Execute(() =>
        (Ready || initialization && Phase == SessionPhase.Unselected) && Selected is { } character && repository is IPrivilegeServiceRoster roster
            ? new PrivilegeServiceSnapshot(InventoryReceipt(character), (timeProvider ?? TimeProvider.System).GetUtcNow(),
                roster.ReadPrivilegeServiceRoster(owner)) : null);

    public bool CanPublishPrivilegeService(PrivilegeServiceSnapshot snapshot, bool initialization) => owner.Execute(() =>
        (Ready || initialization && Phase == SessionPhase.Unselected) &&
        snapshot.Receipt.Generation == owner.Generation && Phase == snapshot.Receipt.Phase &&
        SelectionEpoch == snapshot.Receipt.SelectionEpoch && ReferenceEquals(Selected, snapshot.Receipt.Character));
}
