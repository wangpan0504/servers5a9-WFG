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
        LostLandEntrance();
        Console.WriteLine($"PASS {Count-checks} 世界地图任务条件解析断言");
    }

    static void LostLandEntrance()
    {
        const string role = "[role]\n`[golden commerce]` 0 `[item shop]` 71\n[/role]";
        That(TownCatalog.HasLostLandEntryRole(role), "乔安的零参数golden commerce角色提供菜单入口");
        foreach (string invalid in new[] { "", "[role]`[party matching]` 0[/role]", role.Replace("0 `[item", "1 `[item"),
            role.Replace("0 `[item", "-1 `[item"), role.Replace("0 `[item", "999999999999 `[item"),
            role.Replace("[/role]", ""), role + role, "[role]" + role,
            role.Replace("0 `[item", "0 `[golden commerce]` 0 `[item"),
            role.Replace("0 `[item", "0 `[golden commerce]` `[item"), role.Replace("0 `[item", "0 1 `[item") })
            That(!TownCatalog.HasLostLandEntryRole(invalid), "无角色、相邻角色、条件参数或畸形声明不授予入口");

        var map = new TownMap("westcoast.map", [new(new(0, 0, 1000, 500), [], [])], [], false, [new(146, 845, 155)]);
        var area = new TownArea(40, 0, 17, [], [], [], null, map, [], []) { DungeonWorldMap = 101 };
        var world = new TownDungeonMap(101, "worldmap/golden_road.wdm", [new(4000, -1, false)]);
        TownCatalog Catalog(TownDungeonMap selection, params uint[] npcs) => new(
            new() { [(40, 0)] = area }, new(40, 0, 800, 300), new(40, 0, 800, 300), new(0, [], []), [],
            new Dictionary<uint, uint[]>(), new Dictionary<int, TownDungeonMap> { [101] = selection })
            { LostLandEntryNpcs = npcs.ToHashSet() };
        var towns = Catalog(world, 146);
        var done = new HashSet<int>();
        That(towns.HasDungeonEntrance(area, done, 4000) && map.DungeonGates.Length == 0,
            "真实NPC菜单入口不要求不存在的街道选图门");
        That(!towns.HasDungeonEntrance(area, done, 3) && !Catalog(world).HasDungeonEntrance(area, done, 4000),
            "NPC入口不能授权普通副本，资源未声明角色时仍拒绝");
        That(!towns.HasDungeonEntrance(area with { DungeonWorldMap = 102 }, done, 4000) &&
            !towns.HasDungeonEntrance(area with { Default = map with { Npcs = [] } }, done, 4000) &&
            !towns.HasDungeonEntrance(area with { Default = map with { Error = "unavailable" } }, done, 4000),
            "错误选图、缺NPC或损坏地图均不取得菜单入口");
        foreach (var malformed in new[] { world with { Error = "unavailable" }, world with { Options = [] },
            world with { Options = [new(3, -1, false)] }, world with { Options = [new(4000, 3156, false)] },
            world with { Options = [new(4000, -1, true)] }, world with { Options = [new(4000, -1, false), new(3, -1, false)] } })
            That(!Catalog(malformed, 146).HasDungeonEntrance(area, done, 4000), "选图资源改变后不借用遗忘之地入口");
        var phase = area with { Phases = [map with { Npcs = [] }], Shifts = [new(0, 3156)] };
        That(towns.HasDungeonEntrance(phase, done, 4000) && !towns.HasDungeonEntrance(phase, new HashSet<int> { 3156 }, 4000),
            "入口NPC按当前任务阶段地图核对，不借用旧阶段NPC");
        var physical = area with { Default = map with { DungeonGates = [new(new(900, 200, 20, 100), [3156], [])] } };
        That(!towns.HasDungeonEntrance(physical, done, 3) && towns.HasDungeonEntrance(physical, new HashSet<int> { 3156 }, 3) &&
            !towns.HasDungeonEntrance(physical with { Default = physical.Default with
                { DungeonGates = [new(new(900, 200, 20, 100), [3156], [1])] } }, new HashSet<int> { 3156 }, 3),
            "普通地图门的任务与未知条件门禁保持原判定");
    }
}
