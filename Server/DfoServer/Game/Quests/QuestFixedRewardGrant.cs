using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestFixedRewardGrant
{
    // 仅准备已证固定物品部分；本结果不签发完整任务领奖资格，不省略独立经验/金币和交付合同。
    public static InventoryResult Prepare(Character character, StoryQuest quest, ItemCatalog items, GoldLimitCatalog goldLimits,
        EpicPieceCatalog pieces, DateTimeOffset utcNow)
    {
        var fixedRewards = ReadRewards(character.Job, quest);
        return InventoryRewardGrantService.TryGrantBatch(character,
            fixedRewards.Select(reward => new InventoryReward(reward.ItemId, reward.Count)).ToArray(), items, goldLimits, pieces, utcNow);
    }

    // 3145声明job=all/grow type=-1；武器沿真实职业条件筛选，不按转职或觉醒收窄。
    internal static ImmutableArray<QuestFixedItemReward> ReadRewards(byte job, StoryQuest quest)
    {
        if (job > 15 || quest.Id != 3145 || quest.Condition != "[clear map]" || quest.SubType != -1 ||
            quest.Grade != "[epic]" || quest.Difficulty != "1" || quest.RewardType != "[item]" ||
            quest.Npc != 1 || quest.CompleteNpc != 1247 ||
            !quest.IntData.SequenceEqual([76126]) || quest.DungeonIndex != 3 || quest.BossTemplate != 107000903 ||
            quest.Path != "n_quest/quest_renewal/epic_quest/elvengard/elvengard_epic_01.qst")
            throw new NotSupportedException("固定奖励准备仅支持已核验的全职业3145完整资源合同");
        var fixedRewards = QuestFixedRewardDefinition.Parse(quest).ForJob(job);
        QuestFixedItemReward[] armor = [new(10403, 1), new(12403, 1), new(14403, 1), new(16403, 1), new(18403, 1)];
        if (fixedRewards.Length != 7 || fixedRewards[0] != new QuestFixedItemReward(10165055, 1) ||
            fixedRewards[1].Count != 1 || !fixedRewards.Skip(2).SequenceEqual(armor))
            throw new InvalidDataException("3145固定奖励声明发生变化，不能按空奖励或部分奖励完成");
        return fixedRewards;
    }
}
