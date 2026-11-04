using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 先退出自身owner，再按固定全团owner锁序提交阶段资格及任务，最后安装原会话快照。
    internal void CommitPendingAntonPhase()
    {
        var battle = owner.Execute(() => raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } raid ? raid.Battle : null);
        if (battle?.PendingAntonSettlement() is null) return;
        if (repository is not IAntonRaidLifecycleStore store) throw new InvalidOperationException("正式安徒恩缺少阶段存储");
        try { battle.CommitPendingAntonSettlement(store); }
        catch (Exception error) when (store.IsRetryable(error))
        { battle.RetryAntonSettlement(); throw; }
    }
}
