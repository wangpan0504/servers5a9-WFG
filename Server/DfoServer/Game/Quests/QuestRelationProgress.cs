using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Quests;

// Full03 sub type=2按完成列表选顺序子任务；服务端参考两端按真实完成集初始化并更新剩余子任务数。
internal static class QuestRelationProgress
{
    public static bool ListParent(StoryQuest quest, QuestCatalog quests) => quest.Condition == "[quest clear]" && quest.SubType is -1 or 1 or 3 &&
        quest.IntData.Count > 0 && quest.IntData.Distinct().Count() == quest.IntData.Count &&
        quest.IntData.All(id => id is > 0 and < 30000 && id != quest.Id && quests.TryGet(id, out _)) &&
        QuestCoverage.HasOrdinaryDeclarations(quest);

    public static uint ListScalar(StoryQuest quest, IReadOnlyCollection<int> completed) =>
        (quest.SubType == -1 ? quest.IntData.All(completed.Contains) : quest.IntData.Any(completed.Contains)) ? 0u : 1u;
    // Full03完成登记只关联资源显式marker；不扩展成任意任务依赖链或账号任务领奖。
    public static int? CascadeMarker(StoryQuest quest, QuestCatalog quests)
    {
        if (quest.Definition?.Has("casecade clear") != true) return null;
        if (!QuestCoverage.HasOrdinaryCascade(quest)) throw new InvalidDataException("casecade clear不是唯一合法marker声明: " + quest.Id);
        int marker = quest.Definition.Fields.Single(field => field.Name == "casecade clear").Values[0].Value;
        if (marker == quest.Id || !quests.TryGet(marker,out var target) ||
            !IsCascadeMarker(target) && !IsAlternativeTerminal(quest,target,quests))
            throw new InvalidDataException("casecade clear引用缺失、自指或不是无奖励角色同步marker: " + quest.Id + "→" + marker);
        return marker;
    }

    // Full03 02038090只登记直接目标；简短旅程终点可引用同奖励的长旅程终点，不重复领奖或递归推进。
    static bool IsAlternativeTerminal(StoryQuest quest,StoryQuest target,QuestCatalog quests) =>
        quest.Grade=="[epic]" && target.Grade=="[epic]" && quest.Condition=="[meet npc]" && target.Condition==quest.Condition &&
        quest.MinimumLevel==target.MinimumLevel && quest.MaximumLevel==target.MaximumLevel &&
        quest.RewardType is null or "[item]" && quest.RewardType==target.RewardType && quest.Rewards.SequenceEqual(target.Rewards) && quest.DeclaredGoldMultiple==target.DeclaredGoldMultiple &&
        quest.Definition is {AccountQuest:false,JobChange:0} source &&
        target.Definition is {Diagnostics.Length:0,AccountQuest:false,JobChange:0,AcceptanceRewards.Length:0,SelectRewards.Length:0} definition &&
        definition.Job==source.Job && definition.GrowType==source.GrowType && definition.Has("casecade clear") &&
        QuestCoverage.HasOrdinaryCascade(target) &&
        quests.TryGet(definition.Fields.Single(field=>field.Name=="casecade clear").Values[0].Value,out var marker) &&
        IsAccountSyncMarker(marker);

    public static bool IsCascadeMarker(StoryQuest marker) =>
        marker.Definition is { Diagnostics.Length: 0, AccountQuest: false, JobChange: 0, AcceptanceRewards.Length: 0, SelectRewards.Length: 0 } definition &&
        !definition.Has("casecade clear") && marker.DeclaredGoldMultiple is null or 0 &&
        marker.RewardType is null or "[item]" &&
        (marker.Rewards.Count == 0 || marker.Rewards is [{ Type: 0, Value: 0, Text: null },{ Type: 0, Value: 0, Text: null }]) &&
        definition.Fields.Where(field => field.Name == "synchronization account quest").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 0, Text: null }] }];

    public static bool IsAccountSyncMarker(StoryQuest marker) => IsCascadeMarker(marker) && !QuestCycle.Daily(marker) && !QuestCycle.Repeat(marker) &&
        marker.Definition!.Fields.Single(field => field.Name == "synchronization account quest").Values[0].Value == 1;

    public static bool SequentialParent(StoryQuest parent, QuestCatalog quests) =>
        parent.Condition == "[quest clear]" && parent.SubType == 2 && parent.MainQuest is null &&
        QuestEligibility.InspectResource(parent).Entry is QuestResourceEntry.Ordinary or QuestResourceEntry.Hidden &&
        QuestCoverage.HasOrdinaryDeclarations(parent) && parent.Grade != "[training]" &&
        !QuestCycle.Daily(parent) && !QuestCycle.Repeat(parent) &&
        parent.Definition is { AccountQuest: false, AccountCollisions.Length: 0, RequiredAnswers.Length: 0 } && parent.IntData.Count > 0 &&
        parent.IntData.Distinct().Count() == parent.IntData.Count && parent.IntData.All(id =>
            id is > 0 and < 30000 && quests.TryGet(id, out var child) && child.MainQuest == parent.Id &&
            child.Definition is { Diagnostics.Length: 0 });

    public static bool ThresholdShape(StoryQuest parent) =>
        parent.Condition == "[clear multi quest]" && parent.SubType == 4 && parent.MainQuest is null &&
        parent.Definition is { MinimumClearCount: > 0 } definition &&
        definition.MinimumClearCount.Value <= parent.IntData.Count && parent.IntData.Count > 0 &&
        parent.IntData.Distinct().Count() == parent.IntData.Count &&
        parent.IntData.All(id => id is > 0 and < 30000 && id != parent.Id);

    public static bool ThresholdParent(StoryQuest parent, QuestCatalog quests) => ThresholdShape(parent) &&
        QuestCoverage.HasOrdinaryDeclarations(parent) && parent.IntData.All(id =>
            quests.TryGet(id,out var child) && child.MainQuest == parent.Id && child.Definition is { Diagnostics.Length: 0 });

    // Full03只要求未达成为正scalar、达成为0；1/0是与该消费合同相容的服务端模型。
    public static uint ThresholdScalar(StoryQuest parent, IReadOnlyCollection<int> completed)
    {
        if (!ThresholdShape(parent)) throw new NotSupportedException("阈值父任务需要完整sub4引用和最小完成数");
        var done = completed.ToHashSet();
        return parent.IntData.Count(done.Contains) >= parent.Definition!.MinimumClearCount!.Value ? 0u : 1u;
    }

    // 原生末子任务的34会转向父任务；普通寻物末子合并扣料，奖励仅支持固定物品或已确认的0/0占位。
    public static StoryQuest? TerminalSeeking(StoryQuest parent, QuestCatalog quests)
    {
        if (!SequentialParent(parent,quests) || parent.RewardType != "[item]") return null;
        var child = quests.Get(parent.IntData[^1]);
        return child is { Condition: "[seeking]", SubType: -1, RewardType: "[item]", DeclaredGoldMultiple: null or 0 } &&
            child.Attributes.Contains("not give exp quest") && QuestLifecyclePlan.ValidSeeking(child) &&
            (ZeroRewardChild(child) || child.Rewards.Count > 0 && child.Rewards.Count % 2 == 0 &&
                child.Rewards.All(token => token.Type == 0 && token.Text is null && token.Value > 0)) &&
            QuestCoverage.HasOrdinaryDeclarations(child) &&
            child.Definition is { AccountQuest: false, JobChange: 0, SelectRewards.Length: 0, AcceptanceRewards.Length: 0 } &&
            !child.Definition.Has("casecade clear") && !QuestCycle.Daily(child) && !QuestCycle.Repeat(child)
                ? child : null;
    }

    public static uint Initial(StoryQuest parent, QuestCatalog quests, IReadOnlyCollection<int> completed,
        IReadOnlyList<ActiveMainQuest>? active = null)
    {
        if (ThresholdParent(parent,quests)) return ThresholdScalar(parent,completed);
        if (!SequentialParent(parent,quests)) throw new NotSupportedException("父任务不是已确认的完整顺序关系");
        var done = completed.ToHashSet();
        uint remaining = checked((uint)parent.IntData.Count(id => !done.Contains(id)));
        // 原生末子交付会转向父任务并再次检查scalar；条件达成不等于已交付，清图凭据和翻牌时机仍由领奖规划验证。
        if (remaining == 1 && (TerminalSeeking(parent,quests) ?? TerminalClearDialogue(parent,quests)) is { } terminal && !done.Contains(terminal.Id) &&
            active?.Any(row => row.QuestId == terminal.Id && row.AcceptanceId != Guid.Empty && row.ConditionState == 0) == true)
            return 0;
        return remaining;
    }

    public static bool DialogueChild(StoryQuest child, QuestCatalog quests) =>
        child.MainQuest is { } id && quests.TryGet(id, out var parent) && parent.IntData.Contains(child.Id) &&
        (SequentialParent(parent, quests) || ThresholdParent(parent,quests) || ListParent(parent, quests));

    public static bool SceneChild(StoryQuest child, Character character, QuestCatalog quests, DungeonEntryPlan entry) =>
        DialogueChild(child,quests) && child.MainQuest == entry.RouteQuestId &&
        character.ActiveMainQuests.Any(row => row.QuestId == entry.RouteQuestId && QuestHuntProgress.Bound(row,entry));

    // 零物品占位与未声明奖励都属于禁经验子任务，实际奖励仍由普通交付处理。
    public static bool ZeroRewardChild(StoryQuest quest) => quest.MainQuest is not null &&
        (quest.RewardType is null && quest.Rewards.Count == 0 || quest.RewardType == "[item]" &&
            quest.Rewards is [{ Type: 0, Value: 0, Text: null }, { Type: 0, Value: 0, Text: null }]) &&
        quest.Attributes.Contains("not give exp quest") &&
        quest.Definition is { SelectRewards.Length: 0 } && quest.DeclaredGoldMultiple is null or 0;

    public static bool TownLinkedChild(StoryQuest quest) => ZeroRewardChild(quest) &&
        quest.Condition is "[meet npc]" or "[seek n meet npc]" or "[seeking]" or "[look cinematic]";

    // 清图只达成条件；带完成NPC的零奖励子任务仍须保留到原生结算后对话交付。
    public static bool ClearDialogueChild(StoryQuest quest, QuestCatalog quests) =>
        quest.CompleteNpc > 0 && ZeroRewardChild(quest) && quest.MainQuest is { } parentId &&
        quests.TryGet(parentId,out var parent) && SequentialParent(parent,quests) && parent.IntData[^1] == quest.Id &&
        (quest.Condition == "[clear map]" || QuestDungeonClearProgress.Supports(quest));

    public static StoryQuest? TerminalClearDialogue(StoryQuest parent, QuestCatalog quests) =>
        SequentialParent(parent,quests) && quests.Get(parent.IntData[^1]) is { } child && ClearDialogueChild(child,quests)
            ? child : null;

    // 已完成父任务下的终末零奖励子任务只补归档；不重开父任务，也不重复消费跳过链的材料。
    public static int? CompletedParentChild(Character character, StoryQuest child, QuestCatalog quests) =>
        ZeroRewardChild(child) && QuestCoverage.HasOrdinaryDeclarations(child) &&
        child.Definition is { AccountQuest: false, JobChange: 0 } && !child.Definition.Has("casecade clear") &&
        !QuestCycle.Daily(child) && !QuestCycle.Repeat(child) && child.MainQuest is { } parentId &&
        quests.TryGet(parentId,out var parent) && SequentialParent(parent,quests) && parent.IntData.Contains(child.Id) &&
        character.CompletedQuests.Contains(parentId) && !character.ActiveMainQuests.Any(row => row.QuestId == parentId) &&
        character.ActiveMainQuests.Any(row => row.QuestId == child.Id && row.ConditionState == 0 && row.AcceptanceId != Guid.Empty)
            ? parentId : null;

    public static ImmutableArray<ActiveMainQuest> Refresh(IReadOnlyList<ActiveMainQuest> active,
        IReadOnlyCollection<int> completed, QuestCatalog quests)
    {
        var done = completed.ToHashSet();
        return active.Select(row => row.AcceptanceId != Guid.Empty &&
            quests.TryGet(row.QuestId, out var parent) && (SequentialParent(parent, quests) || ThresholdParent(parent,quests) || ListParent(parent, quests))
                ? row with { ConditionState = ListParent(parent, quests) ? ListScalar(parent, done) : Initial(parent,quests,done,active) } : row).ToImmutableArray();
    }
}
