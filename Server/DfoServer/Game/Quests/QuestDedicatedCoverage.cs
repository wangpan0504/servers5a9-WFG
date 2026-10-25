using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal sealed record QuestDedicatedCoverageEntry(ImmutableArray<int> Answers, bool AccountMarker, ImmutableArray<string> Paths)
{
    public bool InstanceMission { get; init; }
}

// 只读普查复用实际专用领域，不把跳过、隐形标记或实例任务误当成普通NPC接取。
internal static class QuestDedicatedCoverage
{
    public static ImmutableDictionary<int,QuestDedicatedCoverageEntry> Inspect(QuestCatalog quests, QuestConnectionCatalog? connections, OperationHopeCatalog? hope = null)
    {
        var result = ImmutableDictionary.CreateBuilder<int,QuestDedicatedCoverageEntry>();
        // 仅统计已接线的实例专用路径；目录必须与本次任务快照共享，绝不改变普通接取语义。
        if (hope is not null && hope.Mazes.Count > 0)
            foreach (var mission in hope.Missions.Values)
                if (quests.TryGet(mission.Quest.Id,out var quest) && ReferenceEquals(quest,mission.Quest) &&
                    OperationHopeCatalog.SupportsMission(quest) && hope.Mazes.Keys.All(ordinal =>
                        OperationHopePlan.ReachableMissions(hope,ordinal).Contains(quest.Id)))
                    result[quest.Id] = new([],false,["operation-hope:1598/16/8523"]) { InstanceMission = true };
        if (connections is null) return result.ToImmutable();
        foreach (var unit in connections.Units)
        {
            if (!connections.TryGet(unit.ParentKey,out var registered) || !ReferenceEquals(unit,registered) ||
                unit.QuestionQuestId is not { } id || unit.QuickClear is not { } quick ||
                !quests.TryGet(id,out var question) || question.Definition is not { } definition ||
                question.MinimumLevel is < 1 or > 95 || definition.Job is not (null or "[all]")) continue;
            // 虚拟上下文仅检查已实现的资源合同，不持久化、不绕过线上角色的真实资格。
            var required = definition.PrerequisiteGroups.FirstOrDefault(group => group.Length > 0);
            if (definition.RequiredAnswers.Any(row => row.Answer is < 0 or > 254) ||
                definition.RequiredAnswers.GroupBy(row => row.QuestId).Any(group => group.Count() != 1)) continue;
            var candidate = new Character(1,0,"Coverage",1,new(38,0,450,250),null)
            {
                Level = (byte)question.MinimumLevel,
                CompletedQuests = (required.IsDefault ? ImmutableArray<int>.Empty : required).Concat(definition.RequiredAnswers.Select(row => row.QuestId)).Distinct().ToArray(),
                QuestAnswers = definition.RequiredAnswers.ToImmutableDictionary(row => row.QuestId,row => (byte)row.Answer),
                ActiveMainQuests = [new(0,checked((ushort)id),QuestQuestionProgress.Unanswered) { AcceptanceId = Guid.NewGuid() }]
            };
            if (!QuestQuickClearPlan.TryCreate(candidate,checked((uint)unit.ParentKey),quests,connections,out var plan,out _)) continue;
            Add(id,quick.Answer,false,$"quick-clear:1714/{unit.ParentKey}");
            var marker = quests.Get(plan!.MarkerQuestId);
            if (marker is { Grade: "[invisible]", Condition: "[meet npc]", SubType: -1, Npc: -1, CompleteNpc: -1,
                IntData: [-1], MainQuest: null } && QuestRelationProgress.IsAccountSyncMarker(marker))
                Add(marker.Id,null,true,$"account-marker:quick-clear/{unit.ParentKey}");
        }
        return result.ToImmutable();

        void Add(int id,int? answer,bool marker,string path)
        {
            var old = result.GetValueOrDefault(id) ?? new([],false,[]);
            result[id] = new(answer is { } value ? old.Answers.Add(value).Distinct().Order().ToImmutableArray() : old.Answers,
                old.AccountMarker || marker,old.Paths.Add(path).Distinct().Order().ToImmutableArray());
        }
    }
}
