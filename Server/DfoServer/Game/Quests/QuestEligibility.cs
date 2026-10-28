using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal sealed record QuestEligibilityResult(bool Eligible, bool Visible, string Reason);
internal enum QuestEligibilityMode { Ordinary, IgnoreMinimumLevel }
internal enum QuestResourceEntry { Ordinary, Dialogue, Dedicated, OutsideLevels, Event, Creature, Profession, Hardcore, Invalid, Hidden }
internal sealed record QuestResourceAccess(QuestResourceEntry Entry, string Reason);

// 仅从已装载的领域快照与共享资源计算，不拥有在线角色或访问数据库。
internal static class QuestEligibility
{
    private static readonly string[] Jobs =
    [
        "[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]", "[at gunner]", "[thief]", "[at fighter]",
        "[at mage]", "[demonic swordman]", "[creator mage]", "[at swordman]", "[knight]", "[demonic lancer]",
        "[at priest]", "[gun blader]"
    ];

    internal static bool MatchesJob(byte job, string? required) => job < Jobs.Length &&
        (required is null or "[all]" || required == Jobs[job]);

    // 两端普通列表的分类合同；本版新增专用分类仍保留定义，等待对应入口确认。
    public static QuestResourceAccess InspectResource(StoryQuest quest)
    {
        if (quest.Definition is not { } definition || definition.Diagnostics.Length != 0)
            return new(QuestResourceEntry.Invalid, "资源声明存在未确认项");
        if (quest.MinimumLevel < 1 || quest.MinimumLevel > 95 || quest.MaximumLevel < quest.MinimumLevel)
            return new(QuestResourceEntry.OutsideLevels, "原始等级不属于Lv1–95普通任务；保留特殊标记");
        if (definition.IsEvent)
            return new(QuestResourceEntry.Event, "活动任务需要本版活动启用与期限配置");
        if (definition.HardcoreOnly)
            return new(QuestResourceEntry.Hardcore, "任务需要尚未具备的极限模式");
        if (definition.CreatureKind >= 0)
            return new(QuestResourceEntry.Creature, "任务需要匹配宠物状态");
        if (definition.ExpertJobLevel.Length != 0)
            return new(QuestResourceEntry.Profession, "任务需要副职业状态");
        if (!QuestCoverage.HasOrdinaryFilter(quest))
            return new(QuestResourceEntry.Dedicated, "exception filter需要已确认的单字符串character声明");
        if (quest.MainQuest is not null)
            return new(QuestResourceEntry.Dialogue, "子任务需要父任务及资源顺序的真实入口");
        if (QuestHiddenEntry.ImmediateShape(quest) || QuestHiddenEntry.NpcDialogueShape(quest))
            return new(QuestResourceEntry.Hidden, "隐藏任务通过合格21候选和884请求开启");
        if (QuestAntonUnlockEntry.Supports(quest))
            return new(QuestResourceEntry.Ordinary, "本服安徒恩解锁任务的奈恩入口；仍须原前置和材料");

        if (QuestMarriageProgress.Supports(quest) && definition.ExposedByNpc)
            return new(QuestResourceEntry.Ordinary, "婚约准备任务；仍须核对当前关系、前置及原接取身份");
        if (!definition.ExposedByNpc || !OrdinaryGrade(quest.Grade) && !QuestCycle.NormalDungeonMission(quest) && !QuestEpisodeEntry.Supports(quest))
            return new(QuestResourceEntry.Dedicated, "隐藏或专用分类任务需要对应入口");
        return new(QuestResourceEntry.Ordinary, "普通任务候选；仍须核对角色、前置及完成状态");
    }

    private static bool OrdinaryGrade(string? grade) => grade is null or "" or "[normal]" or "[side]" or "[sub]" or "[epic]" or
        "[realization]" or "[training]" or "[achievement]" or "[mission]" or "[daily]" or "[daily random]" or
        "[normaly repeat]" or "[special daily]" or "[common unique]" or "[system]";

    public static ImmutableArray<int> Visible(Character character, QuestCatalog catalog)
    {
        var completed = Completed(character);
        var active = character.ActiveMainQuests.Select(quest => (int)quest.QuestId).ToHashSet();
        return catalog.All.Where(quest => Evaluate(character, quest, catalog, completed, active, includeActive: true).Visible)
            .Select(quest => quest.Id).ToImmutableArray();
    }

    public static bool CreatureCandidatesChanged(Character before, Character after, QuestCatalog catalog)
    {
        var previousCompleted = Completed(before);
        var currentCompleted = Completed(after);
        var previousActive = before.ActiveMainQuests.Select(row => (int)row.QuestId).ToHashSet();
        var currentActive = after.ActiveMainQuests.Select(row => (int)row.QuestId).ToHashSet();
        return catalog.All.Where(quest => quest.Definition?.CreatureKind >= 0).Any(quest =>
            Evaluate(before, quest, catalog, previousCompleted, previousActive, includeActive: true).Visible !=
            Evaluate(after, quest, catalog, currentCompleted, currentActive, includeActive: true).Visible);
    }

    // 21同时携带可见任务与等待客户端884的合格隐藏候选；候选本身不生成822或接取槽。
    public static ImmutableArray<int> Candidates(Character character, QuestCatalog catalog)
    {
        var completed = Completed(character);
        var active = character.ActiveMainQuests.Select(quest => (int)quest.QuestId).ToHashSet();
        return catalog.All.Where(quest => Evaluate(character,quest,catalog,completed,active,includeActive:true).Visible ||
            !active.Contains(quest.Id) && QuestHiddenEntry.Direct(quest,catalog) &&
            !quest.Definition!.HideOnCompletedQuests.Any(completed.Contains) &&
            Evaluate(character,quest,catalog,completed,active).Eligible).Select(quest => quest.Id).ToImmutableArray();
    }

    public static QuestEligibilityResult Evaluate(Character character, StoryQuest quest, QuestCatalog catalog,
        bool allowAboveMaximum = false, QuestEligibilityMode mode = QuestEligibilityMode.Ordinary) =>
        Evaluate(character, quest, catalog, Completed(character),
            character.ActiveMainQuests.Select(active => (int)active.QuestId).ToHashSet(), allowAboveMaximum: allowAboveMaximum, mode: mode);

    public static HashSet<int> Completed(Character character) => character.CompletedQuests.Concat(character.AccountCompletedQuests).ToHashSet();

    private static QuestEligibilityResult Evaluate(Character character, StoryQuest quest, QuestCatalog catalog,
        IReadOnlySet<int> completed, IReadOnlySet<int> active, bool includeActive = false, bool allowAboveMaximum = false,
        QuestEligibilityMode mode = QuestEligibilityMode.Ordinary)
    {
        var access = InspectResource(quest);
        if (access.Entry is QuestResourceEntry.Invalid or QuestResourceEntry.OutsideLevels or QuestResourceEntry.Event)
            return Reject(access.Reason);
        if (access.Entry == QuestResourceEntry.Profession && !QuestExpertJobProgress.MatchesLevel(character, quest))
            return Reject(access.Reason);
        if (access.Entry == QuestResourceEntry.Creature && !QuestCreatureReward.Matches(character, quest, catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            return Reject("未装备任务要求的宠物，或宠物种类、等级及进化产物不符");
        if (access.Entry == QuestResourceEntry.Hardcore) return Reject(access.Reason);
        var definition = quest.Definition!;
        if (quest.Grade == "[wedding]" && !QuestMarriageProgress.Eligible(character, quest))
            return Reject("结婚准备需要本人的有效婚约，任务形状必须已接入");
        if (!DfoServer.Game.Pvp.PvpTrainingExposure.Allows(character,quest))
            return Reject("普通与专用决斗角色任务身份不符");
        if (quest.Grade is "[training]" or "[daily random]")
            return Reject("任务需要对应每日专用列表");
        if (character.Job >= Jobs.Length || character.Level is < 1 or > 95)
            return Reject("角色等级或职业无效");
        // 特殊等级标记保留原值，不能截为1或95后成为普通可接任务。
        if (quest.MinimumLevel < 1 || quest.MaximumLevel < quest.MinimumLevel ||
            mode != QuestEligibilityMode.IgnoreMinimumLevel && character.Level < quest.MinimumLevel ||
            !allowAboveMaximum && character.Level > quest.MaximumLevel)
            return Reject("等级不符");
        var characterRequirements = CharacterRequirements(character, quest, catalog);
        if (!characterRequirements.Eligible) return characterRequirements;
        if (completed.Contains(quest.Id) && !QuestCycle.Repeat(quest) || !includeActive && active.Contains(quest.Id))
            return Reject("已完成或已接取");
        if (definition.PrerequisiteGroups.Length != 0 && !definition.PrerequisiteGroups.Any(group =>
            group.Length != 0 && group.All(id => catalog.TryGet(id, out _) && completed.Contains(id))))
            return Reject("前置组未达成");
        if (definition.Collisions.Any(id => completed.Contains(id) || active.Contains(id)))
            return Reject("冲突任务已完成或进行中");
        if (definition.AccountCollisions.Any(character.AccountCompletedQuests.Contains))
            return Reject("冲突账号任务已完成");
        if (definition.RequiredAnswers.Any(required => !completed.Contains(required.QuestId) || required.Answer is < 0 or > 254 ||
            !(character.AccountQuestAnswers.TryGetValue(required.QuestId,out byte answer) || character.QuestAnswers.TryGetValue(required.QuestId,out answer)) ||
            answer != required.Answer))
            return Reject("持久前置回答不符");
        if (quest.MainQuest is { } parentId)
        {
            if (!active.Contains(parentId) || !catalog.TryGet(parentId, out var parent) ||
                parent.Condition is not ("[quest clear]" or "[clear quest]" or "[clear multi quest]"))
                return Reject("父任务未接取");
            if (parent.Condition == "[clear multi quest]")
                return QuestRelationProgress.ThresholdParent(parent,catalog) && parent.IntData.Contains(quest.Id)
                    ? new(true, includeActive && active.Contains(quest.Id), "等待阈值父任务的真实子任务入口")
                    : Reject("阈值父任务引用或条件未确认");
            int next = parent.IntData.FirstOrDefault(id => !completed.Contains(id));
            if (next != quest.Id)
                return Reject("尚未到本子任务的资源顺序");
            return new(true, includeActive && active.Contains(quest.Id), "等待父任务对话入口");
        }
        bool visible = (access.Entry == QuestResourceEntry.Ordinary ||
            includeActive && active.Contains(quest.Id) && QuestAutoDailyPlan.Supports(quest) ||
            access.Entry == QuestResourceEntry.Profession && definition.ExposedByNpc && OrdinaryGrade(quest.Grade) ||
            access.Entry == QuestResourceEntry.Creature && definition.ExposedByNpc && OrdinaryGrade(quest.Grade)) &&
            !definition.HideOnCompletedQuests.Any(completed.Contains);
        return new(true, visible, visible ? "资格满足" : "需要专用任务入口");
    }

    // 与普通资格共用角色适用性；不将尚未完成的前置或未作答当成已排除的职业分支。
    internal static QuestEligibilityResult CharacterRequirements(Character character, StoryQuest quest, QuestCatalog catalog)
    {
        if (character.Job >= Jobs.Length || quest.Definition is not { } definition)
            return Reject("角色职业或任务定义无效");
        if (!MatchesJob(character.Job, definition.Job))
            return Reject("职业不符");
        int grow = character.GrowType & 15, awakening = character.GrowType >> 4;
        if (definition.TargetCharacters.Length != 0 && !definition.TargetCharacters.Any(target =>
            (target.Job == "[all]" || target.Job == Jobs[character.Job]) &&
            (target.GrowType == -1 || target.GrowType == grow) && (target.Awakening == -1 || target.Awakening == awakening ||
                target.Awakening==1 && awakening==2 && AwakenedStoryAlternative(quest,catalog,character.Job,grow))))
            return Reject("目标转职或觉醒不符");
        if (definition.NonTargetCharacters.Any(target => (target.Job == "[all]" || target.Job == Jobs[character.Job]) &&
            (target.GrowType == -1 || target.GrowType == grow) && (target.Awakening == -1 || target.Awakening == awakening)))
            return Reject("角色符合任务排除职业条件");
        if (QuestProfessionReward.LegacySelfAwakening(quest) && awakening != 0)
            return Reject("旧外传主线觉醒只面向尚未觉醒角色");
        bool selfAwakening = character.Job is 9 or 10 && quest.Grade == "[realization]";
        if (definition.JobChange == 1 && (quest.RewardType == "[grow type]" && grow != 0 ||
                quest.RewardType != "[grow type]" && ((TransferredStoryAlternative(quest,catalog,character.Job) || TransferredAutoMeet(quest)) ? grow == 0 : grow != 0)) ||
            definition.JobChange == 2 && ((!selfAwakening && grow == 0) || awakening != 0) ||
            definition.JobChange == 3 && ((!selfAwakening && grow == 0) || awakening != 1) ||
            definition.GrowType != -1 && definition.JobChange is not (1 or 10 or 20) &&
            definition.GrowType != (definition.JobChange is 2 or 3 ? grow : character.GrowType))
            return Reject("转职阶段不符");
        return new(true, false, "角色条件满足");
    }

    // 明确只面向已转职角色的接取型祝贺会面不执行转职；不扩展到普通会面或混合目标。
    internal static bool TransferredAutoMeet(StoryQuest quest)
        => quest is { Grade: "[common unique]", Condition: "[meet npc]", SubType: 1,
            RewardType: "[item]", IntData: [> 0], Definition: { JobChange: 1 } definition } &&
            !definition.TargetCharacters.IsEmpty && definition.TargetCharacters.All(target =>
                target.GrowType is >= 1 and <= 5 && target.Awakening == -1 && Jobs.Contains(target.Job));

    // 50级主线以互斥分支区分觉醒任务和已觉醒角色的普通衔接任务；二觉仍满足后者的一觉资格。
    // 只接受同职业/分支、同前置的零奖励普通替代，不放宽其他精确觉醒条件，也不完成觉醒任务。
    static bool AwakenedStoryAlternative(StoryQuest quest,QuestCatalog catalog,byte job,int grow)
        => quest is {Grade:"[epic]",Condition:"[clear map]",RewardType:"[item]",Definition:{JobChange:0} definition} &&
            quest.Rewards is [{Type:0,Text:null,Value:0},{Type:0,Text:null,Value:0}] && definition.SelectRewards.IsEmpty &&
            definition.PrerequisiteGroups.Length>0 && definition.Collisions.Any(id=>catalog.TryGet(id,out var sibling) &&
                sibling.RewardType=="[awakening type]" && sibling.Rewards is [{Type:0,Text:null,Value:1}] &&
                sibling.MinimumLevel==quest.MinimumLevel && sibling.Condition==quest.Condition &&
                sibling.Definition is {JobChange:2} other && other.Job==Jobs[job] && other.GrowType==grow &&
                definition.PrerequisiteGroups.Length==other.PrerequisiteGroups.Length &&
                definition.PrerequisiteGroups.Zip(other.PrerequisiteGroups).All(pair=>pair.First.SequenceEqual(pair.Second)));

    // 转职主线的零奖励互斥替代仅服务已转职角色；GM设置状态也不要求伪造旧转职任务记录。
    public static bool TransferredStoryAlternative(StoryQuest quest,QuestCatalog catalog,byte job)
        => job<Jobs.Length && quest is {Grade:"[epic]",Condition:"[clear map]",RewardType:"[item]",Definition:{JobChange:1} definition} &&
            quest.Rewards is [{Type:0,Text:null,Value:0},{Type:0,Text:null,Value:0}] && definition.SelectRewards.IsEmpty &&
            definition.TargetCharacters is [{GrowType:-1,Awakening:-1} target] && target.Job==Jobs[job] &&
            definition.PrerequisiteGroups.Length>0 && definition.Collisions.Any(id=>catalog.TryGet(id,out var sibling) &&
                sibling.RewardType=="[grow type]" && sibling.Rewards is [{Type:0,Text:null,Value:>=1 and <=5}] &&
                sibling.MinimumLevel==quest.MinimumLevel && sibling.Condition==quest.Condition &&
                sibling.Definition is {JobChange:1} other && other.TargetCharacters.Any(row=>row.Job==Jobs[job] && row.GrowType==0) &&
                definition.PrerequisiteGroups.Length==other.PrerequisiteGroups.Length &&
                definition.PrerequisiteGroups.Zip(other.PrerequisiteGroups).All(pair=>pair.First.SequenceEqual(pair.Second)));

    private static QuestEligibilityResult Reject(string reason) => new(false, false, reason);
}
