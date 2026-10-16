using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

// 开发GM在线路由：始终在本会话 owner 内执行，不顶号；当前在游戏中的角色按调用方选择拒绝或使用已提交的 Selected。
internal sealed partial class GameSession
{
    // 只读查看：当前角色返回已提交的在线快照，其他角色经本 owner 读取持久状态。
    public Character InspectCharacter(string name) => owner.Execute(() => DevelopmentTarget(name, false));

    public T WithDevelopmentOwner<T>(string name, bool refuseSelected, Func<AccountOwners.Lease, Character, T> action) =>
        owner.Execute(() => action(owner, DevelopmentTarget(name, refuseSelected)));

    // 背包与钱包写入：规划在锁内基于最新快照，Repository 复验 revision 后原子提交；成功后同步在线快照。
    public Character CommitDevelopmentInventory(string name, bool refuseSelected, Func<Character, InventoryResult> plan, string rejected) => owner.Execute(() =>
    {
        var current = DevelopmentTarget(name, refuseSelected);
        bool selected = Selected?.Id == current.Id;
        var result = plan(current);
        if (!result.Accepted)
            throw new InvalidDataException(rejected);
        // 账号钱包由同账号角色共享；会改钱包时，当前角色也须没有待确认结果，提交后替换其钱包快照。
        bool wallet = result.Plan is { } changed && !changed.Before.AccountWallet.Same(changed.After.AccountWallet);
        if ((selected || wallet && Selected is not null) && (pendingRental is not null || HasPendingFormalCard || HasPendingTownQuestCommit))
            throw new InvalidOperationException("当前角色有待确认的业务结果，请先完成或重新选角");
        var updated = InventoryService.Commit(repository, owner, current, result);
        if (selected)
            Selected = updated;
        else if (wallet && Selected is { } other)
            Selected = other with { Inventory = other.Inventory with { AccountWallet = updated.Inventory.AccountWallet } };
        return updated;
    });

    Character DevelopmentTarget(string name, bool refuseSelected)
    {
        if (Selected is { } selected && string.Equals(selected.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            if (refuseSelected)
                throw new InvalidOperationException("角色正在游戏中，客户端背包或邮箱不会即时刷新；请先回到选角再执行");
            return selected;
        }
        return repository.LoadCharacter(owner, repository.ListRoster(owner).SingleOrDefault(row =>
            string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))?.Slot ?? throw new InvalidDataException("角色不存在"));
    }
}
