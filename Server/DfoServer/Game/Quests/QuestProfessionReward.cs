using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 第一转职分支变化重建双树；同分支逐级觉醒叠加赠技，均保留已核算的额外点数来源。
internal static class QuestProfessionReward
{
    public static bool SupportedDeclaration(StoryQuest quest) =>
        quest.RewardType == "[grow type]" && quest.Definition?.JobChange == 1 &&
            quest.Rewards is [{ Type:0,Text:null,Value:>=1 and <=5 }] ||
        quest.RewardType == "[awakening type]" && quest.Rewards is [{ Type:0,Text:null,Value:>=1 and <=2 }] &&
            (quest.Definition?.JobChange == quest.Rewards[0].Value+1 || LegacySelfAwakening(quest));

    // 旧Lv50主线的外传职业觉醒以目标角色和奖励声明阶段，没有普通转职任务的job change标记。
    // 保留真实副本、前置与碰撞约束；不把缺少标记的任意职业奖励视为合法。
    public static bool LegacySelfAwakening(StoryQuest quest) => quest is
        { Grade:"[epic]",Condition:"[clear map]",SubType:-1,MinimumLevel:50,IntData:[>0],
          RewardType:"[awakening type]",Rewards:[{Type:0,Text:null,Value:1}],
          Definition:{JobChange:0,SelectRewards.Length:0,PrerequisiteGroups.Length:>0,Dungeons.Length:>0,
            TargetCharacters:[{Job:"[demonic swordman]" or "[creator mage]",GrowType:0,Awakening:-1}]} definition } &&
        !definition.Has("job change quest");

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
            if (target > 2 || definition.JobChange != target + 1 && !LegacySelfAwakening(quest) ||
                LegacySelfAwakening(quest) && (expected.Job is not (9 or 10) ||
                    definition.TargetCharacters[0].Job != (expected.Job==9?"[demonic swordman]":"[creator mage]")) || target != stage + 1 ||
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
        if ((target & 15) is >= 1 and <= 5 && (expected.GrowType & 15) != (target & 15))
        {
            // 互转保留觉醒高位，但不能保留旧分支加点；同分支觉醒不走重建分支。
            var initialized = SkillLearning.Initialize(expected.Job, skills.FreeSkills(expected.Job, target), skills);
            if (!initialized.Success)
                throw new NotSupportedException("转职分支赠技无法初始化: " + initialized.Failure);
            learning = ExpertJobSkills.Preserve(expected.Job, initialized.State!, learning, skills) with
            {
                SelectedTree = learning.SelectedTree,
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
