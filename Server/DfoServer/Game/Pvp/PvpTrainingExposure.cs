using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Pvp;

internal static class PvpTrainingExposure
{
    // 本服兼容解释：普通链无声明为0，风云儿1仅专用，含两条OR前置的风卷流云2适用双方。
    // QST+7B4仅证实标签写入，未发现匹配客户端读端；不将这项解释推广到其它未知任务。
    public static bool TryRead(StoryQuest quest,out int exposure)
    {
        exposure=-1;
        bool shape=PvpTrainingProgress.Matches(quest) || quest is
            {Id:2670,Grade:"[side]",Condition:"[meet npc]",SubType:-1,Npc:157,CompleteNpc:-1} &&
            quest.IntData.SequenceEqual(new[]{157}) && quest.Definition is {Diagnostics.Length:0} || quest is
            {Id:2671,Grade:"[side]",Condition:"[meet npc]",SubType:-1,Npc:159,CompleteNpc:323} &&
            quest.IntData.SequenceEqual(new[]{323}) && quest.Definition is {Diagnostics.Length:0} || quest is
            {Id:2675,Grade:"[side]",Condition:"[meet npc]",SubType:-1,Npc:157,CompleteNpc:323} &&
            quest.IntData.SequenceEqual(new[]{323}) && quest.Definition is {Diagnostics.Length:0};
        if(!shape || quest.Definition is not {} definition) return false;
        var fields=definition.Fields.Where(field=>field.Name=="pvp private expose").ToArray();
        if(fields.Length==0 && quest.Id is >=2670 and <=2674) {exposure=0;return true;}
        if(fields is not [{Children.Length:0,Values.Length:1} field] || field.Values[0] is not {Type:0,Text:null} value)
            return false;
        int expected=quest.Id is >=2675 and <=2678 ? 1 : quest.Id==2679 ? 2 : 0;
        if(value.Value!=expected) return false;
        exposure=value.Value;
        return true;
    }

    public static bool Allows(Character character,StoryQuest quest)
    {
        if(quest.Id is <2670 or >2679) return true;
        return TryRead(quest,out int exposure) && (exposure==2 || (exposure==1)==character.IsPvpPrivate);
    }
}
