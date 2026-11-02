using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 仅恢复原生频道要求的真实首步；后继任务编号没有证据，不据路径猜编号。
internal static class QuestStrongway2019Entry
{
    public const int Id = QuestCatalog.Strongway2019QuestId;
    public const string ResourcePath = QuestCatalog.Strongway2019QuestPath;

    public static bool Supports(StoryQuest quest) =>
        quest is { Id: Id, Path: ResourcePath, Grade: "[special]", Difficulty: "G", Npc: 400001051, CompleteNpc: -1,
            MinimumLevel: 95, MaximumLevel: 99, Condition: "[meet npc]", SubType: 1, IntData: [400001051],
            MainQuest: null, DungeonIndex: null, DungeonParameter: null, RewardType: "[item]",
            DeclaredGoldMultiple: null, DeclaredIgnoreLevelForExperience: null, Attributes.Count: 0,
            CinematicPath: null, CinematicMapId: null, Prerequisites.Count: 0, MapNpcIds.Count: 0,
            Rewards: [{ Type: 0, Text: null, Value: 0 }, { Type: 0, Text: null, Value: 1 }] } &&
        quest.Definition is { Diagnostics.Length: 0, IsEvent: true, Job: "[all]", GrowType: -1, JobChange: 0,
            ExposedByNpc: true, AccountQuest: false, CannotGiveup: false, GiveupRule: 0, HardcoreOnly: false,
            CreatureKind: -1, CreatureLevel: -1, FirstExposedByNpc: null, MinimumClearCount: null,
            RewardItemTradeInstance: false, GradeModifiers.Length: 0, PrerequisiteGroups.Length: 0,
            RequiredAnswers.Length: 0, Collisions.Length: 0, AccountCollisions.Length: 0,
            TargetCharacters.Length: 0, NonTargetCharacters.Length: 0, Dungeons.Length: 0,
            HideOnCompletedQuests.Length: 0, ExpertJobLevel.Length: 0, RelationQuests.Length: 0,
            Exceptions.Length: 0, SelectRewards.Length: 0, AcceptanceRewards.Length: 0,
            NpcVisibility.Length: 0, AlternativeNpcs.Length: 0, Drops.Length: 0 } definition &&
        definition.Values("event") is [{ Type: 0, Text: null, Value: 1 }] &&
        definition.Values("imagine level") is [{ Type: 0, Text: null, Value: 95 }] &&
        definition.Fields.All(field => field.Children.Length == 0 && field.Name is
            "grade" or "difficulty" or "npc index" or "complete npc index" or "job" or "grow type" or "level" or
            "imagine level" or "event" or "type" or "sub type" or "int data" or "name" or "condition message" or
            "depend message" or "solve message" or "reward type" or "reward int data" or "reward selection int data") &&
        definition.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() == definition.Fields.Length &&
        QuestCoverage.HasOrdinaryDeclarations(quest);
}
