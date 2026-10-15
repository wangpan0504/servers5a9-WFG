using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 首次转职重建双树；逐级觉醒叠加赠技，均保留已核算的额外点数来源。
internal static class QuestProfessionReward
{
    public static byte Target(Character expected, StoryQuest quest, SkillLearningCatalog skills)
    {
        if (quest.Definition is not { } definition ||
            quest.Rewards is not [{ Type: 0, Text: null, Value: >= 1 and <= 5 }] ||
            quest.Definition.SelectRewards.Length != 0)
            throw new NotSupportedException("任务不是合法职业变化奖励");
        byte target = checked((byte)quest.Rewards[0].Value);
        if (quest.RewardType == "[grow type]")
        {
            if (definition.JobChange != 1 || expected.GrowType != 0)
                throw new NotSupportedException("首次转职需要未转职角色和对应任务标记");
            var number = definition.Values("grow number");
            if (number.Length != 0 && (number is not [{ Type: 0, Text: null }] || number[0].Value != target - 1))
                throw new NotSupportedException("转职任务资源的零基分支索引与奖励不符");
        }
        else if (quest.RewardType == "[awakening type]")
        {
            int first = expected.GrowType & 15, stage = expected.GrowType >> 4;
            if (target > 2 || definition.JobChange != target + 1 || target != stage + 1 ||
                first == 0 && expected.Job is not (9 or 10))
                throw new NotSupportedException("觉醒必须按当前合法职业逐级推进");
            target = checked((byte)(first | target << 4));
        }
        else throw new NotSupportedException("未知职业奖励类型");
        // 实际分支资格由任务资源给出，再确认目标CHR赠技索引可读取；占位CHR块不代表有觉醒任务。
        _ = skills.FreeSkills(expected.Job, target);
        return target;
    }

    public static CharacterLearningState Prepare(Character expected, byte target, byte level,
        CharacterLearningState learning, SkillLearningCatalog skills, DateTimeOffset now)
    {
        if (!learning.Initialized || learning.BonusSp is null || learning.BonusTp is null)
            throw new NotSupportedException("职业变化需要已核算的两树SP/TP来源");
        if (expected.GrowType == 0 && target is >= 1 and <= 5)
        {
            // 旧端 set_grow_type 首次转职先清两树；觉醒不走此分支。新树按免费基线核算，不再扣旧加点费用。
            var initialized = SkillLearning.Initialize(expected.Job, skills.FreeSkills(expected.Job, target), skills);
            if (!initialized.Success)
                throw new NotSupportedException("首次转职赠技无法初始化: " + initialized.Failure);
            learning = initialized.State! with
            {
                Experience = learning.Experience,
                BonusSp = learning.BonusSp,
                BonusTp = learning.BonusTp,
                ExpertContractExpiresAt = learning.ExpertContractExpiresAt
            };
        }
        var result = SkillPointLedger.Synchronize(expected.Job, target, level, learning, skills,
            skills.ActiveExpertContractLevels(learning.ExpertContractExpiresAt, now.ToUnixTimeSeconds()));
        result.Validate();
        return result;
    }
}
