using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DfoServer.Game.Currency.AccountWallet AccountWallet() => owner.Execute(() =>
        Selected?.Inventory.AccountWallet ?? repository.LoadAccountWallet(owner));
    public InventoryResult MutateInventory(Func<Character, InventoryResult> operation, ushort invalidStateError = 1,
        bool requireTown = true) => owner.Execute(() =>
    {
        if (!Ready || requireTown && Phase != SessionPhase.Town || Selected is null)
            return InventoryResult.Reject(invalidStateError);
        var result = operation(Selected);
        return CommitInventory(Selected, result);
    });

    InventoryResult CommitInventory(Character character, InventoryResult result)
    {
        Selected = InventoryService.Commit(repository, owner, character, result);
        return result with { Receipt = result.Accepted && result.Plan is not null
            ? new(Selected, owner.Generation, SelectionEpoch) { Phase = Phase } : null };
    }

    public bool CanPublishInventory(InventoryCommitReceipt receipt) => owner.Execute(() =>
        Ready && receipt.Generation == owner.Generation && Phase == receipt.Phase &&
        SelectionEpoch == receipt.SelectionEpoch && Selected?.Id == receipt.Character.Id &&
        ReferenceEquals(Selected, receipt.Character));

    public InventoryResult PlanAndCommitInventory(Func<Character, InventoryResult> plan, ushort invalidStateError = 1)
    {
        var (character, epoch) = owner.Execute(() => (Ready && Phase == SessionPhase.Town ? Selected : null, SelectionEpoch));
        if (character is null)
            return InventoryResult.Reject(invalidStateError);

        // 只读资源解析和批量规划不占用所属账号锁；此回调不能写库或修改在线状态。
        var result = plan(character);
        return owner.Execute(() =>
        {
            // 换角、接管及 GM/其他业务替换快照后，不能把旧随机结果提交给新的状态，也不自动重抽。
            if (!Ready || !MatchesSelection(epoch, character.Id) || !ReferenceEquals(Selected, character))
                return InventoryResult.Reject(invalidStateError);
            return CommitInventory(character, result);
        });
    }
}
