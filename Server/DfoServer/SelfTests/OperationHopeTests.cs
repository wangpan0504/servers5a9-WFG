using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OperationHopeTests
{
    public static void Run(PvfArchive pvf)
    {
        int before = Count;
        var catalog = OperationHopeCatalog.Load(pvf,SelfTestCatalogs.Dungeons(pvf),SelfTestCatalogs.Quests(pvf),SelfTestCatalogs.Rooms(pvf));
        var quests = SelfTestCatalogs.Quests(pvf);
        var maps = DungeonCatalog.ParseDirectory(pvf.ReadTokens("map/map.lst"),pvf.ResolveString,"map");
        var monsters = DungeonCatalog.ParseDirectory(pvf.ReadTokens("monster/monster.lst"),pvf.ResolveString,"monster");
        That(catalog.Areas.Count == 9 && catalog.MissionIds.SequenceEqual(Enumerable.Range(6171,9)), "希望真实九区域与九任务完整装载");
        That(catalog.Difficulty == 2, "希望采用真实指定难度2，不以普通难度0替代");
        That(catalog.FatigueValue == 4 && catalog.NeedFatigue == 4, "保留真实疲劳声明，不据此猜测扣费时点");
        foreach (var area in catalog.Areas.Values)
        {
            That(area.QuestId == 6171+area.Id && area.MissionValue == 0 && !area.Maps.IsEmpty, "每区原任务编号及未解释列保留");
            var quest = quests.Get(area.QuestId);
            That(quest.Grade == "[op hope mission]" && quest.RewardType == "[operation hope]" &&
                quest.Condition == "[hunt monster]" && quest.SubType == -1 &&
                quest.IntData is [8523,-1, > 0,1] &&
                monsters.TryGetValue(quest.IntData[2],out string? monsterPath) && pvf.Contains(monsterPath),
                "九任务均有真实副本猎杀目标，不能误当普通可见任务");
            That(!QuestHuntProgress.TryTargets(quest,out _), "未接通实例领域前仍不能进入普通任务猎杀路径");
            That(OperationHopeCatalog.SupportsMission(quest), "完整当前任务声明均在已支持资格奖励边界内");
            foreach (string field in new[] { "send postal", "select reward item", "pre required quest", "reward gold", "target character", "clear default attribute", "attribute" })
                That(!OperationHopeCatalog.SupportsMission(quest with { Definition = quest.Definition! with {
                    Fields = quest.Definition.Fields.Add(new QuestField(field,[],[])) } }), "新增未支持资格或奖励域必须拒绝而非截断");
            That(!OperationHopeCatalog.SupportsMission(quest with { Definition = quest.Definition! with { Job = "[swordman]" } }), "职业受限新任务不能冒用全职业资格");
            foreach (int map in area.Maps)
            {
                That(maps.TryGetValue(map,out string? path) && pvf.Contains(path), "区域地图声明均能解析真实资源");
                That(OperationHopePlan.IsTerminalArea(catalog,map) == (area.Id >= 6), "终端区域由声明连接决定，不以隐藏Boss替代");
                foreach (int target in catalog.Maps[map].NextAreas)
                    That(catalog.Areas[target].Maps.All(next => OperationHopePlan.CanConnectAreas(catalog,map,next)), "每个声明分支均能连接目标区域");
            }
        }
        That(OperationHopePlan.CanConnectAreas(catalog,550040,550043) && OperationHopePlan.CanConnectAreas(catalog,550040,550046), "起始区域双分支均保留");
        That(!OperationHopePlan.CanConnectAreas(catalog,550041,550046) && !OperationHopePlan.CanConnectAreas(catalog,550040,550063), "不能穿越未声明分支或直接跳终点");
        That(!OperationHopePlan.CanConnectAreas(catalog,0,550040) && !OperationHopePlan.CanConnectAreas(catalog,550040,0) && !OperationHopePlan.IsTerminalArea(catalog,550025), "未知图及旧Boss坐标上的普通图不冒充区域终点");
        That(catalog.Mazes.Count == 5 && catalog.Mazes.Values.All(maze => maze.Warps.Length == 12), "副本全局传送对完整共享给五个迷宫，不只落末段");
        That(catalog.Mazes[0].ClearMaps.SequenceEqual(new[] { 550059,550061,550063 }) &&
            catalog.Mazes[4].ClearMaps.SequenceEqual(new[] { 550059,550061,550063,550023,550024 }), "逐迷宫保留完整清图列表，不只指向隐藏Boss");
        foreach (int ordinal in catalog.Mazes.Keys)
        {
            var original = catalog.Mazes[ordinal].Layout;
            var promoted = original with { MapSpecification = original.MapSpecification!.Select(map => original.Bosses.Contains(map.Position) ? map with { Kind = "boss" } : map).ToArray() };
            That(OperationHopePlan.MatchesLayout(original,promoted), "兼容普通Entry仅把原Boss格map标成boss");
            That(!OperationHopePlan.MatchesLayout(original,promoted with { MapSpecification = promoted.MapSpecification!.Select((map,index) => index == 0 ? map with { Id = map.Id+1 } : map).ToArray() }), "同坐标替换地图身份不属于合法Entry提升");
            That(!OperationHopePlan.MatchesLayout(original,original with { MapSpecification = original.MapSpecification!.Select((map,index) => index == 0 ? map with { Kind = "boss" } : map).ToArray() }), "普通非Boss格不能伪造提升");
            That(OperationHopePlan.ReachableMissions(catalog,ordinal).SequenceEqual(catalog.MissionIds), "每个迷宫的真实区域路线都能到达九种目标候选，不从不可达池抽取");
            var identity = new DungeonRunIdentity(1,0,1,Guid.NewGuid(),1);
            foreach (int questId in catalog.MissionIds)
            {
                var accepted = new ActiveMainQuest(0,checked((ushort)questId),1) { AcceptanceId = Guid.NewGuid() };
                var binding = OperationHopePlan.TryBind(catalog,questId,ordinal,95,identity,accepted);
                That(binding is not null && binding.Mission.RewardItemId == 10307438 && binding.Mission.RewardCount == 60 && binding.Mission.SuppressExperience && binding.Mission.ExperienceRule == "op-hope-grade29-not-give-exp-v1", "显式任务绑定保留原60奖励而不启用默认随机");
                That(OperationHopePlan.Matches(binding!,identity with { Epoch = 20 },accepted with { ConditionState = 0 }), "加载epoch推进不改变实例与接取身份");
                That(!OperationHopePlan.Matches(binding!,identity with { InstanceId = Guid.NewGuid() },accepted) &&
                    !OperationHopePlan.Matches(binding!,identity with { CharacterId = 2 },accepted) &&
                    !OperationHopePlan.Matches(binding!,identity with { TaskAttempt = 2 },accepted) &&
                    !OperationHopePlan.Matches(binding!,identity,accepted with { AcceptanceId = Guid.NewGuid() }), "别的角色实例尝试或重新接取不能复用绑定");
                That(OperationHopePlan.TryBind(catalog,questId,ordinal,94,identity,accepted) is null &&
                    OperationHopePlan.TryBind(catalog,questId,ordinal,95,identity,accepted with { AcceptanceId = Guid.Empty }) is null &&
                    OperationHopePlan.TryBind(catalog,questId,ordinal,95,identity with { InstanceId = Guid.Empty },accepted) is null, "拒绝等级不足及未正式分配的任务和实例");
            }
            That(OperationHopePlan.IsDeclaredTransition(catalog,ordinal,new(0,1),new(0,0)), "本区实际相邻房间可达");
            That(!OperationHopePlan.IsDeclaredTransition(catalog,ordinal,new(0,0),new(0,1)), "官方规则禁止返回先前sector，即便静态门双向");
            That(!OperationHopePlan.IsDeclaredTransition(catalog,ordinal,new(0,0),new(0,2)), "同区域不能任意跨格传送");
            That(OperationHopePlan.IsDeclaredTransition(catalog,ordinal,new(0,0),new(1,1)) &&
                OperationHopePlan.IsDeclaredTransition(catalog,ordinal,new(0,2),new(2,1)), "两个实际跨区域传送分支均保留");
            That(!OperationHopePlan.IsDeclaredTransition(catalog,ordinal,new(0,1),new(1,1)), "普通静态横轴相邻边不能绕过区域选择和传送门");
            That(!OperationHopePlan.IsDeclaredTransition(catalog,ordinal,new(0,0),new(6,0)), "不可跳过资源声明直达终区");
        }
        int draws = 0;
        for (int index = 0; index < 9; index++)
        {
            int chosen = index;
            That(OperationHopePlan.ChooseMission(catalog,0,count => { draws++; That(count == 9,"均匀抽取仅传冻结候选总数"); return chosen; }) == 6171+index,
                "每个可达候选占据唯一抽取位置，不按稀缺度加权");
        }
        That(draws == 9 && OperationHopePlan.ChooseMission(catalog,-1,_ => throw new Exception("不应抽取")) is null, "无合法迷宫不消费随机抽取");
        Reject<InvalidOperationException>(() => OperationHopePlan.ChooseMission(catalog,0,_ => -1),"注入抽样器不能越过候选下界");
        Reject<InvalidOperationException>(() => OperationHopePlan.ChooseMission(catalog,0,_ => 9),"注入抽样器不能越过候选上界");
        That(OperationHopePlan.IsDeclaredTerminal(catalog,0,550059) && !OperationHopePlan.IsDeclaredTerminal(catalog,0,550025),
            "旧固定Boss坐标上的普通图不能替代三个明确清图终点");
        That(!OperationHopePlan.IsDeclaredTerminal(catalog,0,550023) && OperationHopePlan.IsDeclaredTerminal(catalog,4,550023),
            "隐藏变体终点只按本次迷宫声明生效，不合并所有迷宫的通关列表");
        VerifyRealMonsterPlans(catalog,SelfTestCatalogs.Rooms(pvf));
        VerifyDeathBinding(catalog);
        VerifyConfiguredRuns(catalog);
        Console.WriteLine($"PASS {Count-before} 代号希望资源与区域连接断言");
    }

    private static void VerifyRealMonsterPlans(OperationHopeCatalog catalog, DungeonRoomCatalog rooms)
    {
        // 原 MAP 行经过正式规划后仍须生成可报告死亡的敌对目标，而不只校验资源中出现过 ID。
        foreach (var maze in catalog.Mazes.Values)
        {
            var plans = maze.Layout.MapSpecification!.Where(map => catalog.Maps.ContainsKey(map.Id)).ToDictionary(map => map.Id,
                map => DungeonMonsterPlan.FromRoom(rooms.Get(map,requirePassiveDefinitions:false,allowUnresolvedSpecialMetadata:true),1,100,95,playerTeam:0));
            foreach (var (questId,mapIds) in maze.MissionMaps)
                That(mapIds.Any(mapId => plans[mapId].Monsters.Any(monster => monster.MonsterId == catalog.Missions[questId].MonsterId &&
                    monster.AiCharacter is null && monster.CanReportDeath && !monster.IsFriendly)), "每个真实迷宫的任务目标经过正式怪物规划后仍可死亡");
            foreach (int mapId in maze.ClearMaps.Where(plans.ContainsKey))
                That(plans[mapId].Monsters.Any(monster => monster.IsBoss && monster.AiCharacter is null && monster.CanReportDeath && !monster.IsFriendly),
                    "真实 clear-list 终点具有可报告死亡的敌对 Boss，不能以资源 ID 或普通房冒充");
        }
    }

    private static void VerifyDeathBinding(OperationHopeCatalog catalog)
    {
        // 合成正式 Run 的提交阶段，不冒称真实客户端战斗或 SQL 领奖验收。
        var mission = catalog.Missions[6177];
        var map = new DungeonMap("boss",new(7,0),550059,"map/fixture-hope-target.map");
        var maze = new DungeonMaze(0,9,3,"fixture",[map.Position],[map.Position],[map],[]);
        var active = new ActiveMainQuest(0,6177,1) { AcceptanceId = Guid.NewGuid() };
        var entry = new DungeonEntryPlan(1,1,8523,0,byte.MaxValue,catalog.Difficulty,maze,map,map) { QuestAcceptances = [active] };
        var source = new DungeonRoomMonster(mission.MonsterId,[mission.MonsterId,0,3,100,100,0,1,1],"[fixed]","[boss]","monster/fixture.mob");
        var room = new DungeonRoomDefinition(map,[source],[],[]);
        var plan = DungeonMonsterPlan.FromRoom(room,95,10);
        var authority = new HopeTestAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,100,new([plan],[]),authority,out var created,out _),"建立合成希望目标Run");
        var run = created!;
        var binding = OperationHopePlan.TryBind(catalog,6177,0,95,run.Snapshot.Identity,active)!;
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        var death = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,10));
        That(death.Pending is not null,"真实权威候选进入原Run提交阶段");
        That(OperationHopePlan.RecordTarget(binding,run,death.Pending!).TargetDeath is null &&
            !OperationHopePlan.CanClaim(catalog,binding,run,active with { ConditionState = 0 }),"SQL前待提交死亡或伪造scalar0均不准领奖");
        run.ConfirmCommittedDeath(death.Pending!);
        binding = OperationHopePlan.RecordTarget(binding,run,death.Pending!);
        That(binding.TargetDeath == death.Pending && OperationHopePlan.CanClaim(catalog,binding,run,active with { ConditionState = 0 }),
            "同实例已确认目标死亡加终末通关才能交给原子领奖事务");
        That(!OperationHopePlan.CanClaim(catalog,binding,run,active) &&
            !OperationHopePlan.CanClaim(catalog,binding,run,active with { ConditionState = 0, AcceptanceId = Guid.NewGuid() }),"未达成或重新接取不能复用原目标凭据");
        var forged = new PreparedDungeonDeath(death.Pending!.Death,death.Pending.Monster,death.Pending.Result);
        That(OperationHopePlan.RecordTarget(binding with { TargetDeath = null },run,forged).TargetDeath is null,"同值重构Prepared对象不是原Run确认的候选");
        var late = binding with { TargetDeath = null, Identity = binding.Identity with { Epoch = death.Pending.Death.Identity.Epoch+1 } };
        That(OperationHopePlan.RecordTarget(late,run,death.Pending).TargetDeath is null,"同实例晚绑定也不能复用绑定之前的死亡");
        run.Return(run.Snapshot.Identity,Guid.NewGuid());
        That(!OperationHopePlan.CanClaim(catalog,binding,run,active with { ConditionState = 0 }),"退出后的旧实例凭据不再授权领奖");
    }

    private static void VerifyConfiguredRuns(OperationHopeCatalog catalog)
    {
        foreach (var (ordinal,maze) in catalog.Mazes)
        foreach (int questId in catalog.MissionIds)
        {
            var maps = maze.Layout.MapSpecification!;
            var targetMap = maps.First(map => maze.MissionMaps[questId].Contains(map.Id));
            var start = maps.Single(map => map.Position == maze.Layout.Starts.Single());
            var firstPath = Path(start.Position,map => map.Id == targetMap.Id);
            var lastPath = Path(targetMap.Position,map => maze.ClearMaps.Contains(map.Id));
            var travel = firstPath.Concat(lastPath.Skip(1)).ToArray();
            var active = new ActiveMainQuest(0,checked((ushort)questId),1) { AcceptanceId = Guid.NewGuid() };
            var entry = new DungeonEntryPlan(1,1,8523,0,byte.MaxValue,catalog.Difficulty,maze.Layout,start,
                maps.Single(map => map.Position == maze.Layout.Bosses.Single())) { QuestAcceptances = [active] };
            var plans = maps.Select((map,index) =>
            {
                var monsters = new List<DungeonRoomMonster>();
                if (maze.ClearMaps.Contains(map.Id) && catalog.Maps.TryGetValue(map.Id,out var area))
                {
                    int boss = catalog.Missions[catalog.Areas[area.AreaId].QuestId].MonsterId;
                    monsters.Add(new(boss,[boss,0,3,100,100,0,1,1],"[fixed]","[boss]","monster/fixture.mob"));
                }
                if (map.Id == targetMap.Id && monsters.All(row => row.MonsterId != catalog.Missions[questId].MonsterId))
                {
                    int target = catalog.Missions[questId].MonsterId;
                    monsters.Add(new(target,[target,0,3,100,100,0,1,1],"[fixed]","[normal]","monster/fixture.mob"));
                }
                return DungeonMonsterPlan.FromRoom(new(map,monsters,[],[]),1,checked((ushort)(100+index*4)),95);
            }).ToArray();
            var route = new DungeonFixedRoute(plans,maze.Layout.StaticTopology.Links!.Select(link => new DungeonRouteConnection(link.First,link.Second)).ToArray());
            var authority = new HopeTestAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry,1,100,route,authority,out var created,out _),"使用真实迷宫布局建立正式Run");
            var run = created!;
            var binding = OperationHopePlan.TryBind(catalog,questId,ordinal,95,run.Snapshot.Identity,active)!;
            That(!run.ConfigureHope(catalog,binding with { Identity = binding.Identity with { InstanceId = Guid.NewGuid() } }) &&
                run.ConfigureHope(catalog,binding) && run.ConfigureHope(catalog,binding),"首次配置核实例并支持同一入场配置幂等");
            var areas = new HashSet<int>();
            for (int at = 0; at < travel.Length; at++)
            {
                var point = travel[at];
                if (at != 0) That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),point).Outcome == DungeonRunOutcome.Accepted,"真实有向区域路线通过原Move与房间缓存推进");
                That(run.HopeAreaId is int area && run.HasLoadedHopeArea(area) == areas.Contains(area),"新区域加载前与同区已加载事实复用原房间缓存");
                var loadingIdentity = run.Snapshot.Identity;
                var loadingAction = Guid.NewGuid();
                run.CompleteLoading(loadingIdentity,loadingAction);
                areas.Add(run.HopeAreaId!.Value);
                That(run.Snapshot.ConsumedFatigue == areas.Count*4 && run.CompleteLoading(loadingIdentity,loadingAction).Outcome == DungeonRunOutcome.Duplicate &&
                    run.Snapshot.ConsumedFatigue == areas.Count*4, "首次区域4点与加载重放去重，同行不同地图不重复扣费");
                That(!run.ConfigureHope(catalog,binding),"加载后不可重新配置或替换实例任务");
                if (at == 0) That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,1)).Outcome == DungeonRunOutcome.Rejected,"原静态横轴connection不能绕过希望区域门");
                foreach (var monster in plans.Single(plan => plan.Room.Map.Position == point).Monsters)
                {
                    var prepared = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,monster.ObjectKey)).Pending!;
                    That(prepared is not null,"正式Run核验本房权威死亡候选");
                    run.ConfirmCommittedDeath(prepared!);
                    if (monster.MonsterId == catalog.Missions[questId].MonsterId) active = active with { ConditionState = 0 };
                }
                if (at < travel.Length-1) That(!OperationHopePlan.CanClaim(catalog,run.HopeBinding!,run,active),"目标已死但未真实通关不能提前领取");
                if (at > 0 && at < travel.Length-1) That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),travel[at-1]).Outcome == DungeonRunOutcome.Rejected,"原缓存可用也不能返回前sector或前区域");
            }
            That(areas.Count == 4 && run.Snapshot.ConsumedFatigue == 16 && run.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement && run.ClearFact?.ClearMap?.Id != 550025 &&
                OperationHopePlan.CanClaim(catalog,run.HopeBinding!,run,active),"五迷宫九任务均经四区域真实Boss终点，自动保存目标死亡并具备领奖资格");

            DungeonPoint[] Path(DungeonPoint from,Func<DungeonMap,bool> complete)
            {
                var pending = new Queue<DungeonPoint[]>(); pending.Enqueue([from]);
                var visited = new HashSet<DungeonPoint>();
                while (pending.TryDequeue(out var path))
                {
                    var point = path[^1];
                    if (!visited.Add(point)) continue;
                    if (complete(maps.Single(map => map.Position == point))) return path;
                    foreach (var map in maps)
                        if (!visited.Contains(map.Position) && OperationHopePlan.IsDeclaredTransition(catalog,ordinal,point,map.Position))
                            pending.Enqueue(path.Append(map.Position).ToArray());
                }
                throw new InvalidOperationException("希望真实布局没有经过任务目标的终点路径");
            }
        }
    }

    private sealed class HopeTestAuthority : DungeonCombatAuthority
    {
        private long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity,ushort key) => ConfirmDeath(identity,Guid.NewGuid(),key,++sequence);
    }

}
