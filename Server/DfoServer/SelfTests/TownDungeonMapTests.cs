using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TownDungeonMapTests
{
    public static void Run()
    {
        int checks = Count;
        string[] tags = ["[dungeon]", "[/dungeon]", "[in progress]", "[is clear quest]", "[extra map]", "[future condition]"];
        PvfToken Tag(int index) => new(3,index);
        PvfToken Number(int value) => new(0,value);
        TownDungeonMap Read(params PvfToken[] rows) => TownDungeonMap.Read(7,"synthetic.wdm",rows,id => tags[id]);

        var map = Read(Tag(0),Number(53),Number(-1),Number(242),Tag(2),Number(3355),
            Number(7146),Tag(3),Number(3351),Number(242),Tag(2),Number(7915),Tag(1),
            Tag(4),Tag(0),Number(9582),Number(-1),Tag(1));
        That(map.Error is null && map.Options.SequenceEqual(new TownDungeonOption[]
        {
            new(53,-1,false), new(242,3355,true), new(7146,3351,false), new(242,7915,true)
        }),"完成任务标记不阻断普通入口，保留每项任务条件、重复地下城和资源顺序");
        That(map.Options.All(option => option.DungeonId != 9582),"普通世界地图不混入extra map入口");
        That(Read(Tag(0),Number(53),Number(3351),Tag(1)).Options.Single() == new TownDungeonOption(53,3351,false),
            "原有无标记任务条件保持完成记录语义");
        foreach (int marker in new[] { 2,3 })
        {
            Reject<NotSupportedException>(() => Read(Tag(0),Number(7146),Tag(marker),Number(-1),Tag(1)),"条件标记不能以-1取消任务要求");
            Reject<NotSupportedException>(() => Read(Tag(0),Number(7146),Tag(marker),Tag(1)),"条件标记缺少任务编号仍拒绝");
        }
        Reject<NotSupportedException>(() => Read(Tag(0),Number(7146),Tag(5),Number(3351),Tag(1)),"未知条件不转为无条件入口");
        Console.WriteLine($"PASS {Count-checks} 世界地图任务条件解析断言");
    }
}
