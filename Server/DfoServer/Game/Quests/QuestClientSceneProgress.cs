using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Quests;

// 匹配客户端范围type64及演出type65使用各自终末事件；坐标与演出入口由当前服务端场景约束。
internal static class QuestClientSceneProgress
{
    public static bool TownRange(StoryQuest quest) => quest.Condition == "[reach the range]" &&
        (quest.SubType == 1 && quest.IntData.Count == 6 && quest.IntData[0] is >= 0 and <= 255 && quest.IntData[1] is >= 0 and <= 255 &&
            quest.IntData[2] >= 0 && quest.IntData[3] >= 0 && quest.IntData[4] > 0 && quest.IntData[5] > 0 ||
         quest.SubType == 0 && quest.IntData is [> 0, >= 0, >= 0]);

    public static bool Cinematic(StoryQuest quest, out byte town, out byte area)
    {
        town = area = 0;
        if (quest.Condition != "[look cinematic]" || quest.SubType != -1 || quest.IntData is not [> 0] ||
            quest.CinematicPath is null || quest.Definition is not { } definition) return false;
        var accept = definition.Values("auto accept"); var guide = definition.Values("go area guide");
        var npcGuide = definition.Values("go guide");
        // Full03 04199C42保留城镇/区域/NPC引导；该入口不要求额外声明auto accept。
        if (npcGuide is [{ Type: 0, Value: >= 0 and <= 255 }, { Type: 0, Value: >= 0 and <= 255 },
            { Type: 0, Value: > 0 }, { Type: 0, Value: -1 }, { Type: 6, Text: not null }])
        {
            town = (byte)npcGuide[0].Value; area = (byte)npcGuide[1].Value; return true;
        }
        if (accept is not [{ Type: 6, Text: "enter town" }, { Type: 0, Value: >= 0 and <= 255 }] ||
            guide is not [{ Type: 0 }, { Type: 0, Value: >= 0 and <= 255 }, { Type: 0, Value: >= 0 },
                { Type: 0, Value: >= 0 }, { Type: 6, Text: not null }] || guide[0].Value != accept[1].Value) return false;
        town = (byte)accept[1].Value; area = (byte)guide[1].Value; return true;
    }

    // 副本演出没有城镇引导。地图号来自演出文件，任务自己声明要进的地下城。
    public static bool DungeonCinematic(StoryQuest quest) => quest.Condition == "[look cinematic]" && quest.SubType == -1 &&
        quest.IntData is [> 0] && quest.CinematicPath is not null && quest.CinematicMapId is > 0 &&
        quest.Definition is { Dungeons.Length: > 0 } && !Cinematic(quest, out _, out _);

    public static bool Matches(Character character, StoryQuest quest, TownCatalog towns)
    {
        var position = character.Position;
        var completed = QuestEligibility.Completed(character);
        if (!towns.IsPosition(position,completed)) return false;
        if (TownRange(quest))
        {
            if (quest.SubType == 1)
                return position.Town == quest.IntData[0] && position.Area == quest.IntData[1] &&
                    position.X >= quest.IntData[2] && position.Y >= quest.IntData[3] &&
                    position.X < (long)quest.IntData[2] + quest.IntData[4] && position.Y < (long)quest.IntData[3] + quest.IntData[5];
            // Full03 02012BF8/02A441A0：目标NPC id精确匹配，两个方向容许值各加40，边界包含。
            return towns.Areas.TryGetValue((position.Town,position.Area),out var currentArea) && currentArea.Map(completed).Npcs.Any(npc =>
                npc.Id == quest.IntData[0] && Math.Abs((long)position.X - npc.X) <= (long)quest.IntData[1] + 40 &&
                Math.Abs((long)position.Y - npc.Y) <= (long)quest.IntData[2] + 40);
        }
        if (!Cinematic(quest,out byte town,out byte area) || position.Town != town || position.Area != area) return false;
        var guide = quest.Definition!.Values("go guide");
        return guide.Length == 0 || towns.Areas[(town,area)].Map(completed).Npcs.Any(npc => npc.Id == guide[2].Value);
    }

    public static int ResolveNpc(Character character, int npcId, StoryQuest quest)
    {
        int resolved = npcId;
        // Full03 02282450始终比较原参数，成立的后续行覆盖结果，不递归使用上次替代值。
        foreach (var row in quest.Definition?.AlternativeNpcs ?? [])
            if (row.Source == npcId && (character.CompletedQuests.Contains(row.CompletedQuest) ||
                character.AccountCompletedQuests.Contains(row.CompletedQuest)))
                resolved = row.Target;
        return resolved;
    }

    public static bool DialogueNpc(Character character, int npcId, StoryQuest quest, QuestCatalog quests, TownCatalog towns)
    {
        npcId = ResolveNpc(character,npcId,quest);
        if (npcId <= 0) return false;
        var position = character.Position;
        if (towns.Areas.TryGetValue((position.Town,position.Area),out var area) &&
            area.Map(QuestEligibility.Completed(character)).Npcs.Any(npc => npc.Id == npcId)) return true;
        // 演出后的原生对话可引用临时演员；只接受本任务直接前置演出的solve message和同一真实场景。
        return quest.Definition is { } definition && definition.PrerequisiteGroups.SelectMany(group => group).Distinct().Any(id =>
            character.CompletedQuests.Contains(id) && quests.TryGet(id,out var previous) && Cinematic(previous,out _,out _) &&
            Matches(character,previous,towns) && previous.Definition!.Values("solve message").Where(value => value.Text is not null)
                .Any(value => Regex.Matches(value.Text!,@"<npc::(\d+)>").Any(match => int.TryParse(match.Groups[1].Value,out int actor) && actor == npcId)));
    }
}
