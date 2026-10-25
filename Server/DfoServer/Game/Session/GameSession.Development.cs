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

    // 选角名册变更（改名/删除）：角色不能在游戏中；提交后推进选角纪元，使客户端已发布的旧名册作废。
    public T CommitRosterChange<T>(string name, Func<AccountOwners.Lease, Character, T> action) => owner.Execute(() =>
    {
        if (Selected is not null)
            throw new InvalidOperationException("角色正在游戏中，客户端名册不会即时刷新；请先回到选角再执行");
        var result = action(owner, DevelopmentTarget(name, true));
        SelectionEpoch++;
        return result;
    });

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

    // 由连接层安装：在该连接的命令闸门内走与客户端“返回选角”相同的投影流程（发 ACK 与角色列表）。
    public Func<Task>? ReturnToSelectionRequested { private get; set; }

    // GM 强制小退：只对当前正在游戏的角色生效（name 为空则不限角色）；返回是否真的回到了选角。
    public async Task<bool> ForceReturnToSelectionAsync(string? name = null)
    {
        Func<Task>? hook;
        try
        {
            hook = owner.Execute(() => Selected is { } current &&
                (name is null || string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)) ? ReturnToSelectionRequested : null);
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return false; }
        if (hook is null) return false;
        await hook();
        return true;
    }

    // GM 断开连接：与同账号重复登录（ReplaceExisting）使用同一条断线回调；连接清理后会话自行出目录。
    public bool Disconnect()
    {
        try { owner.Disconnect(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    // 称号成就提交：在本会话 owner 内基于最新快照规划并提交；目标是当前在游戏角色时同步内存 Selected（客户端画面等下次选角刷新）。
    public T CommitDevelopmentTitle<T>(string name, Func<AccountOwners.Lease, Character, (Character After, T Value)> action) => owner.Execute(() =>
    {
        var current = DevelopmentTarget(name, false);
        bool selected = Selected?.Id == current.Id;
        if (selected && (pendingRental is not null || HasPendingFormalCard || HasPendingTownQuestCommit))
            throw new InvalidOperationException("当前角色有待确认的业务结果，请先完成或重新选角");
        var (after, value) = action(owner, current);
        if (selected)
        {
            InvalidateImageCommunication();
            Selected = after;
        }
        return value;
    });

    // 副职业类型与累计熟练度：GM 写入后同步在线快照；客户端等级按累计熟练度换算，需回到选角再进入才刷新显示。
    public Character SetExpertJob(string name, byte type, int experience) => owner.Execute(() =>
    {
        var current = DevelopmentTarget(name, false);
        bool selected = Selected?.Id == current.Id;
        if (selected && (pendingRental is not null || HasPendingFormalCard || HasPendingTownQuestCommit))
            throw new InvalidOperationException("当前角色有待确认的业务结果，请先完成或重新选角");
        var setter = repository as IExpertJobSetter ?? throw new InvalidOperationException("当前存档不支持修改副职业");
        var after = setter.SetExpertJob(owner, current, type, experience);
        if (selected) Selected = after;
        return after;
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
