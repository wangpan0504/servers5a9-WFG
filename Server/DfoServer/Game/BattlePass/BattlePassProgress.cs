using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 只投影显示进度，不改累计经验、资格、任务或领取事实。
internal readonly record struct BattlePassProgress(uint Level, uint Experience)
{
    public static BattlePassProgress FromTotalExperience(BattlePassCatalog catalog, long totalExperience)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalExperience);
        ulong total = (ulong)totalExperience;
        int level = catalog.LevelAtTotalExperience(total);
        // 本服终级显示策略为最高级/0；原累计经验仍完整保留。
        ulong progress = level == catalog.MaximumLevel ? 0 : total - catalog.TotalExperienceToReach(level);
        return new(checked((uint)level), checked((uint)progress));
    }
}
