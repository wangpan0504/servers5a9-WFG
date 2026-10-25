using static PvfLib.Tests.TestCheck;
namespace PvfLib.Tests;
internal static class TownEpisodeFileTests
{
    public static void Run()
    {
        string[] names=["[episode town]","[/episode town]","[return npc]","[town go guide quest]","[/town go guide quest]","[area]","[/area]","[unknown]"];
        string Resolve(int id)=>names[id];
        PvfToken[] valid=[new(3,0),new(3,2),new(0,618),new(3,3),new(0,8646),new(0,8647),new(3,4),new(3,1)];
        var parsed=TownEpisodeFile.Parse(valid,Resolve)!;
        That(parsed.ReturnNpc==618 && parsed.GuideQuests.SequenceEqual(new[]{8646,8647}),"剧情城镇保留旧原始NPC/引导ID，不做任务编号重映射");
        That(TownEpisodeFile.Parse([],Resolve) is null && TownEpisodeFile.Parse([new(6,0)],Resolve) is null,"缺席与普通字符串不是剧情城镇声明");
        That(TownEpisodeFile.Parse([new(3,0),new(3,7),new(0,42),new(3,1)],Resolve)!.Fields.Single().Name=="unknown","未知剧情子声明保留给资源层解释");
        Reject<InvalidDataException>(()=>TownEpisodeFile.Parse(valid.Concat(valid).ToArray(),Resolve));
        Reject<InvalidDataException>(()=>TownEpisodeFile.Parse(valid[..^1],Resolve));
        Reject<InvalidDataException>(()=>TownEpisodeFile.Parse([new(3,1)],Resolve));
        Reject<InvalidDataException>(()=>TownEpisodeFile.Parse([new(3,0),new(0,1),new(3,1)],Resolve));
        Reject<InvalidDataException>(()=>TownEpisodeFile.Parse([new(3,0),new(3,2),new(0,1),new(3,2),new(0,2),new(3,1)],Resolve));
        Reject<InvalidDataException>(()=>TownEpisodeFile.Parse([new(3,0),new(3,2),new(6,0),new(3,1)],Resolve));
        Reject<InvalidDataException>(()=>TownEpisodeFile.Parse([new(3,0),new(3,3),new(6,0),new(3,4),new(3,1)],Resolve));
        Reject<InvalidDataException>(()=>TownEpisodeFile.Parse(new[]{new PvfToken(3,5)}.Concat(valid).Append(new(3,6)).ToArray(),Resolve));
    }
}
