using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 超时空剧情的十二步原资源链，不把团队模式或任意episode声明视作普通任务。
internal static class QuestEpisodeEntry
{
    public static bool Supports(StoryQuest quest)
    {
        if (quest is not { Id: >=8660 and <=8671, Grade:"[episode]", MainQuest:null, MinimumLevel:94, MaximumLevel:99,
            RewardType:"[item]", Rewards:[{Type:0,Value:0},{Type:0,Value:0}] } ||
            quest.Definition is not { Diagnostics.Length:0, Job:"[all]", FirstExposedByNpc:null } definition ||
            !quest.Path.StartsWith("n_quest/quest_renewal/epic_quest/cosmofiend/",StringComparison.Ordinal) ||
            !definition.PrerequisiteGroups.SelectMany(group=>group).SequenceEqual([quest.Id==8660 ? 6045 : quest.Id-1])) return false;
        int index=quest.Id-8660;
        int[] npcs=[614,469,58,15,148,621,619,619,620,620,619,148];
        int[] targets=[469,58,15,148,618,619,59929,59933,59938,619,148,28];
        string condition=index==2 ? "[reach the range]" : index is >=6 and <=8 ? "[clear map]" : "[meet npc]";
        if (quest.Npc!=npcs[index] || quest.Condition!=condition || quest.SubType!=(index==0 ? 1 : index==2 ? 0 : -1) ||
            !quest.IntData.SequenceEqual(index==2 ? [15,100,50] : new[]{targets[index]}) ||
            (index is >=6 and <=8 ? definition.Dungeons is not [{DungeonId:5346,Parameter:-1}] : definition.Dungeons.Length!=0)) return false;
        bool town=index is >=5 and <=9, retained=index>=4;
        string[] visibility=index>=4 ? ["[accept]","[clear]"] : index==3 ? ["[clear]"] : [];
        return Flag("episode town use",town) && Flag("npc visivility not revert",retained) &&
            definition.NpcVisibility.Select(row=>row.Condition).SequenceEqual(visibility) &&
            definition.NpcVisibility.All(row=>row.Npcs.SequenceEqual([618]) &&
                row.Condition is "[accept]" or "[clear]" && row.Visibility=="[show]");
        bool Flag(string name,bool expected) => definition.Fields.Where(field=>field.Name==name).ToArray() switch
        { [] => !expected, [{Values.Length:0,Children.Length:0}] => expected, _ => false };
    }
}
