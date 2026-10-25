using static PvfLib.Tests.TestCheck;
namespace PvfLib.Tests;
internal static class OperationHopeFileTests
{
    public static void Run()
    {
        string[] tags = ["[mission info list]", "[/mission info list]", "[area]", "[/area]", "[map]", "[/map]", "[next area]", "[/next area]", "[mission]", "[/mission]", "[fatigue value]", "[need fatigue]", "[unknown]"];
        string Resolve(int id) => tags[id];
        PvfToken[] valid = [new(3,0),new(0,6171),new(3,1),new(3,2),new(0,0),new(3,4),new(0,550040),new(3,6),new(0,1),new(0,2),new(3,7),new(3,5),new(3,8),new(0,6171),new(0,0),new(3,9),new(3,3),new(3,10),new(0,4),new(3,11),new(0,4)];
        var parsed = OperationHopeFile.Parse(valid, Resolve);
        That(parsed.MissionIds.SequenceEqual(new[] { 6171 }) && parsed.Areas.Single().Maps.Single().NextAreas.SequenceEqual(new[] { 1,2 }), "代号希望保留区域任务及分支原序");
        That(parsed.FatigueValue == 4 && parsed.NeedFatigue == 4 && parsed.Areas[0].MissionValue == 0, "代号希望疲劳及未知任务值原样保留");
        That(OperationHopeFile.Parse(valid.Concat(new PvfToken[] { new(3,12), new(0,77) }).ToArray(),Resolve).Fields.Last().Name == "unknown", "未知顶层概率等字段保留，不猜单位");
        Reject<InvalidDataException>(() => OperationHopeFile.Parse(valid.Where((_,i) => i != 16).ToArray(), Resolve));
        Reject<InvalidDataException>(() => OperationHopeFile.Parse(valid.Concat(new PvfToken[] { new(3,10),new(0,4) }).ToArray(), Resolve));
        Reject<InvalidDataException>(() => OperationHopeFile.Parse(valid.Select((t,i) => i == 13 ? new PvfToken(6,0) : t).ToArray(),Resolve));
        Reject<InvalidDataException>(() => OperationHopeFile.Parse(valid.Where((_,i) => i != 14).ToArray(),Resolve));
        Reject<InvalidDataException>(() => OperationHopeFile.Parse(valid.Where((_,i) => i != 10).ToArray(),Resolve));
        Reject<InvalidDataException>(() => OperationHopeFile.Parse(valid.Where((_,i) => i is not (19 or 20)).ToArray(),Resolve));
        Reject<InvalidDataException>(() => OperationHopeFile.Parse([],Resolve));
        DungeonDeclarations();
    }
    private static void DungeonDeclarations()
    {
        string[] names = ["[maze info]", "[hidden boss maze]", "[clear condition]", "[/clear condition]", "[clear map]", "list",
            "[operation hope dungeon]", "[warp map condition]", "[/warp map condition]", "[source grid pos]", "[dest grid pos]", "[continuous dungeon]"];
        string Resolve(int id) => names[id];
        PvfToken[] first = [new(3,0),new(3,2),new(3,4),new(6,5),new(0,2),new(0,10),new(0,11),new(0,1),new(3,3)];
        PvfToken[] second = [new(3,0),new(3,1),new(3,2),new(3,4),new(6,5),new(0,1),new(0,12),new(0,7),new(3,3)];
        PvfToken[] tail = [new(3,6),new(0,1),new(3,7),new(3,9),new(0,0),new(0,0),new(3,10),new(0,1),new(0,1),new(3,8),new(3,11),new(0,1)];
        var valid = first.Concat(second).Concat(tail).ToArray();
        var parsed = OperationHopeDungeonFile.Parse(valid,Resolve);
        That(parsed.Mazes.Length == 2 && !parsed.Mazes[0].HiddenBoss && parsed.Mazes[1].HiddenBoss && parsed.Warps.Length == 1, "全局传送不归属最后迷宫，隐藏迷宫标记保留");
        That(parsed.Mazes[0].ClearMaps.SequenceEqual(new[] { 10,11 }) && parsed.Mazes[1].DeclaredCount == 7, "原始清图列表及尾整数无损保留，解析器不推测运行阈值");
        Reject<InvalidDataException>(() => OperationHopeDungeonFile.Parse(first.Concat(tail[..^2]).ToArray(),Resolve));
        Reject<InvalidDataException>(() => OperationHopeDungeonFile.Parse(first.Concat(first.Skip(1)).Concat(tail).ToArray(),Resolve));
        Reject<InvalidDataException>(() => OperationHopeDungeonFile.Parse(first.Take(7).Concat(first.Skip(8)).Concat(tail).ToArray(),Resolve));
        Reject<InvalidDataException>(() => OperationHopeDungeonFile.Parse(first.Concat(tail.Where((_,i) => i != 9)).ToArray(),Resolve));
        Reject<InvalidDataException>(() => OperationHopeDungeonFile.Parse(first.Concat(tail.Where((_,i) => i is not (6 or 7 or 8))).ToArray(),Resolve));
    }

}
