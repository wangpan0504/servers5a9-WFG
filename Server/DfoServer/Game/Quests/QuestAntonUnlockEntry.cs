using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Quests;

// 本服明确恢复这一条旧解锁任务的城镇入口（#897：任意真实城镇地图接取与交付，不再限奈恩所在图）；
// 不改原资源，也不把频道时段当作团本通关。
internal static class QuestAntonUnlockEntry
{
    public static bool Supports(StoryQuest quest) =>
        quest is { Id: 12829, Path: "n_quest/anton/anton_dimension_enter.qst", Grade: "[side]", Difficulty: "F",
            Npc: 332, CompleteNpc: -1, MinimumLevel: 85, MaximumLevel: 99, MainQuest: null,
            Condition: "[seeking]", SubType: -1, IntData: [3262, 100], RewardType: "[item]",
            DeclaredGoldMultiple: 10, DeclaredIgnoreLevelForExperience: 1 } &&
        quest.Definition is { Diagnostics.Length: 0, ExposedByNpc: false, AccountQuest: false,
            Job: "[all]", GrowType: -1, JobChange: 0, IsEvent: false, FirstExposedByNpc: null,
            AcceptanceRewards.Length: 0, SelectRewards.Length: 0 } definition &&
        definition.PrerequisiteGroups is [{ } prerequisite] && prerequisite.SequenceEqual([3779]) &&
        quest.Rewards.All(row => row.Type == 0) &&
        quest.Rewards.Select(row => row.Value).SequenceEqual([10095688, 3, 10095689, 3, 10095690, 3, 10095687, 1]);

    // 真实城镇地图（含赛丽亚38/1等城镇房间，坐标落在可走区域或门内）；等级、前置、材料与一次完成仍由原规则校验。
    public static bool AtTown(Character character, TownCatalog towns) =>
        towns.IsPosition(character.Position, QuestEligibility.Completed(character));
}
