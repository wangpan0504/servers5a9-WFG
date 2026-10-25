using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using PvfLib;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TrainQuestSettlementTests
{
    // 实际PVF路线与合成39；不声称Windows客户端自然演出时序已验收。
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf,skills);
        var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var clears = global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        VerifyResources(pvf,dungeons,rooms);
        var quest = quests.Get(3581);
        That(quest.Condition == "[hunt enemy]" && quest.IntData.SequenceEqual(new[] {91,-1,70136,1,1}) &&
            quest.Prerequisites.SequenceEqual(new[] {3580}),"列车实际猎杀任务与前置未改写");
        var entrance = MiddleQuestEntryTests.Entrance(towns,quest,91);
        var connections = global::DfoServer.Testing.SelfTestCatalogs.QuestConnections(pvf);
        foreach (var scenario in new[] { (Request:3581u,BaseDeath:false,Replay:false,Lost:false),
            (Request:0u,BaseDeath:true,Replay:false,Lost:false),(Request:3581u,BaseDeath:false,Replay:false,Lost:true),
            (Request:3581u,BaseDeath:false,Replay:true,Lost:false) })
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,77,
                scenario.Replay ? entrance.Completed.Append(3581).ToArray() : entrance.Completed,position:entrance.Position,
                loseGrowth:scenario.Lost,growthLossCondition: pending => pending.Result.Snapshot.CurrentMap.Id == 70078);
            FormalDungeonReceipt opening;
            if (scenario.Replay)
            {
                That(connections.TryGetReplayDungeon(3581,out uint replayDungeon) && replayDungeon == 91 &&
                    f.Session.PrepareQuestReplay(3581,connections,dungeons,rooms) is not null &&
                    f.Session.BeginQuestReplayDungeonSelection(91).Receipt is not null,"原目录许可已完成列车回放，不造接取");
                opening = f.Session.EnterQuestReplay(91,3581,0)!;
            }
            else
            {
                That(f.Session.ChangeQuest(3581,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                    "列车沿原前置建立正式接取");
                opening = f.Session.EnterFormalDungeon(91,scenario.Request,0,dungeons,rooms)!;
            }
            That(opening is not null,"列车真实入场");
            That(opening!.Entry.BaseBossMap.Id == 70077 && opening.Entry.BossMap.Id == 70078 &&
                !opening.Entry.IsTerminalMap(opening.Entry.BaseBossMap),"战斗基础图不提前结算，站台是资源声明的末层终点");
            if (!scenario.BaseDeath && !scenario.Lost && !scenario.Replay) VerifyTerminalBoundary(opening.Entry,rooms);
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            foreach (var point in QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology,
                opening.Entry.StartMap.Position,opening.Entry.BaseBossMap.Position).Skip(1))
            {
                foreach (var enemy in loaded.Monsters.Monsters.Where(row => row.IsBlocking))
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,enemy),kills,growth).Outcome ==
                        DungeonRunOutcome.Accepted,"列车沿真实普通房间死亡前进");
                That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,"列车通过真实门墙");
                loaded = f.Session.CompleteFormalDungeonLoading()!;
            }
            That(loaded.Snapshot.CurrentMap.Id == 70077,"到达巨灵布鲁战斗图");
            var baseBoss = loaded.Monsters.Monsters.Single(row => row.MonsterId == 70136);
            var baseObservation = FormalDungeonSessionTests.Observation(loaded,baseBoss);
            if (scenario.BaseDeath)
                That(f.Session.ObserveFormalDungeonDeath(baseObservation,kills,growth) is { Outcome:DungeonRunOutcome.Accepted,ClearFact:null } &&
                    f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null,
                    "先收到基础39只记该对象死亡，不在演出换层前提前发31");
            // 死亡演出可先导航，未收到基础39时不替客户端补杀。
            That(f.Session.MoveFormalDungeon(4,0,1) is not null,"CMT2447进入站台末层");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Snapshot.CurrentMap.Id == 70078 && f.Session.EnableFormalDungeonSettlement() is null,
                "站台加载不能冒充Boss死亡");
            var boss = loaded.Monsters.Monsters.Single(row => row.MonsterId == 70136);
            var before = f.Read(); var identity = f.Session.DungeonSnapshot!.Identity;
            byte[] premature = DungeonSettlementProtocolTests.Report(before.Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(premature,out var early) &&
                f.Session.SettleFormalDungeon(early!.ActorId,early.RankPoint,early.MaximumRankPoint,premature,dungeons,clears,growth).Outcome ==
                    DungeonRunOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()),"末层无39时46不能提前发奖");
            var stale = f.Session.ObserveFormalDungeonDeath(baseObservation,kills,growth);
            That(stale.ClearFact is null && stale.Reward is null && stale.Receipt is null &&
                QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Session.DungeonSnapshot.Identity == identity &&
                f.Session.EnableFormalDungeonSettlement() is null,
                "换层后旧基础对象key只审计忽略，不能当成末层死亡");
            var observation = new NativeDungeonScriptedDeathObservation(boss.ObjectKey,0x22,true,new string('A',64));
            That(f.Session.ObserveFormalDungeonScriptedDeath(observation with { ChecksumMatches=false },kills,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"坏校验39不授予终点或经验");
            long countBefore = (long)f.Query("SELECT count(*) FROM character_dungeon_growth_receipts")!;
            if (scenario.Lost)
            {
                Reject<IOException>(() => Submit(),"末层死亡已提交但返回丢失");
                That(f.Read().Revision == before.Revision+1 && f.Session.DungeonSnapshot.Identity == identity &&
                    f.Session.EnableFormalDungeonSettlement() is null && f.Session.ReturnFromFormalDungeon() is null,
                    "未知结果保留原候选及离场互锁，不能提前发布清图");
            }
            else if (!scenario.Replay && !scenario.BaseDeath)
            {
                f.Sql("CREATE TRIGGER train_death_abort AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'train rollback'); END");
                Reject<SqliteException>(() => Submit(),"末层死亡事务中途SQL回滚");
                That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Session.DungeonSnapshot.Identity == identity &&
                    f.Session.EnableFormalDungeonSettlement() is null &&
                    (long)f.Query("SELECT count(*) FROM character_dungeon_growth_receipts")! == countBefore,"回滚不改变任务、经验、回执或epoch");
                f.Sql("DROP TRIGGER train_death_abort");
            }
            var result = Submit();
            That(result.Outcome == DungeonRunOutcome.Accepted && (scenario.Replay ? f.Read().ActiveMainQuests.All(row => row.QuestId != 3581) :
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3581).ConditionState == 0),
                "站台CMT2448真实对象39达到巨灵布鲁1/1");
            That(f.Session.Phase == SessionPhase.Settlement && f.Session.EnableFormalDungeonSettlement(boss.ObjectKey) is not null,
                "完成猎杀后必须同时拥有真实末层清图与回城许可（#739）");
            var saved = f.Read();
            That(saved.Revision == before.Revision+1 && result.ClearFact?.ClearMap?.Id == 70078 &&
                loaded.Monsters.Monsters.Where(row => !row.CanReportDeath).All(row => f.Session.DungeonSnapshot!.LivingMonsters.Contains(row.ObjectKey)) &&
                (long)f.Query("SELECT count(*) FROM character_dungeon_growth_receipts")! == countBefore+1 &&
                Submit().Outcome == DungeonRunOutcome.Duplicate && QuestCompletionRepositoryTests.Same(saved,f.Read()),
                "真实末层死亡只提交一次，重放不重复经验和任务奖励");
            byte[] body = DungeonSettlementProtocolTests.Report(f.Read().Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(body,out var report),"列车完整46正文");
            That(f.Session.SettleFormalDungeon(report!.ActorId,report.RankPoint,report.MaximumRankPoint,body,dungeons,clears,growth).Outcome ==
                DungeonRunOutcome.Accepted && f.Session.FormalDungeonResultFocus() is not null,"列车保留正式结算事务");
            That(f.Session.ReturnFromFormalDungeon() is not null,"列车结算允许正式回城");
            FormalDungeonDeathResult Submit() => f.Session.ObserveFormalDungeonScriptedDeath(observation,kills,growth);
        }
        Console.WriteLine($"PASS {Count-checks} 列车猎杀演出末层结算（非实机）");
    }

    static void VerifyResources(PvfArchive pvf,DungeonCatalog dungeons,DungeonRoomCatalog rooms)
    {
        var maze = dungeons.Get(91).Mazes.Single(row => row.ConnectsQuest(3581));
        var basis = rooms.Get(maze.MapSpecification!.Single(map => map.Id == 70077));
        var terminal = rooms.Get(maze.LayeredMaps.Single(map => map.Id == 70078));
        That(basis.BossDeathLayerChange is { MonsterId:70136, MonsterOrdinal:0, CinematicId:2447 } &&
            terminal.StartCinematicBossDestroy is { MonsterId:70136, MonsterOrdinal:0, CinematicId:2448 },
            "实际MOB死亡CMT2447与末层ACT/CMT2448绑定原怪物序号，不以任务号硬编码终点");
        var death = pvf.ReadTokens(basis.BossDeathLayerChange!.CinematicPath);
        var destroy = pvf.ReadTokens(terminal.StartCinematicBossDestroy!.CinematicPath);
        That(DungeonCinematicBossTerminalRules.DeathChangesLayer(70077,death,pvf.ResolveString) &&
            DungeonCinematicBossTerminalRules.RewardedDestroy(70078,0,destroy,pvf.ResolveString),"真实CMT正例");
        That(!DungeonCinematicBossTerminalRules.DeathChangesLayer(70078,death,pvf.ResolveString) &&
            !DungeonCinematicBossTerminalRules.RewardedDestroy(70077,0,destroy,pvf.ResolveString) &&
            !DungeonCinematicBossTerminalRules.RewardedDestroy(70078,1,destroy,pvf.ResolveString),"错地图或演员序号不给终点证据");
        int changeAt = At(death,"change map");
        int destroyAt = At(destroy,"destroy");
        That(!DungeonCinematicBossTerminalRules.DeathChangesLayer(70077,[..death,..death.Take(2)],pvf.ResolveString) &&
            !DungeonCinematicBossTerminalRules.DeathChangesLayer(70077,[..death,death[changeAt]],pvf.ResolveString) &&
            !DungeonCinematicBossTerminalRules.RewardedDestroy(70078,0,[..destroy,destroy[destroyAt]],pvf.ResolveString),
            "重复地图、CHANGE MAP或DESTROY声明均拒绝");
        var noReward = destroy.ToArray(); noReward[destroyAt+4] = new(0,0);
        var wrongActor = destroy.ToArray(); wrongActor[destroyAt-7] = death[changeAt-7];
        var wrongIndex = death.ToArray(); wrongIndex[changeAt-5] = new(0,1);
        That(!DungeonCinematicBossTerminalRules.RewardedDestroy(70078,0,noReward,pvf.ResolveString) &&
            !DungeonCinematicBossTerminalRules.RewardedDestroy(70078,0,wrongActor,pvf.ResolveString) &&
            !DungeonCinematicBossTerminalRules.DeathChangesLayer(70077,wrongIndex,pvf.ResolveString),
            "无奖励退休、非怪物主体或非当前角色换图不能套用该终点");
        string actionPath = terminal.Map.Path[..(terminal.Map.Path.LastIndexOf('/')+1)]+"action/basic_70078.act";
        var action = pvf.ReadTokens(actionPath);
        var unrelated = action.ToArray(); unrelated[4] = new(0,1);
        That(DungeonCinematicBossTerminalRules.StartCinematic(action,pvf.ResolveString) == 2448 &&
            DungeonCinematicBossTerminalRules.StartCinematic(unrelated,pvf.ResolveString) is null,
            "末层必须由真实起始触发指向行为0，不借其它触发演出");
        int At(IReadOnlyList<PvfToken> tokens,string tag) => Enumerable.Range(0,tokens.Count).First(i =>
            tokens[i].Type == 3 && pvf.ResolveString(tokens[i].Value).Trim('[',']').Equals(tag,StringComparison.OrdinalIgnoreCase));
    }

    static void VerifyTerminalBoundary(DungeonEntryPlan entry,DungeonRoomCatalog rooms)
    {
        entry = entry with { BossMap=entry.BaseBossMap };
        uint next=100;
        var plans = entry.Maze.MapSpecification!.Concat(entry.Maze.LayeredMaps).Select(map =>
        {
            var plan = DungeonMonsterPlan.Create(rooms,map,entry.CharacterId,checked((ushort)next),entry.BasisLevel,entry.PlayerTeam);
            if (plan.Monsters.Count != 0 || plan.SpecialObjects.Count != 0) next=plan.NextObjectKey;
            return plan;
        }).ToArray();
        var route = new DungeonFixedRoute(plans,entry.Maze.StaticTopology.Links!.Select(link =>
            new DungeonRouteConnection(link.First,link.Second)).ToArray());
        var target = plans.Single(plan => plan.Room.Map.Id == 70078);
        That(Grants(entry,route),"真实完整路线可独立派生站台终点");
        foreach (var altered in new[]
        {
            entry with { RouteQuestId=3582 },
            entry with { QuestAcceptances=[] },
            entry with { QuestAcceptanceId=Guid.NewGuid() },
            entry with { QuestAcceptances=[..entry.QuestAcceptances,entry.QuestAcceptances.Single(row => row.QuestId == 3581)] }
        }) That(!Grants(altered,route),"错路线、失去/重接接取或重复接取不能派生末层终点");
        foreach (var evidence in new DungeonCinematicBossEvidence?[]
        {
            null,
            target.Room.StartCinematicBossDestroy! with { MonsterId=70137 },
            target.Room.StartCinematicBossDestroy! with { MonsterOrdinal=1 },
            target.Room.StartCinematicBossDestroy! with { MonsterPath="monster/unrelated.mob" },
            target.Room.StartCinematicBossDestroy! with { MapId=70077 }
        })
            That(!Grants(entry,route with { Rooms=plans.Select(plan => plan == target ? plan with
                { Room=plan.Room with { StartCinematicBossDestroy=evidence } } : plan).ToArray() }),
                "缺失或错绑终末地图、序号、怪物模板/路径不扩大清图范围");
        var extraMap = target.Room.Map with { Id=170078,LayerIndex=target.Room.Map.LayerIndex+1 };
        var extra = DungeonMonsterPlan.FromRoom(target.Room with { Map=extraMap },entry.CharacterId,checked((ushort)next),entry.BasisLevel,playerTeam:entry.PlayerTeam);
        That(!Grants(entry with { Maze=entry.Maze with { LayeredMaps=[..entry.Maze.LayeredMaps,extraMap] } },
            route with { Rooms=[..plans,extra] }),"多个同坐标候选层不能猜测终末");
        bool Grants(DungeonEntryPlan candidate,DungeonFixedRoute path) =>
            SinglePlayerDungeonRun.TryCreate(candidate,1,100,path,new NativeDungeonCombatAuthority(),out var run,out _) &&
            run!.Entry.IsTerminalMap(target.Room.Map);
    }
}
