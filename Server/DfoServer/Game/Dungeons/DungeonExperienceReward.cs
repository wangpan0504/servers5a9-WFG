using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Dungeons;

// 奖励候选携带明确规则与资源身份；合法死亡及当前owner仍由提交路径核验。
internal sealed record DungeonExperienceReward(string RuleId, string ResourceSource, int MonsterId, int MonsterLevel, uint Amount);

// 存储返回实际提交后的角色；Duplicate表示账本精确重放，不是新的击杀或新奖励。
internal sealed record DungeonGrowthCommit(Character Character, bool Duplicate);

internal static class DungeonKillExperiencePolicy
{
    public static DungeonExperienceReward Plan(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, DungeonKillExperienceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!run.IsPendingDeath(prepared))
            throw new InvalidOperationException("击杀经验只能规划当前待提交死亡");
        return new(DungeonKillExperienceCatalog.RuleId, DungeonKillExperienceCatalog.ResourcePath, prepared.Monster.MonsterId,
            prepared.Monster.CandidateLevel, catalog.GetExperience(prepared.Monster.CandidateLevel));
    }
}
