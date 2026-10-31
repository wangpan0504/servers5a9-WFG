using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 读取已有正式结算记录，不以884回传字段生成通关历史。
internal interface IQuestHiddenHistoryStore
{
    bool HasQuestDungeonClear(AccountOwners.Lease owner, Character expected, int dungeonId, int count);
}

internal static class QuestStandbyEntry
{
    // 0228A060逐组尝试hidden set，组内条件全部满足后提前结束；区域停留仍是独立AND条件。
    // 本版普雷攻坚尚无权威尝试账本，raid try分支不补造；已确认的引导通关分支可独立满足。
    internal static bool Supports(StoryQuest quest) => quest is
        { Grade: "[hidden]", MainQuest: null, Condition: "[meet npc]", SubType: -1, IntData: [8001] } &&
        quest.Definition is { FirstExposedByNpc: null } definition &&
        definition.Fields.Where(field => field.Name == "stand by area").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 0, Value: 60 }, { Type: 0, Value: 1 }, { Type: 0, Value: 1000 }] }] &&
        definition.Fields.Where(field => field.Name == "hidden set").ToArray() is { Length: 2 } sets &&
        sets.Count(field => field is { Values.Length: 0, Children: [{ Name: "raid try", Children.Length: 0,
            Values: [{ Type: 6, Text: "prey_raid" }] }] }) == 1 &&
        sets.Count(field => field is { Values.Length: 0, Children: [{ Name: "dungeon clear", Children.Length: 0,
            Values: [{ Type: 0, Value: 291100397 }, { Type: 0, Value: 1 }] }] }) == 1 &&
        !definition.Fields.Any(field => field.Name is "area" or "npcdialog" or "standbyarea" or "itemusecheck" or
            "npc dialog" or "item use check" or "reach the range");

    internal static bool AtArea(Character character) => character.Position is { Town: 60, Area: 1 };
}
