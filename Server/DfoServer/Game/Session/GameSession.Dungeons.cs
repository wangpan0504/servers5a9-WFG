using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 调用方必须来自已核验正式战斗，并传入本次选角及规划时的完整角色快照。
    // 同一owner内先提交SQL，再确认死亡和安装实际角色；此方法不发包、不签发死亡。
    public (DungeonGrowthCommit Commit, DungeonRunResult Death) CommitDungeonGrowth(long expectedSelectionEpoch,
        Character expected, SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        DungeonExperienceReward reward, CharacterGrowthCatalog catalog, FrozenDungeonGroundDeath? loot = null, QuestDropPlan? questDrops = null) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!Ready || SelectionEpoch != expectedSelectionEpoch || !ReferenceEquals(Selected, expected) ||
            run.Snapshot.Identity.CharacterId != expected.Id || !run.IsPendingDeath(prepared))
            throw new InvalidOperationException("副本成长的选角、角色快照或待提交死亡已失效");

        // 存储失败或提交结果不明时保留候选和Selected；不能在这里撤销后生成新ID重试。
        var committed = repository.CommitDungeonGrowth(owner, expected, run, prepared, reward, catalog, loot, questDrops);
        if (committed.Character.Id != expected.Id || committed.Character.Revision < expected.Revision)
            throw new InvalidOperationException("副本成长提交结果不属于原角色或revision回退");
        var death = run.ConfirmCommittedDeath(prepared);
        if (death.Outcome != DungeonRunOutcome.Accepted)
            throw new InvalidOperationException("已提交成长的原死亡候选不能确认");
        Selected = committed.Character;
        return (committed, death);
    });
}
