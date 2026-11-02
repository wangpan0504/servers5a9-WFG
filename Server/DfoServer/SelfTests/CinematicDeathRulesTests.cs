using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CinematicDeathRulesTests
{
    public static void Run()
    {
        int start = Count;
        var names = new List<string> { "[die cinematic]", "[/die cinematic]", "down", "up", "[hp max]" };
        string Resolve(int id) => names[id];
        PvfToken tag = new(3,0), end = new(3,1), down = new(6,2);
        foreach (int id in new[] { 17,8241 })
        {
            PvfToken movie = new(0,id);
            That(DungeonCinematicBossTerminalRules.DieCinematic([tag,movie,end],Resolve) == id &&
                DungeonCinematicBossTerminalRules.DieCinematic([tag,movie,down,end],Resolve) == id,
                "无方向旧格式与显式down均保留实际演出编号");
            foreach (PvfToken[] tokens in new PvfToken[][]
            {
                [tag,movie,new(6,3),end], [tag,movie,down,new(0,1),end],
                [tag,new(0,0),down,end], [tag,movie,down,end,tag,movie,end]
            })
                That(DungeonCinematicBossTerminalRules.DieCinematic(tokens,Resolve) is null,
                    "未知方向、多余参数、非法编号或歧义声明不获得换层资格");
        }
        foreach (bool explicitActor in new[] { false,true })
        {
            object[] prefix = explicitActor ? ["[ACTOR]","[TYPE]","[CHARACTER]","[INDEX]",0,"[POS]",0,0,"[/ACTOR]"] : [];
            var values = new object[] { "[MAP]",77,"[TYPE]","[SCENARIO]","[SCENE]","[BEHAVIOR]" }.Concat(prefix)
                .Concat(new object[] { "[CHANGE MAP]","[X MIN]",1,"[X MAX]",1,"[Y MIN]",2,"[Y MAX]",2,
                    "[Z MIN]",0,"[Z MAX]",0,"[CHARACTER VIEW]",1,"[/CHANGE MAP]","[/BEHAVIOR]","[/SCENE]" });
            var movie = values.Select(value =>
            {
                if (value is int n) return new PvfToken(0,n);
                names.Add((string)value); return new PvfToken(3,names.Count-1);
            }).ToArray();
            That(DungeonCinematicBossTerminalRules.DeathChangesLayer(77,movie,Resolve) &&
                !DungeonCinematicBossTerminalRules.DeathChangesLayer(78,movie,Resolve) &&
                !DungeonCinematicBossTerminalRules.DeathChangesLayer(77,[..movie,..movie.Take(2)],Resolve),
                "死亡演出支持隐式全局或显式角色换层，但不接受错地图或重复声明");
        }
        Console.WriteLine($"PASS {Count-start} 死亡演出旧格式、down及非法声明断言");
    }
}
