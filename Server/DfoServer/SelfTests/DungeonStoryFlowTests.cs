using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonStoryFlowTests
{
    public static void Run()
    {
        int before = Count;
        foreach (byte state in new byte[] { 0,1 })
        foreach (byte type in new byte[] { 0,1,2 })
        {
            That(DungeonRequests.TryParseStoryPause([state,type],out var request) && request == new DungeonStoryPauseRequest(state,type),
                "剧情暂停保留六种原生状态组合");
            That(DungeonPackets.StoryPause(0x1234,state,type).SequenceEqual(new byte[] { 0x34,0x12,state,type }),
                "暂停应答绑定当前角色，正文为u16/u8/u8");
        }
        foreach (byte[] invalid in new byte[][] { [],[0],[0,0,0],[2,0],[0,3] })
            That(!DungeonRequests.TryParseStoryPause(invalid,out _),"无效暂停正文不转成成功应答");
        Layers(); RepeatedLayers(); Stages(); StageBaseFallback(); Entrances(); QuestObjects();
        Console.WriteLine($"PASS {Count-before} 剧情暂停、层叠、重复坐标阶段和动态任务物件断言（合成）");
    }

    static void Layers()
    {
        var start = new DungeonMap("map",new(0,0),100,"map/base.map");
        var boss = new DungeonMap("boss",new(1,0),101,"map/boss.map");
        var first = start with { Kind = "layered", Id = 102, Path = "map/layer1.map" };
        var last = first with { Id = 103, Path = "map/layer2.map" };
        var maze = new DungeonMaze(0,2,1,"BBEE",[start.Position],[boss.Position],[start,boss],[]) { LayeredMaps = [first,last] };
        var entry = new DungeonEntryPlan(99,0,777,200,0,0,maze,start,boss);
        var plans = new[] { Empty(start),Monster(boss,20),Empty(first),Empty(last) };
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,100,new(plans,[new(start.Position,boss.Position)]),authority,out var run,out _),"叠层使用同一个正式实例");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),start.Position,true).Outcome == DungeonRunOutcome.Accepted,"首层按资源入口进入");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),boss.Position).Outcome == DungeonRunOutcome.Rejected &&
            run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),start.Position,false).Outcome == DungeonRunOutcome.Rejected,
            "存在后续层时不能跳过演出返回基础房间或普通邻房");
        That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),start.Position,true).Outcome == DungeonRunOutcome.Accepted,"后续层按原序推进");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),boss.Position).Outcome == DungeonRunOutcome.Accepted && run.Snapshot.CurrentMap == boss,
            "末层清房后允许正常邻房加载");
    }

    static void RepeatedLayers()
    {
        var start = new DungeonMap("map",new(0,0),100,"map/base.map");
        var boss = new DungeonMap("boss",new(1,0),101,"map/boss.map");
        var layer = new DungeonMap("layered",boss.Position,102,"map/layer.map") { CandidateIds = [102,102] };
        var paths = new Dictionary<int,string> { [100] = start.Path,[101] = boss.Path,[102] = layer.Path };
        var maze = DungeonCatalog.ResolveMapCandidates(new(0,2,1,"BBEE",[start.Position],[boss.Position],[start,boss],[])
            { LayeredMaps = [layer] },paths,_ => true,_ => throw new InvalidOperationException());
        var entry = new DungeonEntryPlan(99,0,777,200,0,0,maze,start,maze.LayeredMaps[^1]) { BaseBossMap = boss };
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,100,new([Empty(start),Empty(boss),
            Monster(maze.LayeredMaps[0],30),Monster(maze.LayeredMaps[1],31)],[new(start.Position,boss.Position)]),authority,out var run,out _),
            "同模板的两层拥有独立正式房间和对象key");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),boss.Position);
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),boss.Position,true).Outcome == DungeonRunOutcome.Accepted,
            "重复模板首层仍沿明确层入口加载");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,30,1)).ClearFact is null,
            "首次同ID目标层清房不产生最终通关凭据");
        That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),boss.Position,true).Outcome == DungeonRunOutcome.Accepted,
            "首层清房后可以进入同模板的第二层");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.CurrentMap.LayerIndex == 1 && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 31 }),
            "第二层不继承第一层的清房和死亡缓存");
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,31,2)).ClearFact?.ClearMap == maze.LayeredMaps[1] &&
            run.ClearFact!.ConfirmedDeathEvents.Count == 2,"原序最后同ID层实际清房后才产生最终通关凭据");
    }

    static void Stages()
    {
        var strings = new Dictionary<int,string>();
        PvfToken Text(string value,byte type = 6)
        {
            int id = strings.Count+1; strings.Add(id,value); return new(type,id);
        }
        PvfToken Tag(string value) => Text("["+value+"]",3);
        var tokens = new List<PvfToken> { Tag("minimum required level"),new(0,1),Tag("basis level"),new(0,20),Tag("maze info"),
            Tag("size"),new(0,3),new(0,1),Tag("greed"),Text("BBFFEE"),Tag("map specification"),Text("map"),new(0,0),new(0,0),new(0,99) };
        for (int index = 0; index < 4; index++) tokens.AddRange([Text("map"),new(0,index%2+1),new(0,0),new(0,100+index),new(0,900+index),
            new(0,900+index),new(0,100+(index+2)%4)]);
        tokens.AddRange([Text("map"),new(0,2),new(0,0),new(0,104)]);
        tokens.AddRange([Tag("/map specification"),Tag("start map"),new(0,0),new(0,0),Tag("/start map"),Tag("boss map")]);
        for (int index = 0; index < 4; index++) tokens.AddRange([new(0,index%2+1),new(0,0)]);
        tokens.AddRange([Tag("/boss map"),Tag("quest connection"),new(0,0),new(0,200),new(0,-1)]);
        tokens.AddRange([Tag("/maze info"),Tag("boss room entrance condition"),Text("[clear action]"),Text("[time condition]"),new(0,7),new(0,9),
            Text("[hunt monster]"),new(0,1),new(0,1),new(0,4),new(0,1),Tag("warp map condition"),
            Tag("source grid pos"),new(0,1),new(0,0),Tag("dest grid pos"),new(0,0),new(0,0),Tag("/warp map condition")]);
        var paths = Enumerable.Range(99,6).Concat(Enumerable.Range(900,4)).ToDictionary(id => id,id => "map/stage"+id+".map");
        var definition = DungeonCatalog.ParseResource(777,"dungeon/stage.dgn",tokens,id => strings[id],paths,_ => true);
        var maze = definition.Mazes.Single();
        That(maze.ResourceError is null && maze.StoryStages.Select(map => map.Id).SequenceEqual([100,101,102,103]) && maze.StaticTopology.Supported,
            "重复boss坐标按资源地图行顺序生成完整阶段，不折叠为一张地图");
        That(maze.StoryStages[0].CandidateIds.SequenceEqual([100,900,900,102]),"剧情阶段沿资源首项选择，不把额外候选当作新阶段");
        var bases = maze.MapSpecification!.Where(map => map.StoryStageIndex < 0).ToArray();
        That(bases.Length == 3 && bases[1].CandidateIds.SequenceEqual([900,902]) && bases[2].CandidateIds.SequenceEqual([901,903,104]),
            "各坐标汇总基础候选、排除所有阶段首项并去重，非阶段地图行仍参与候选");
        var draws = new List<int>();
        maze = DungeonCatalog.ResolveMapCandidates(maze,paths,_ => true,count => { draws.Add(count); return count-1; });
        That(draws.SequenceEqual([2,3]) && maze.MapSpecification!.Where(map => map.StoryStageIndex < 0).Select(map => map.Id).SequenceEqual([99,902,104]),
            "基础地图按去重后的候选数抽签，阶段首项不参与抽签");
        That(maze.ConditionalMaps.Single() is { Id: 99,StoryStageIndex: -1,Position.X: 1 },
            "阶段坐标的条件传送只使用唯一普通基础模板，不选择剧情阶段");
        That(maze.StoryStages.Select(map => map.Id).SequenceEqual([100,101,102,103]) &&
            ReferenceEquals(DungeonCatalog.ResolveMapCandidates(maze,paths,_ => true,_ => throw new InvalidOperationException()),maze),
            "基础选择在入场时冻结，重访不重抽且不改变剧情阶段原序");
        var missingPaths = new Dictionary<int,string>(paths); missingPaths.Remove(900);
        That(DungeonCatalog.ResolveMapCandidates(definition.Mazes.Single(),missingPaths,_ => true,count => count-1).ResourceError is null,
            "未选中的旧候选缺失不阻断当前路线");
        Reject<InvalidDataException>(() => DungeonCatalog.ResolveMapCandidates(definition.Mazes.Single(),missingPaths,_ => true,_ => 0),
            "实际选中的基础地图缺失时不能替换为其他地图");
        var active = new ActiveMainQuest(0,200,1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(99,0,"StageFixture",0,new(38,2,1085,249),null) { ActiveMainQuests = [active] };
        var quest = QuestEligibilityTests.Quest(200) with { Condition = "[clear map]",IntData = [103] };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [maze] },character,777,200,0,out var entry,out _,quest: quest) &&
            entry!.StartMap.Id == 99 && entry.BossMap.Id == 103,"普通起点与剧情首阶段分开，最终清图目标从资源选择");
        var plans = maze.MapSpecification!.Concat(maze.ConditionalMaps).Select(map => Monster(map,map.IsConditional ? (ushort)(60+map.Position.X) :
            map.StoryStageIndex >= 0 ? (ushort)(10+map.StoryStageIndex) :
            map.Id == 99 ? (ushort)9 : (ushort)(50+map.Position.X))).ToArray();
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry!,1,100,new(plans,[new(new(0,0),new(1,0)),new(new(1,0),new(2,0))]),authority,out var run,out _),"阶段复用正式Run与完整独立对象身份");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,9,1));
        That(run.ConfirmBossEntranceOpen(),"来源实际击杀按通用目标计数开放条件传送");
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,0)).Outcome == DungeonRunOutcome.Accepted,"普通起点进入首个剧情阶段");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        for (int index = 0; index < 4; index++)
        {
            That(run.Snapshot.CurrentMap.Id == 100+index && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { (ushort)(10+index) }),
                "重访同坐标装载对应阶段，先前死亡不继承");
            var death = run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,(ushort)(10+index),index+2));
            That((death.ClearFact is not null) == (index == 3),"只有资源最终阶段可以签发通关");
            if (index == 3) continue;
            That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),new((index+1)%2+1,0)).Outcome == DungeonRunOutcome.Accepted &&
                run.Phase == SinglePlayerDungeonPhase.Loading,"阶段切换经过正常加载状态");
            run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        }
        That(run.ClearFact!.ConfirmedDeathEvents.Count == 5,"阶段通关仅包含普通起点及阶段内实际提交死亡");
    }

    static void StageBaseFallback()
    {
        var strings = new Dictionary<int,string>();
        PvfToken Text(string value,byte type = 6) { int id = strings.Count+1; strings.Add(id,value); return new(type,id); }
        PvfToken Tag(string value) => Text("["+value+"]",3);
        var tokens = new List<PvfToken> { Tag("minimum required level"),new(0,1),Tag("basis level"),new(0,20),Tag("maze info"),
            Tag("size"),new(0,2),new(0,1),Tag("greed"),Text("BBEE"),Tag("map specification") };
        for (int index = 0; index < 3; index++) tokens.AddRange([Text("map"),new(0,index%2),new(0,0),new(0,100+index)]);
        tokens.AddRange([Tag("/map specification"),Tag("start map"),new(0,0),new(0,0),Tag("/start map"),Tag("boss map")]);
        for (int index = 0; index < 3; index++) tokens.AddRange([new(0,index%2),new(0,0)]);
        tokens.AddRange([Tag("/boss map"),Tag("quest connection"),new(0,0),new(0,200),new(0,-1)]);
        var paths = Enumerable.Range(100,3).ToDictionary(id => id,id => "map/stage"+id+".map");
        var definition = DungeonCatalog.ParseResource(777,"dungeon/fallback.dgn",tokens,id => strings[id],paths,_ => true);
        var maze = DungeonCatalog.ResolveMapCandidates(definition.Mazes.Single(),paths,_ => true,_ => throw new InvalidOperationException());
        That(maze.ResourceError is null && maze.MapSpecification!.Where(map => map.StoryStageIndex < 0).Select(map => map.Id).SequenceEqual([100,101]),
            "无额外候选的坐标使用首阶段模板建立独立基础房间");
        var character = new Character(99,0,"FallbackFixture",0,new(38,2,1085,249),null)
            { ActiveMainQuests = [new(0,200,1) { AcceptanceId = Guid.NewGuid() }] };
        var quest = QuestEligibilityTests.Quest(200) with { Condition = "[clear map]",IntData = [102] };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [maze] },character,777,200,0,out var entry,out _,quest: quest) &&
            entry!.StartMap.Id == 100 && entry.StartMap.StoryStageIndex == -1,"基础模板与阶段0同ID时入场仍未消费阶段0");
        var layer = new DungeonMap("layered",new(0,0),103,"map/layer.map") { LayerIndex = 0 };
        maze = maze with { LayeredMaps = [layer] };
        entry = entry! with { Maze = maze };
        var plans = maze.MapSpecification!.Select(map => Monster(map,(ushort)(map.StoryStageIndex < 0 ? 20+map.Position.X : 30+map.StoryStageIndex)))
            .Append(Empty(layer)).ToArray();
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,100,new(plans,[new(new(0,0),new(1,0))]),authority,out var run,out _),
            "同模板基础房间和阶段拥有独立房间身份与对象key");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,20,1)).ClearFact is null,"基础房间清房不提前通关");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.CurrentMap.StoryStageIndex == -1,"首阶段与起点同坐标时先正常进入邻房基础图");
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,21,2));
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(0,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.CurrentMap.StoryStageIndex == 0 && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 30 }),
            "返回同模板首阶段不继承基础房间死亡状态");
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,30,3));
        run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),new(0,0),true); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),new(0,0),false).Outcome == DungeonRunOutcome.Accepted,
            "层叠演出结束可以回到来源阶段");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.CurrentMap.StoryStageIndex == 0 && run.Snapshot.LivingMonsters.Count == 0,
            "层叠返回恢复来源阶段及其清房状态，不切换成普通基础图");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.CurrentMap.StoryStageIndex == 1 && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 31 }),
            "层叠返回后继续按剧情原序推进，同模板邻房基础图死亡缓存不复用");
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,31,4)).ClearFact is null,"中间阶段不提前通关");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(0,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,32,5)).ClearFact?.ClearMap == maze.StoryStages[^1],
            "只有最后剧情阶段的实际清房签发通关");
    }

    static void QuestObjects()
    {
        var parent = QuestEligibilityTests.Quest(200);
        var child = parent with { Id = 201,Grade = "[sub]",MainQuest = 200,Condition = "[hunt enemy]",IntData = [777,-1,303,3,1],
            RewardType = null,Attributes = ["not give exp quest"] };
        var quests = new QuestCatalog(new() { [200] = parent,[201] = child });
        var active = new[] { new ActiveMainQuest(0,200,1) { AcceptanceId = Guid.NewGuid() },new ActiveMainQuest(1,201,1) { AcceptanceId = Guid.NewGuid() } };
        var boss = new DungeonMap("boss",new(0,0),100,"map/boss.map");
        var maze = new DungeonMaze(0,1,1,"fixture",[boss.Position],[boss.Position],[boss],[new(0,0),new(0,200),new(0,-1)]);
        var entry = new DungeonEntryPlan(99,0,777,200,0,0,maze,boss,boss) { QuestAcceptances = [.. active] };
        var clear = new DungeonClearFact(new(99,200,1,Guid.NewGuid(),101),777,[Guid.NewGuid()]) { ClearMap = boss };
        That(QuestHuntProgress.QuestObjectRoomClear(active,entry,quests,clear)[1].ConditionState == 0,
            "动态无奖励子任务物件由实际关联终点清房推进，同号普通怪物不冒充对象事件");
        foreach (var invalid in new DungeonClearFact?[] { null,clear with { ClearMap = boss with { Id = 101 } },
            clear with { DungeonId = 778 },clear with { ConfirmedDeathEvents = [] } })
            That(QuestHuntProgress.QuestObjectRoomClear(active,entry,quests,invalid).SequenceEqual(active),"非真实当前终点清房不能推进动态任务物件");
        That(QuestHuntProgress.QuestObjectRoomClear([active[0],active[1] with { AcceptanceId = Guid.NewGuid() }],entry,quests,clear)[1].ConditionState == 1,
            "旧副本清房不能推进放弃重接的物件子任务");
        foreach (var invalid in new[] { child with { Grade = "[epic]" },child with { MainQuest = 202 },child with { RewardType = "[item]" },
            child with { Attributes = [] },child with { IntData = [777,1,303,3,1] },child with { IntData = [777,-1,303,3,1,777,-1,304,3,1] } })
        {
            var source = invalid.IntData.Count == 10 ? new[] { active[0],active[1] with { ConditionState = 513 } } : active;
            That(QuestHuntProgress.QuestObjectRoomClear(source,entry,new(new() { [200] = parent,[201] = invalid }),clear).SequenceEqual(source),
                "其他分类、奖励、父任务、难度或不唯一目标保留原条件");
        }
    }

    static void Entrances()
    {
        That(DungeonPackets.BossEntranceOpened().SequenceEqual(new byte[5]),"Boss入口开放通知保持u32/u8全零正文");
        var strings = new Dictionary<int,string>();
        PvfToken Text(string text,byte type = 6) { int id = strings.Count+1; strings.Add(id,text); return new(type,id); }
        PvfToken Tag(string tag) => Text("["+tag+"]",3);
        var tokens = new List<PvfToken> { Tag("minimum required level"),new(0,1),Tag("basis level"),new(0,20),Tag("maze info"),
            Tag("size"),new(0,3),new(0,1),Tag("greed"),Text("BBFFEE"),Tag("map specification"),
            Text("map"),new(0,0),new(0,0),new(0,100),Text("map"),new(0,1),new(0,0),new(0,101),
            Text("boss"),new(0,2),new(0,0),new(0,102),Tag("/map specification"),
            Tag("start map"),new(0,0),new(0,0),Tag("/start map"),Tag("boss map"),new(0,2),new(0,0),Tag("/boss map"),
            Tag("quest connection"),new(0,0),new(0,200),new(0,-1),Tag("/maze info"),
            Tag("boss room entrance condition"),Text("[clear action]"),Text("[time condition]"),new(0,7),new(0,9),
            Text("[hunt monster]"),new(0,1),new(0,1),new(0,4),new(0,2),Tag("warp map condition"),
            Tag("source grid pos"),new(0,1),new(0,0),Tag("dest grid pos"),new(0,0),new(0,0),
            Tag("source grid pos"),new(0,2),new(0,0),Tag("dest grid pos"),new(0,0),new(0,0),Tag("/warp map condition") };
        var paths = Enumerable.Range(100,3).ToDictionary(id => id,id => "map/gate"+id+".map");
        DungeonDefinition Parse(IReadOnlyList<PvfToken> values) => DungeonCatalog.ParseResource(777,"dungeon/gate.dgn",values,id => strings[id],paths,_ => true);
        var definition = Parse(tokens);
        var maze = definition.Mazes.Single();
        That(maze.ResourceError is null && maze.BossEntrance is { TimeMode: 7,TimeValue: 9 } &&
            maze.BossEntrance.Targets.SequenceEqual(new[] { new DungeonEntranceHunt(1,4,2) }),"入口时间参数和猎杀额外字段原样保留，不猜造倒计时");
        That(maze.ConditionalMaps.Select(map => (map.Id,map.Position.X,map.Kind)).SequenceEqual(new[] { (100,1,"map"),(100,2,"boss") }) &&
            maze.StaticTopology.Supported,"条件传送保留逻辑房间及Boss属性，目标坐标仅提供替换模板");
        paths.Add(104,"map/gate104.map"); paths.Add(105,"map/gate105.map");
        var selected = DungeonCatalog.ResolveMapCandidates(maze with { MapSpecification = [
            maze.MapSpecification![0] with { CandidateIds = [100,104] },maze.MapSpecification[1],
            maze.MapSpecification[2] with { CandidateIds = [102,105] }] },paths,_ => true,_ => 1);
        That(selected.MapSpecification![0].Id == 104 && selected.MapSpecification[2].Id == 105 &&
            selected.ConditionalMaps.Select(map => (map.Id,map.Path,map.Position.X,map.Kind)).SequenceEqual(new[] {
                (104,paths[104],1,"map"),(104,paths[104],2,"boss") }),
            "条件替换和普通房间使用同一次抽选，保留来源逻辑坐标和Boss身份");
        var start = maze.MapSpecification![0]; var boss = maze.MapSpecification[2];
        var character = new Character(99,0,"GateFixture",0,new(38,2,1085,249),null)
            { ActiveMainQuests = [new(0,200,1) { AcceptanceId = Guid.NewGuid() }] };
        var quest = QuestEligibilityTests.Quest(200) with { Condition = "[clear map]",IntData = [100] };
        That(DungeonEntryPlan.TryCreate(definition,character,777,200,0,out var entry,out _,quest: quest) && entry!.BossMap == maze.ConditionalMaps[1],
            "任务清图目标允许资源声明的终点替换，不按固定地图或任务编号特判");
        var layeredBoss = boss with { Kind = "layered",Id = 103,Path = "map/layer.map" };
        var layeredEntry = entry! with { BossMap = layeredBoss,Maze = maze with { LayeredMaps = [layeredBoss] } };
        var plans = maze.MapSpecification!.Concat(maze.ConditionalMaps).Select((map,index) => Monster(map,(ushort)(10+index))).ToArray();
        var layeredAuthority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(layeredEntry,1,100,new([.. plans,Monster(layeredBoss,15)],
            [new(new(0,0),new(1,0)),new(new(1,0),new(2,0))]),layeredAuthority,out var layeredRun,out _),
            "入口条件和明确要求的叠层终点共用正式实例");
        layeredRun!.CompleteLoading(layeredRun.Snapshot.Identity,Guid.NewGuid());
        layeredRun.AcceptConfirmedDeath(layeredAuthority.Death(layeredRun.Snapshot.Identity,10,1));
        layeredRun.Move(layeredRun.Snapshot.Identity,Guid.NewGuid(),new(1,0));
        layeredRun.CompleteLoading(layeredRun.Snapshot.Identity,Guid.NewGuid());
        layeredRun.AcceptConfirmedDeath(layeredAuthority.Death(layeredRun.Snapshot.Identity,11,2));
        layeredRun.ConfirmBossEntranceOpen();
        layeredRun.Move(layeredRun.Snapshot.Identity,Guid.NewGuid(),new(2,0));
        layeredRun.CompleteLoading(layeredRun.Snapshot.Identity,Guid.NewGuid());
        That(layeredRun.AcceptConfirmedDeath(layeredAuthority.Death(layeredRun.Snapshot.Identity,14,3)).ClearFact is null &&
            layeredRun.Phase == SinglePlayerDungeonPhase.Dungeon,"同坐标替换清房不能绕过明确要求的叠层终点");
        layeredRun.MoveLayer(layeredRun.Snapshot.Identity,Guid.NewGuid(),new(2,0),true);
        layeredRun.CompleteLoading(layeredRun.Snapshot.Identity,Guid.NewGuid());
        That(layeredRun.AcceptConfirmedDeath(layeredAuthority.Death(layeredRun.Snapshot.Identity,15,4)).ClearFact?.ClearMap == layeredBoss,
            "条件替换后仍按原叠层顺序进入任务终点，真实死亡才产生通关");
        var mixedRoom = new DungeonRoomDefinition(start,[new(1,[1,0,20,0,0,0,1,1],"[fixed]","[normal]","monster/fixture.mob")],[],[])
            { AiCharacters = [new(1,[1,0,0,0,0,0],"[monster]","[normal]","aicharacter/fixture.aic",20)] };
        var mixedPlans = plans.Select((plan,index) => index == 0 ? DungeonMonsterPlan.FromRoom(mixedRoom,99,30) : plan).ToArray();
        var mixedAuthority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry!,1,100,new(mixedPlans,[new(new(0,0),new(1,0)),new(new(1,0),new(2,0))]),mixedAuthority,out var mixedRun,out _),
            "同数字的怪物与APC仍保留各自类型域");
        mixedRun!.CompleteLoading(mixedRun.Snapshot.Identity,Guid.NewGuid());
        mixedRun.AcceptConfirmedDeath(mixedAuthority.Death(mixedRun.Snapshot.Identity,31,1));
        mixedRun.AcceptConfirmedDeath(mixedAuthority.Death(mixedRun.Snapshot.Identity,30,2));
        That(!mixedRun.BossEntranceReady,"APC模板号与怪物目标相同也不计入hunt monster入口目标");
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry!,1,100,new(plans,[new(new(0,0),new(1,0)),new(new(1,0),new(2,0))]),authority,out var run,out _),
            "全部替换房间预留独立key并复用正式Run");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        var first = authority.Death(run.Snapshot.Identity,10,1);
        var pending = run.PrepareConfirmedDeath(first).Pending!;
        That(!run.BossEntranceReady && !run.BossEntranceOpen,"未提交死亡不推进入口");
        run.ConfirmCommittedDeath(pending);
        That(!run.BossEntranceReady,"不足目标数量不开放入口");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.CurrentMap.Id == 101 && !run.Snapshot.CurrentMap.IsConditional,"入口未开放时使用原房间");
        var second = authority.Death(run.Snapshot.Identity,11,2);
        run.AcceptConfirmedDeath(second);
        That(run.BossEntranceReady && !run.BossEntranceOpen,"跨房间的实际死亡达标，仍等待通知发送完成");
        That(run.AcceptConfirmedDeath(second).Outcome == DungeonRunOutcome.Duplicate,"重复死亡不累计入口数量");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(0,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.ConfirmBossEntranceOpen() && run.BossEntranceOpen,"入口开放回执保存在本次Run");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.CurrentMap == maze.ConditionalMaps[0] && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 13 }),
            "同格替换使用新的场景和对象身份，不复用原房间的死亡集合");
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,13,3)).ClearFact is null,"普通逻辑房间的替换不冒充终点通关");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(0,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.LivingMonsters.Count == 0,"已访问替换房间保持缓存死亡状态");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(2,0)); run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        var final = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,14,4)).Pending!;
        That(final.Result.ClearFact?.ClearMap == maze.ConditionalMaps[1] && QuestClearEligibility.TryValidate(entry!,final),
            "真正终点替换沿末次死亡、实际地图和原清图凭据合同完成");
        run.ConfirmCommittedDeath(final);
        That(run.ClearFact!.ConfirmedDeathEvents.Count == 4,"条件入口与替换通关没有伪造额外死亡");
        var next = new List<PvfToken>(tokens);
        next.AddRange([Tag("maze info"),Tag("size"),new(0,1),new(0,1),Tag("greed"),Text("BB"),Tag("map specification"),
            Text("boss"),new(0,0),new(0,0),new(0,102),Tag("/map specification"),Tag("start map"),new(0,0),new(0,0),Tag("/start map"),
            Tag("boss map"),new(0,0),new(0,0),Tag("/boss map"),Tag("/maze info")]);
        var separated = Parse(next).Mazes;
        That(separated[0].BossEntrance is { TimeMode: 7,TimeValue: 9 } gate && gate.Targets.SequenceEqual(maze.BossEntrance!.Targets) &&
            separated[0].WarpMaps.SequenceEqual(maze.WarpMaps) &&
            separated[1].BossEntrance is null && separated[1].WarpMaps.Count == 0,"闭合块后尾随入口与传送归前一maze，不泄漏到后续路线");
        var raw = new[] { new DungeonRawSection("warp map condition",[]),new DungeonRawSection("source grid pos",[new(0,1),new(0,0)]),
            new DungeonRawSection("dest grid pos",[new(0,0),new(0,0)]),new DungeonRawSection("/warp map condition",[]) };
        Reject<InvalidDataException>(() => DungeonMazeConditions.Warps(raw.Concat(raw).ToArray()),"重复传送起点不能随机选择目标");
        Reject<InvalidDataException>(() => DungeonMazeConditions.Warps(raw[..2]),"不完整传送声明保留资源错误");
    }

    static DungeonMonsterPlan Empty(DungeonMap map) => DungeonMonsterPlan.FromRoom(new(map,[],[],[]),99,10);
    static DungeonMonsterPlan Monster(DungeonMap map,ushort key) => DungeonMonsterPlan.FromRoom(new(map,
        [new(1,[1,0,20,0,0,0,1,1],"[fixed]","[boss]","monster/fixture.mob")],[],[]),99,key);
    sealed class Authority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity,ushort key,long sequence) => ConfirmDeath(identity,Guid.NewGuid(),key,sequence);
    }
}
