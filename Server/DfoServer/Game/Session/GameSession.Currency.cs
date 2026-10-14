using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public Character SetCharacterGold(string name, uint amount) => owner.Execute(() =>
    {
        if (pendingRental is not null)
            throw new InvalidOperationException("租赁结果待确认，须恢复原提交或重新选角");
        if (HasPendingFormalCard && string.Equals(Selected?.Name, name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("翻牌结果待确认，不能覆盖当前角色金币");
        var current = Selected is { } selected && string.Equals(selected.Name, name, StringComparison.OrdinalIgnoreCase) ? selected :
            repository.LoadCharacter(owner, repository.ListRoster(owner).SingleOrDefault(row =>
                string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))?.Slot ?? throw new InvalidDataException("角色不存在"));
        var result = CurrencyService.SetGold(current, amount);
        if (!result.Accepted)
            throw new InvalidDataException("金币金额越界");
        var updated = InventoryService.Commit(repository, owner, current, result);
        // 与进度编辑一致，提交后安装在线快照，客户端等再次选角刷新。
        if (Selected?.Id == updated.Id)
            Selected = updated;
        return updated;
    });
}
