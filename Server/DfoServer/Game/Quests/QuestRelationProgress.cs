using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Quests;

// Full03 sub type=2按完成列表选顺序子任务；服务端参考两端按真实完成集初始化并更新剩余子任务数。
internal static class QuestRelationProgress
{
    // Full03完成登记只关联资源显式marker；不扩展成任意任务依赖链或账号任务领奖。
    public static int? CascadeMarker(StoryQuest quest, QuestCatalog quests)
    {
        if (quest.Definition?.Has("casecade clear") != true) return null;
        if (!QuestCoverage.HasOrdinaryCascade(quest)) throw new InvalidDataException("casecade clear不是唯一合法marker声明: " + quest.Id);
        int marker = quest.Definition.Fields.Single(field => field.Name == "casecade clear").Values[0].Value;
        if (marker == quest.Id || !quests.TryGet(marker,out var target) || !IsCascadeMarker(target))
            throw new InvalidDataException("casecade clear引用缺失、自指或不是无奖励角色同步marker: " + quest.Id + "→" + marker);
        return marker;
    }

    public static bool IsCascadeMarker(StoryQuest marker) =>
        marker.Definition is { Diagnostics.Length: 0, AccountQuest: false, JobChange: 0, AcceptanceRewards.Length: 0, SelectRewards.Length: 0 } definition &&
        !definition.Has("casecade clear") && marker.DeclaredGoldMultiple is null or 0 &&
        marker.RewardType is null or "[item]" &&
        (marker.Rewards.Count == 0 || marker.Rewards is [{ Type: 0, Value: 0, Text: null },{ Type: 0, Value: 0, Text: null }]) &&
        definition.Fields.Where(field => field.Name == "synchronization account quest").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 0, Text: null }] }];

    public static bool SequentialParent(StoryQuest parent, QuestCatalog quests) =>
        parent.Condition == "[quest clear]" && parent.SubType == 2 && parent.MainQuest is null &&
        QuestEligibility.InspectResource(parent).Entry == QuestResourceEntry.Ordinary &&
        QuestCoverage.HasOrdinaryDeclarations(parent) && parent.Grade != "[training]" &&
        !QuestCycle.Daily(parent) && !QuestCycle.Repeat(parent) &&
        parent.Definition is { AccountQuest: false, AccountCollisions.Length: 0, RequiredAnswers.Length: 0 } && parent.IntData.Count > 0 &&
        parent.IntData.Distinct().Count() == parent.IntData.Count && parent.IntData.All(id =>
            id is > 0 and < 30000 && quests.TryGet(id, out var child) && child.MainQuest == parent.Id &&
            child.Definition is { Diagnostics.Length: 0 });

    public static uint Initial(StoryQuest parent, QuestCatalog quests, IReadOnlyCollection<int> completed)
    {
        if (!SequentialParent(parent,quests)) throw new NotSupportedException("父任务不是已确认的完整顺序关系");
        var done = completed.ToHashSet();
        return checked((uint)parent.IntData.Count(id => !done.Contains(id)));
    }

    public static bool DialogueChild(StoryQuest child, QuestCatalog quests) =>
        child.MainQuest is { } id && quests.TryGet(id, out var parent) && SequentialParent(parent, quests);

    public static bool SceneChild(StoryQuest child, Character character, QuestCatalog quests, DungeonEntryPlan entry) =>
        DialogueChild(child,quests) && child.MainQuest == entry.RouteQuestId &&
        character.ActiveMainQuests.Any(row => row.QuestId == entry.RouteQuestId && QuestHuntProgress.Bound(row,entry));

    public static bool ZeroRewardChild(StoryQuest quest) => quest.MainQuest is not null && quest.RewardType is null &&
        quest.Attributes.Contains("not give exp quest") && quest.Rewards.Count == 0 &&
        quest.Definition is { SelectRewards.Length: 0 } && quest.DeclaredGoldMultiple is null or 0;

    public static ImmutableArray<ActiveMainQuest> Refresh(IReadOnlyList<ActiveMainQuest> active,
        IReadOnlyCollection<int> completed, QuestCatalog quests)
    {
        var done = completed.ToHashSet();
        return active.Select(row => row.AcceptanceId != Guid.Empty &&
            quests.TryGet(row.QuestId, out var parent) && SequentialParent(parent, quests)
                ? row with { ConditionState = checked((uint)parent.IntData.Count(id => !done.Contains(id))) } : row).ToImmutableArray();
    }
}
