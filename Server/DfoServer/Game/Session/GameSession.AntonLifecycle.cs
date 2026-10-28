using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 不持原owner/Battle/Party锁等待阶段writer；SQL仅提交原666身份，不写玩家资产。
    internal void CommitPendingAntonPhase()
    {
        var battle = owner.Execute(() => raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } raid ? raid.Battle : null);
        if (battle?.PendingAntonSettlement() is not { } candidate) return;
        if (repository is not IAntonRaidLifecycleStore store) throw new InvalidOperationException("正式安徒恩缺少阶段存储");
        try { battle.AcceptAntonSettlement(candidate, store.CommitAntonPhase(candidate)); }
        catch (Exception error) when (store.IsRetryable(error))
        { battle.RetryAntonSettlement(); throw; }
    }
}
