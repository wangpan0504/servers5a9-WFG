using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 官方原PVF唯一已核的星河裂缝入口任务。只把这条具名资源接到10005活动门控，
// 不把其它[event]任务或未经核对的后续任务一并开放。
internal static class QuestEpicRoad2019Entry
{
    public const int Id = 8712;
    public const string ResourcePath = "n_quest/gcontents/191210_tayberrs_road/tayberrs_road_enter.qst";

    public static bool Supports(StoryQuest quest) =>
        quest is { Id: 8712, Path: "n_quest/gcontents/191210_tayberrs_road/tayberrs_road_enter.qst", Condition: "[meet npc]", SubType: -1,
            IntData: [400001102], Npc: 400001102, CompleteNpc: -1, MinimumLevel: 95, MaximumLevel: 99,
            Prerequisites.Count: 0, MainQuest: null, DungeonIndex: null, DungeonParameter: null,
            Grade: "[special]", Difficulty: "M", RewardType: "[item]",
            Rewards: [{ Type: 0, Value: 0, Text: null }, { Type: 0, Value: 100, Text: null }],
            Definition: var definition } &&
        definition is { Diagnostics.Length: 0, IsEvent: true, AccountQuest: false, ExposedByNpc: true,
            Job: "[all]", GrowType: -1, JobChange: 0, CannotGiveup: false, GiveupRule: 0,
            HardcoreOnly: false, CreatureKind: -1, CreatureLevel: -1 } &&
        definition.Fields.Where(field => field.Name == "show quest on event").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 0, Value: 10005, Text: null }] }] &&
        definition.Values("synchronization account quest") is [{ Type: 0, Value: 1, Text: null }];
}
