using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    long quotaDisplayRevision;

    public DungeonEntryResetResult ResetDungeonEntries() => owner.Execute(() =>
    {
        EnsureQuotaResetAllowed();
        long next = checked(quotaDisplayRevision + 1);
        var result = repository.ResetDungeonEntries(owner);
        quotaDisplayRevision = next;
        return result;
    });

    public RewardLimitResetResult ResetRewardLimits() => owner.Execute(() =>
    {
        EnsureQuotaResetAllowed();
        long next = checked(quotaDisplayRevision + 1);
        var result = repository.ResetRewardLimits(owner);
        quotaDisplayRevision = next;
        return result;
    });

    void EnsureQuotaResetAllowed()
    {
        if (Phase is SessionPhase.Dungeon or SessionPhase.Settlement || raidCombat is { } raid &&
            (raid.Battle.View.State != AntonRaidBattleState.Completed || raid.Pending.Count != 0))
            throw new InvalidOperationException("角色正在副本、攻坚战或奖励结算中，请结束后再刷新额度");
    }
}
