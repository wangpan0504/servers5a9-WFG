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
        DungeonExperienceReward reward, CharacterGrowthCatalog catalog, FrozenDungeonGroundDeath? loot = null,
        QuestDropPlan? questDrops = null, PendingSpecialDungeonClearReward? specialClearReward = null) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        if (run.TagEvildomLife is not null || !Ready || SelectionEpoch != expectedSelectionEpoch || !ReferenceEquals(Selected, expected) ||
            run.Snapshot.Identity.CharacterId != expected.Id || !run.IsPendingDeath(prepared))
            throw new InvalidOperationException("副本成长的选角、角色快照或待提交死亡已失效");

        // 存储失败或提交结果不明时保留候选和Selected；不能在这里撤销后生成新ID重试。
        DungeonGrowthCommit committed;
        if (run.Entry.IsTutorial)
        {
            var checkpoint = TutorialCheckpoint(expected, "death", prepared.Death.EventId, prepared.Death.MonsterKey,
                reward.Amount, prepared.Death.Identity, prepared.Result.Snapshot.CurrentMap.Id,
                prepared.Result.ClearFact is not null, catalog) with { Death = prepared, DeathReward = reward, Loot = loot };
            var tutorial = repository.CommitTutorialCheckpoint(owner, checkpoint);
            committed = new(tutorial.Character, tutorial.Duplicate,
                checked((uint)(tutorial.Character.Learning.Experience - expected.Learning.Experience))) { Adventure = tutorial.Adventure };
        }
        else
        {
            var equipmentGrowth = items is null ? null : Game.Inventory.Services.EquipmentGrowthPlan.Create(expected, run, prepared,
                items, (timeProvider ?? TimeProvider.System).GetUtcNow(), timeProvider);
            var fiendwar = raidCombat is { Battle.Kind: Raids.RaidKind.Fiendwar } raid
                ? raid.Battle.CaptureFiendwarTitleDeath(raid.Actor, run, prepared) : null;
            var clock = timeProvider ?? TimeProvider.System;
            var before = questDrops?.Inventory.After ?? expected.Inventory;
            if (equipmentGrowth is not null)
                foreach (var change in equipmentGrowth.Changes)
                {
                    if (before.Find(change.Before.Position) != change.Before)
                        throw new InvalidOperationException("直入包的装备成长前提与任务材料规划冲突");
                    before = before with { Items = before.Items.Replace(change.Before, change.After) };
                }
            DungeonDirectPickupPlan? directPickup = null;
            if (loot is not null && items is not null && pieces is not null)
            {
                // 与仓储既有组合次序相同：任务材料、装备成长、最后才放置本次普通掉落。
                directPickup = DungeonDirectPickupPlan.Create(expected, prepared, loot, before, items, pieces,
                    HelperDirectPickupEnabled, clock.GetUtcNow(), clock, directPickupGoldLimits);
                if (directPickup is { IsExpired: false, Entries.IsEmpty: false })
                    before = directPickup.After;
            }
            SpecialDungeonClearRewardPlan? specialClearPlan = null;
            if (specialClearReward is not null)
            {
                if (!ReferenceEquals(specialClearReward.Death, prepared))
                    throw new InvalidOperationException("特殊地下城通关奖励候选不属于当前待提交死亡");
                var rewardItems = formalDungeon?.RewardItems ?? items
                    ?? throw new InvalidOperationException("特殊地下城通关奖励缺少物品目录");
                specialClearPlan = SpecialDungeonClearRewardPlan.Create(expected, prepared,
                    specialClearReward.Reward, specialClearReward.Grants, before, rewardItems, clock.GetUtcNow());
            }
            committed = repository.CommitDungeonGrowth(owner, expected, run, prepared, reward, catalog, loot, questDrops, equipmentGrowth,
                fiendwar, directPickup, specialClearPlan);
        }
        if (committed.Character.Id != expected.Id || committed.Character.Revision < expected.Revision)
            throw new InvalidOperationException("副本成长提交结果不属于原角色或revision回退");
        var death = run.ConfirmCommittedDeath(prepared);
        if (death.Outcome != DungeonRunOutcome.Accepted)
            throw new InvalidOperationException("已提交成长的原死亡候选不能确认");
        Selected = committed.Character;
        return (committed, death);
    });
}
