using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using PvfLib;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidObjectTests
{
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        var strings = new List<string>();
        string Resolve(int index) => strings[index];
        PvfToken[] Tokens(params object[] values) => values.Select(value => value is int number ? new PvfToken(0, number) : Text((string)value)).ToArray();
        PvfToken Text(string text) { strings.Add(text); return new(3, strings.Count - 1); }
        foreach (int symbol in new[] { 117, 118, 119 })
        {
            var action = Tokens("[TRIGGER]", "[ON SET ACTION]", "[DO BEHAVIOR]", "[ME]", 0, "[/TRIGGER]",
                "[BEHAVIOR]", "[RAID SET SYMBOL]", symbol, "[+]", 1, "[/BEHAVIOR]");
            That(RaidObjectEvidence.SetActionIncrement(action, Resolve) == symbol, "直接ON SET ACTION单次增量声明");
            var destroy = action.ToArray(); destroy[1] = Text("[ON DESTROY]");
            That(RaidObjectEvidence.SetActionIncrement(destroy, Resolve) is null, "旧非raid ON DESTROY不能借用");
            var subtract = action.ToArray(); subtract[9] = Text("[-]");
            That(RaidObjectEvidence.SetActionIncrement(subtract, Resolve) is null, "减HP不是对象增量");
            var repeat = action.Concat(action).ToArray();
            That(RaidObjectEvidence.SetActionIncrement(repeat, Resolve) is null, "重复trigger/behavior拒绝");
        }
        That(RaidObjectEvidence.UnconditionalPassiveObjects(Tokens("[passive object]", 64249, 1, 2, 0, "[/passive object]"), Resolve), "完整根普通对象块");
        That(!RaidObjectEvidence.UnconditionalPassiveObjects(Tokens("[condition]", "[passive object]", 64249, 1, 2, 0,
            "[/passive object]", "[/condition]"), Resolve), "外层条件不能剥离成静态出生");
        That(!RaidObjectEvidence.UnconditionalPassiveObjects(Tokens("[condition]", "[passive object]", 64249, 1, 2, 0,
            "[/passive object]"), Resolve), "缺失外层闭合也不能降为根字段");
        That(!RaidObjectEvidence.UnconditionalPassiveObjects(Tokens("[passive object]", 64249, 1, 2, 0), Resolve), "普通对象缺失闭合拒绝");
        That(!RaidObjectEvidence.UnconditionalPassiveObjects(Tokens("[passive object]", 64249, 1, 2, "[/passive object]"), Resolve), "截断行拒绝");
        PvfToken StringValue(string value) { strings.Add(value); return new(6,strings.Count-1); }
        var shadow = Tokens("[shadow type]").Concat(new[]{StringValue("[mirror]")}).ToArray();
        var rootObjects = Tokens("[passive object]",64250,1116,174,0,"[/passive object]");
        That(RaidObjectEvidence.UnconditionalPassiveObjects(shadow.Concat(rootObjects).ToArray(),Resolve),
            "74113同形根shadow type单字符串mirror不包裹后续普通对象块");
        foreach(var bad in new[]{
            Tokens("[shadow type]").Concat(new[]{StringValue("[unknown]")}).ToArray(),
            Tokens("[shadow type]",1), Tokens("[shadow type]","[mirror]"),
            shadow.Concat(new[]{StringValue("[mirror]")}).ToArray(),
            shadow.Concat(shadow).ToArray(), shadow.Concat(Tokens("[/shadow type]")).ToArray() })
            That(!RaidObjectEvidence.UnconditionalPassiveObjects(bad.Concat(rootObjects).ToArray(),Resolve),
                "shadow未知值/非字符串/多值/重复/块形状仍拒绝，不泛化视觉tag白名单");
        That(!RaidObjectEvidence.UnconditionalPassiveObjects(Tokens("[condition]").Concat(shadow).Concat(rootObjects)
            .Concat(Tokens("[/condition]")).ToArray(),Resolve), "shadow不能把嵌套条件中的对象提升为根出生");
        That(!RaidObjectEvidence.UnconditionalPassiveObjects(Tokens("[animation]").Concat(shadow)
            .Concat(Tokens("[/animation]")).Concat(rootObjects).ToArray(),Resolve),
            "即使外层是已知块，嵌套shadow仍不能借root scalar白名单获准");
        var etc = Tokens("[etc action]");
        strings.Add("Action/direct.act");
        etc = [.. etc, new(6, strings.Count - 1), Text("[/etc action]")];
        That(RaidObjectEvidence.EtcActions(etc, Resolve) is ["Action/direct.act"], "ETC路径序号保留");
        Reject<InvalidDataException>(() => RaidObjectEvidence.EtcActions(etc.Concat(etc).ToArray(), Resolve), "重复ETC不猜覆盖");
        var map = new DungeonMap("map", new(1, 1), 77, "map/fixture.map");
        var another = map with { Position = new(2, 1) };
        var empty = map with { Position = new(0, 0) };
        var maze = new DungeonMaze(3, 3, 3, "", [map.Position], [another.Position], [map, another, empty], []);
        LukeObjectRoom Key(DungeonMap room) => new(10, 3, room.Kind, room.Position, room.Id, room.Path);
        var catalog = new LukeRaidObjectCatalog(new Dictionary<LukeObjectRoom, ImmutableArray<LukeObjectBinding>>
        {
            [Key(map)] = [new(Key(map), 10, 64249, 117, 0, "fixture.act")],
            [Key(another)] = [new(Key(another), 10, 64249, 117, 0, "fixture.act")],
            [Key(empty)] = []
        }.ToImmutableDictionary(), []);
        var run = Guid.NewGuid();
        var registration = new LukeRaidObjectRegistration(run, 10, maze, catalog);
        var script = AntonRaidBattleTests.Script(10, 20);
        script = script with { Symbols = script.Symbols.Add("[object control]", 8), Phases = script.Phases.SetItem(0,
            script.Phases[0] with { Rules = [
                new([new("[on change symbol]", 0, null, "[object control]", null, null, []),
                    new("[if]", 0, null, "[object control]", null, null, [1])],
                    [new("[set dungeon state]", 0, 10, null, RaidDungeonState.Close, null, [])]),
                new([new("[on change symbol]", 0, null, "[object control]", null, null, []),
                    new("[if]", 0, null, "[object control]", null, null, [2])],
                    [new("[set dungeon state]", 0, 10, null, RaidDungeonState.Open, null, [])])] }) };
        var battle = new AntonRaidBattle(script, kind: RaidKind.Luke);
        foreach (var actor in actors) That(battle.JoinStart(actor), "原Battle登记actor");
        var rows = actors.Take(4).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
        using (var token = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 10, rows, registration)!) token.Install();
        var foreignRun = Guid.NewGuid();
        var foreign = rows.Select(row => (row.Actor, row.Identity with { InstanceId = foreignRun })).ToArray();
        var foreignBattle = new AntonRaidBattle(AntonRaidBattleTests.Script(10, 20), kind: RaidKind.Luke);
        foreach (var actor in actors) That(foreignBattle.JoinStart(actor), "另一Battle登记同一当前actor");
        That(foreignBattle.ReserveDungeon(new(foreignBattle.Instance, 0, 1, RaidKind.Luke), 10, foreign, registration) is null,
            "潜在登记绑定原Run不能借给其它Run");
        var initial = battle.View;
        battle.ObserveLukeRun(run, rows, 77, map);
        That(registration.ObservedObjects == 1, "原Run四人共享一次潜在登记");
        var later = rows.Select(row => (row.Actor, row.Identity with { Epoch = 2 })).ToArray();
        battle.ObserveLukeRun(run, later, 77, map);
        That(registration.ObservedObjects == 1, "回同房新epoch不重复登记");
        var moved = rows.Select(row => (row.Actor, row.Identity with { Epoch = 3 })).ToArray();
        battle.ObserveLukeRun(run, moved, 77, another);
        That(registration.ObservedObjects == 2, "同MAP不同迷宫格分别登记");
        battle.ObserveLukeRun(Guid.NewGuid(), moved, 77, map);
        That(registration.ObservedObjects == 2, "其它Run不能修改原登记");
        battle.ObserveLukeRun(run, rows, 77, empty);
        That(registration.ObservedRooms == 2, "旧epoch不能新增空房登记");
        That(initial.Symbols.SequenceEqual(battle.View.Symbols), "加载登记不产销毁事实或写符号");
        long generation = battle.View.DungeonGenerations[10];
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[object control]", 1);
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[object control]", 2);
        That(battle.View.DungeonGenerations[10] > generation, "目标关闭重开确实换代");
        battle.ObserveLukeRun(run, rows.Select(row => (row.Actor, row.Identity with { Epoch = 4 })).ToArray(), 77, empty);
        That(registration.ObservedRooms == 2, "旧目标generation的原Run不为新目标登记房间");
        That(registration.Observe(empty) is { IsEmpty: true }, "已审无对象不同于未覆盖null");
        That(registration.Observe(map with { Path = "map/other.map" }) is null, "同ID异路径不继承证据");
        That(registration.Observe(map with { LayerIndex = 0 }) is null, "未知layer不继承普通出现");
        var independent = new LukeRaidObjectRegistration(Guid.NewGuid(), 10, maze, catalog);
        That(independent.ObservedObjects == 0, "不同Run登记生命周期隔离");
        const string spread = "passiveobject/actionobject/map/luke_raid/phase1/sanctum_of_spread/";
        var covered = new LukeObjectRoom(5056,0,"map",new(0,0),74171,"map/fixture-spread.map");
        var primary = new LukeObjectBinding(covered,8,64252,119,1,spread+"action/censor_machine_broken_front_raid.act");
        var front = new LukeObjectMissingAction(covered,8,64252,spread+"censor_machine_front_raid.obj",2,
            spread+"action/censor_machine_broken_forever_front_raid.act");
        var back = new LukeObjectMissingAction(covered,7,64287,spread+"censor_machine_back_raid.obj",2,
            spread+"action/censor_machine_broken_forever_back_raid.act");
        var coveredRooms = ImmutableDictionary<LukeObjectRoom,ImmutableArray<LukeObjectBinding>>.Empty.Add(covered,[primary]);
        var classified = new LukeRaidObjectCatalog(coveredRooms,[front.Issue,back.Issue]) {MissingActions=[front,back]};
        That(classified.Issues.Length==2 && classified.RouteWarnings.Length==2 && classified.BlockingRouteIssues.IsEmpty,
            "完整房间/action1下精确两OBJ的未消费ETC2仅作类型化warning，诊断不删除");
        foreach(var unknown in new[]{front with {ActionOrdinal=1},front with {ObjectId=999},front with {ObjectOrdinal=9},
            front with {ObjectPath="other.obj"},front with {ActionPath=spread+"action/unknown.act"},
            front with {Room=covered with {MazeOrdinal=1}}})
        {
            var rejected = new LukeRaidObjectCatalog(coveredRooms,[unknown.Issue]) {MissingActions=[unknown]};
            That(rejected.RouteWarnings.IsEmpty && rejected.BlockingRouteIssues.Length==1,
                "未知OBJ/动作/路径/源ordinal/未覆盖房间不能借ETC2分类放行");
        }
        var missingRequired = new LukeRaidObjectCatalog(coveredRooms.SetItem(covered,[primary with {ActionOrdinal=0}]),[front.Issue])
            {MissingActions=[front]};
        That(!missingRequired.BlockingRouteIssues.IsEmpty,"缺已证必需action1不授予非阻断分类");
        var unknownIssue = new LukeRaidObjectCatalog(coveredRooms,[front.Issue,"unrecognized-required-resource"]) {MissingActions=[front]};
        That(unknownIssue.BlockingRouteIssues.SequenceEqual(new[]{"unrecognized-required-resource"}),"非消费warning不遮蔽其它必需/未知缺口");
        Console.WriteLine($"PASS {Count-before} 卢克静态对象潜在绑定、回房身份与非销毁边界断言");
    }
    internal static void RunResources(PvfArchive pvf, RaidDungeonCatalog entry, LukeRaidRunPolicy policy)
    {
        var choices = policy.Mazes.Where(row => row.Phase == 0).Select(row => (row.Dungeon, row.MazeOrdinal)).ToArray();
        var catalog = LukeRaidObjectCatalog.Load(pvf, entry, SelfTestCatalogs.Rooms(pvf), choices);
        That(catalog.BindingCount == 6, "当前受控P1三迷宫只登记六个直接前部对象候选");
        string[] expectedIssues = ["luke-object-action-reference-missing:74171:64252:2", "luke-object-action-reference-missing:74171:64287:2",
            "luke-object-action-reference-missing:74172:64252:2", "luke-object-action-reference-missing:74172:64287:2"];
        That(catalog.Issues.Order().SequenceEqual(expectedIssues.Order()),
            "目录只保留当前四处ETC2缺文件诊断，无其它未识别MAP缺口: " + string.Join(";", catalog.Issues));
        foreach (var expected in new[] { (5054,74091,64249,117,10), (5054,74094,64249,117,11),
            (5055,74130,64251,118,12), (5055,74132,64251,118,9),
            (5056,74171,64252,119,8), (5056,74172,64252,119,8) })
        {
            var choice = choices.Single(row => row.Dungeon == expected.Item1);
            var maze = entry.Find(choice.Dungeon)!.Mazes.Single(row => row.SourceOrdinal == choice.MazeOrdinal);
            var map = maze.MapSpecification!.Concat(maze.MapAlternatives).Single(row => row.Id == expected.Item2);
            var rows = catalog.Find(new(choice.Dungeon, choice.MazeOrdinal, map.Kind, map.Position, map.Id, map.Path));
            That(rows is { Length: 1 } && rows.Value[0].ObjectId == expected.Item3 && rows.Value[0].Symbol == expected.Item4 &&
                rows.Value[0].Ordinal == expected.Item5, "登记OBJ、源ordinal与直接ACT声明精确匹配，不计后部64287");
        }
    }

}
