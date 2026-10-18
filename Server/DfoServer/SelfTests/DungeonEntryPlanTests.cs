using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using PvfLib;

namespace DfoServer;

internal static class DungeonEntryPlanTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;

        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var character = new Character(17, 0, "Plan", 42, new Location(38, 2, 100, 200, 0), null)
        {
            Level = 1,
            ActiveMainQuests = [new(0, 3145, 1)]
        };
        bool created = DungeonEntryPlan.TryCreate(catalog, character, 3, 3145, 0, out var plan, out string reason);
        That(created && plan is not null && reason == "resource-plan-ready", "active 3145 creates a resource plan");
        var selected = plan ?? throw new Exception("FAIL: missing resource plan");
        That(selected.CharacterId == 17 && selected.CharacterRevision == 42 && selected.DungeonId == 3 && selected.QuestId == 3145
            && selected.ActiveQuestSlot == 0 && selected.Difficulty == 0, "plan retains character and request identity");
        That(selected.Maze.SourceOrdinal == 1 && selected.Maze.Width == 5 && selected.Maze.Height == 2
            && selected.StartMap.Id == 76121 && selected.StartMap.Position == new DungeonPoint(0, 1)
            && selected.BossMap.Id == 76126 && selected.BossMap.Position == new DungeonPoint(4, 0), "3145 uses its task maze instead of the ordinary maze");
        That(character.Revision == 42 && character.Position == new Location(38, 2, 100, 200, 0)
            && character.CompletedQuests.Length == 0 && character.ActiveMainQuests.SequenceEqual([new ActiveMainQuest(0, 3145, 1)]),
            "resource selection does not mutate character progress or position");
        That(DungeonEntryPlan.TryCreate(catalog, character, 3, 3145, 0, out var repeat, out _) && repeat is not null &&
            repeat.QuestAcceptances.SequenceEqual(selected.QuestAcceptances) && repeat with { QuestAcceptances = selected.QuestAcceptances } == selected,
            "repeated resource selection has no new instance or state transition");
        character.ActiveMainQuests[0] = new(0,3145,0);
        That(selected.QuestAcceptances is [{ ConditionState: 1 }], "入场冻结任务值，后续角色数组变化不改写旧实例接取快照");
        character.ActiveMainQuests[0] = new(0,3145,1);

        Reject(character with { ActiveMainQuests = [] }, 3, 3145, 0, "quest-not-active");
        Reject(character with { CompletedQuests = [3145] }, 3, 3145, 0, "quest-already-completed");
        Reject(character with { ActiveMainQuests = [], CompletedQuests = [3145] }, 3, 3145, 0, "quest-already-completed");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 0)] }, 3, 3145, 0, "quest-condition-not-pending");
        Reject(character with { ActiveMainQuests = [new(0, 3145, uint.MaxValue)] }, 3, 3145, 0, "quest-condition-not-pending");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 1), new(1, 3145, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 1), new(0, 3146, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { ActiveMainQuests = [new(30, 3145, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { Level = 0 }, 3, 3145, 0, "character-level-invalid");
        Reject(character with { Level = 96 }, 3, 3145, 0, "character-level-invalid");
        Reject(character, 0, 3145, 0, "dungeon-unsupported");
        Reject(character, uint.MaxValue, 3145, 0, "dungeon-unsupported");
        foreach (var ordinaryCharacter in new[]
        {
            character,
            character with { ActiveMainQuests = [], CompletedQuests = [3145] },
            character with { ActiveMainQuests = [] }
        })
        {
            That(DungeonEntryPlan.TryCreate(catalog, ordinaryCharacter, 3, 0, 0, out var normalPlan, out _) &&
                normalPlan is { IsOrdinary: true, QuestAcceptanceId: var acceptance, ActiveQuestSlot: byte.MaxValue } &&
                acceptance == Guid.Empty && normalPlan.Maze.SourceOrdinal == 0 && normalPlan.StartMap.Id == 58548 && normalPlan.BossMap.Id == 58553,
                "未完成、已完成和无活动主线均选择真正普通maze，不伪造任务身份");
        }
        foreach (int id in new[] { 3, 5, 6, 7, 8, 9, 1000 })
        {
            var resource = catalog.Get(id);
            var eligible = character with { Level = checked((byte)resource.MinimumLevel!.Value), ActiveMainQuests = [] };
            That(DungeonEntryPlan.TryCreate(catalog, eligible, checked((uint)id), 0, 0, out var normalPlan, out _) &&
                normalPlan!.Maze.StaticTopology.Supported && normalPlan.Maze.QuestConnectionTokens.Count == 0 &&
                normalPlan.MonsterBasisLevel == resource.BasisLevel, "左入口各普通图使用其资源等级、路线和难度");
            Reject(eligible, checked((uint)id), 0, 1, "difficulty-unsupported");
            if (eligible.Level > 1)
                Reject(eligible with { Level = (byte)(eligible.Level - 1) }, checked((uint)id), 0, 0, "dungeon-level-too-low");
        }
        Reject(character, 3, 3146, 0, "quest-not-active");
        Reject(character, 3, 3145, 1, "difficulty-unsupported");
        Reject(character, 3, 3145, byte.MaxValue, "difficulty-unsupported");

        var definition = catalog.Get(3);
        That(!definition.DisableGiveupPenalty && !selected.DisableGiveupPenalty &&
            DungeonEntryPlan.TryCreate(definition with { DisableGiveupPenalty = true },character,3,3145,0,out var exemptPlan,out _) &&
            exemptPlan is { DisableGiveupPenalty: true } && !selected.DisableGiveupPenalty && !definition.DisableGiveupPenalty,
            "入场冻结本次资源退图豁免，保留共享定义与此前实例的原资格");
        That(DungeonEntryPlan.TryCreate(definition with { DisableFatigueConsumption = true },
            character with { ActiveMainQuests = [] },3,0,0,out var noFatiguePlan,out _) && noFatiguePlan is
            { IsOrdinary: true, RequiresFatigueOwner: true, UsesFatigue: false, DisableFatigueConsumption: true,
              DisableGiveupPenalty: false, HasFreeCombatConsumption: false } && !definition.DisableFatigueConsumption,
            "no fatigue只冻结疲劳值免扣，不绕过原owner校验、不免虚弱或道具消耗，也不改共享定义");
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        AuxiliaryEquipmentHuntRoutes(catalog,quests,character);
        OrdinaryHuntRoutes(catalog,quests,rooms,character);
        AwakeningDungeonRoutes(catalog,quests,rooms,character);
        TournamentEntry(catalog,character,rooms);
        HarlemSeekingRoutes(catalog,quests,character);
        foreach (var scenario in new[] { (Quest:3560,Dungeon:82,Level:(byte)65,Target:92247,Boss:92251),
            (Quest:3753,Dungeon:104,Level:(byte)83,Target:76648,Boss:76649) })
        {
            var targetQuest = quests.Get(scenario.Quest);
            var accepted = character with { Level = scenario.Level,
                ActiveMainQuests = [new(5,checked((ushort)scenario.Quest),1) { AcceptanceId = Guid.NewGuid() }] };
            That(DungeonEntryPlan.TryCreate(catalog,accepted,checked((uint)scenario.Dungeon),checked((uint)scenario.Quest),0,
                out var middlePlan,out var middleReason,quest: targetQuest,quests: quests) &&
                middlePlan is { QuestClearMap: not null } && middlePlan.QuestClearMap.Id == scenario.Target &&
                middlePlan.BossMap.Id == scenario.Boss && middlePlan.BaseBossMap == middlePlan.BossMap &&
                !middlePlan.IsTerminalMap(middlePlan.QuestClearMap),
                "中途目标独立于真实Boss，保留原迷宫而不投影任务终点：" + scenario.Quest + "/" + middleReason);
            var missingTarget = targetQuest with { IntData = [int.MaxValue] };
            That(!DungeonEntryPlan.TryCreate(catalog,accepted,checked((uint)scenario.Dungeon),checked((uint)scenario.Quest),0,
                out _,out _,quest: missingTarget,quests: quests),"未声明的地图号不能借中途清图放行");
        }
        var eggQuest = quests.Get(6032);
        var eggAcceptance = new ActiveMainQuest(5,6032,1) { AcceptanceId = Guid.NewGuid() };
        var eggCharacter = character with { Level = 92, CompletedQuests = [6031], ActiveMainQuests = [eggAcceptance] };
        foreach (uint requestQuest in new uint[] { 6032,0 })
        {
            That(DungeonEntryPlan.TryCreate(catalog,eggCharacter,291100319,requestQuest,0,out var eggPlan,out var eggReason,
                quest: requestQuest == 0 ? null : eggQuest,quests: quests) && eggPlan is
                { BossMap.Id: 59914, BossMap.Position.X: 3, BossMap.Position.Y: 0,
                  TerminalLayerMap.Id: 59914, TerminalLayerMap.Position.X: 2, TerminalLayerMap.Position.Y: 0 } &&
                eggPlan.TerminalLayerAcceptanceId == eggAcceptance.AcceptanceId && eggPlan.TerminalLayerQuestId == eggAcceptance.QuestId &&
                eggPlan.TerminalLayerQuestSlot == eggAcceptance.Slot && !eggPlan.IsTerminalMap(eggPlan.BossMap),
                "被偷走的卵保留原Boss坐标并冻结原接取的实际末层，显式及普通0请求一致：" + eggReason);
            var frozenEgg = eggPlan ?? throw new InvalidOperationException("真实末层入场计划未建立");
            var information = DfoServer.Network.Builders.DungeonPackets.Information(frozenEgg);
            That(information[8] == 3 && information[9] == 0, "终末别名不改变NOTI28原grid");
            var actual = DungeonMonsterPlan.Create(rooms,frozenEgg.TerminalLayerMap!,eggCharacter.Id,100,
                frozenEgg.BasisLevel,frozenEgg.PlayerTeam);
            That(actual.Room.DeclaredType == "[boss]" && actual.Monsters.Any(monster => monster.MonsterId == 70215 &&
                monster.IsScriptedBoss && monster.IsBoss && monster.CanReportDeath),
                "真实末层保留MAP自身boss类型和完整dummy演员，不伪造剧情死亡");
        }
        var plazaQuest = quests.Get(3946);
        var plazaCharacter = character with { Level = 88,CompletedQuests = [3945],
            ActiveMainQuests = [new(5,3946,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,plazaCharacter,310,3946,0,out var plazaPlan,out var plazaReason,
            quest: plazaQuest,quests: quests) && plazaPlan is { Maze.SourceOrdinal: 1,StartMap.Id: 76670,BossMap.Id: 76678,BaseBossMap.Kind: "boss" } &&
            plazaPlan.QuestAcceptanceId == plazaCharacter.ActiveMainQuests.Single().AcceptanceId && plazaPlan.Maze.StaticTopology.Supported,
            "真实杂乱的开始在魔兽区块之后仍可解析并选择原九图任务路线：" + plazaReason);
        That(DungeonEntryPlan.TryCreate(catalog,plazaCharacter,310,0,0,out var automaticPlaza,out _,quests: quests) &&
            automaticPlaza is { QuestId: 0,RouteQuestId: 3946,Maze.SourceOrdinal: 1,BossMap.Id: 76678,QuestAcceptanceId: var plazaAcceptance } &&
            plazaAcceptance == Guid.Empty && automaticPlaza.QuestAcceptances.SequenceEqual(plazaCharacter.ActiveMainQuests),
            "时间广场普通零请求复用当前3946路线，不借用魔兽布局或伪造请求接取身份");
        foreach (var scenario in new[] { (Quest:3772,Dungeon:235,Previous:3771), (Quest:3773,Dungeon:227,Previous:3772),
            (Quest:3774,Dungeon:1005,Previous:3773),
            (Quest:3776,Dungeon:1007,Previous:3775), (Quest:3777,Dungeon:238,Previous:3776) })
        {
            var target = quests.Get(scenario.Quest);
            var pending = character with { Level = 84, CompletedQuests = [scenario.Previous],
                ActiveMainQuests = [new(5,checked((ushort)scenario.Quest),1) { AcceptanceId = Guid.NewGuid() }] };
            var resource = catalog.Get(scenario.Dungeon);
            var source = resource.Mazes.Single(maze => maze.ConnectsQuest(checked((uint)scenario.Quest)));
            var sourceMaps = source.MapSpecification ?? throw new InvalidDataException("任务路线缺少显式地图：" + scenario.Quest);
            var originalBoss = sourceMaps.Single(map => map.Position == source.Bosses.Single());
            That(originalBoss.Kind == "map" && originalBoss.Id == target.IntData[0],
                "真实任务Boss坐标的基础map行与清图目标一致：" + scenario.Quest);
            That(DungeonEntryPlan.TryCreate(catalog,pending,checked((uint)scenario.Dungeon),checked((uint)scenario.Quest),0,
                out var declared,out var declaredReason,quest: target,quests: quests),
                "真实任务沿声明的Boss坐标进入原路线：" + scenario.Quest + "/" + declaredReason);
            That(declared!.BaseBossMap == (originalBoss with { Kind = "boss" }) && declared.BossMap == declared.BaseBossMap &&
                declared.QuestAcceptanceId == pending.ActiveMainQuests.Single().AcceptanceId &&
                declared.QuestAcceptances.SequenceEqual(pending.ActiveMainQuests) && sourceMaps.Contains(originalBoss) &&
                sourceMaps.Single(map => map.Position == originalBoss.Position).Kind == "map" && declared.Maze.StaticTopology.Supported,
                "Boss投影保留原地图、接取及完整拓扑，不改共享资源：" + scenario.Quest);
            That(DungeonEntryPlan.TryCreate(catalog,pending,checked((uint)scenario.Dungeon),0,0,out var automaticDeclared,out _,quests: quests) &&
                automaticDeclared!.BossMap == declared.BossMap && automaticDeclared.RouteQuestId == scenario.Quest &&
                automaticDeclared.QuestId == 0 && automaticDeclared.QuestAcceptanceId == Guid.Empty,
                "普通选图的活动任务路线复用相同Boss投影：" + scenario.Quest);
            var planned = new List<DungeonMonsterPlan>();
            uint next = 100;
            foreach (var map in declared.Maze.MapSpecification!)
            {
                var room = DungeonMonsterPlan.Create(rooms,map,pending.Id,checked((ushort)next),declared.BasisLevel,declared.PlayerTeam);
                planned.Add(room);
                if (room.Monsters.Count > 0 || room.SpecialObjects.Count > 0) next = room.NextObjectKey;
            }
            That(planned.Count == declared.Maze.MapSpecification.Count && planned.All(room =>
                room.Room.Monsters.All(monster => room.Monsters.Any(actor => actor.Source == monster)) &&
                room.Room.AiCharacters.All(actor => room.Monsters.Any(monster => monster.AiCharacter == actor)) &&
                room.Room.SpecialObjects.All(sourceObject => room.SpecialObjects.Any(actor => actor.Source == sourceObject))),
                "真实整条路线保留普通、dummy及友方APC演员：" + scenario.Quest);
            var route = new DungeonFixedRoute(planned,declared.Maze.StaticTopology.Links!
                .Select(link => new DungeonRouteConnection(link.First,link.Second)).ToArray());
            That(SinglePlayerDungeonRun.TryCreate(declared,1,100,route,new NativeDungeonCombatAuthority(),out _,out _),
                "Boss投影后的全部真实房间可建立原正式Run：" + scenario.Quest);
        }
        That(DungeonEntryPlan.TryCreate(catalog,character,3,0,0,out var automatic,out _,quests: quests) &&
            automatic!.QuestId == 0 && automatic.RouteQuestId == 3145 && automatic.Maze.SourceOrdinal == 1 && automatic.QuestAcceptanceId == Guid.Empty,
            "普通选图请求优先选择唯一活动任务的资源路线");
        var noTask = character with { ActiveMainQuests = [], CompletedQuests = [3145] };
        That(DungeonEntryPlan.TryCreate(catalog,noTask,3,0,0,out var ordinary,out _,draw: _ => 0,quests: quests) && ordinary is
            { QuestId: 0, RouteQuestId: 0, ActiveQuestSlot: byte.MaxValue, Maze.SourceOrdinal: 0, StartMap.Id: 58548, BossMap.Id: 58553 },
            "没有剧情路线时使用PVF普通迷宫，不能把旧任务接取伪造成当前活动任务");
        That(!DungeonEntryPlan.TryCreate(catalog,character with { ActiveMainQuests = [new(0,3145,1),new(1,3146,2)] },
            3,0,0,out _,out string ambiguous,quests: quests) && ambiguous == "quest-maze-ambiguous",
            "同时匹配不同任务路线时拒绝进入，不随意替换地图");
        That(catalog.Get(12).AllowsDifficulty(1,false) && !catalog.Get(12).AllowsDifficulty(2,false) &&
            !catalog.Get(12).AllowsDifficulty(1,true) && !catalog.Get(3).AllowsDifficulty(1,false),
            "合法普通难度按difficulty level检查，剧情difficulty size及designate限制仍保留");
        var heart = character with { Level = 84, CompletedQuests = [3777],
            ActiveMainQuests = [new(5,3778,1) { AcceptanceId = Guid.NewGuid() }] };
        var heartQuest = quests.Get(3778);
        var heartResource = catalog.Get(233);
        That(heartResource.TryGetDesignatedDifficulty(out var normalHeartDifficulty) && normalHeartDifficulty == 2 &&
            heartResource.AllowsDifficulty(0,true) && !heartResource.AllowsDifficulty(2,true),
            "真实安徒恩心脏保留普通指定二难度元数据，故事只接受自身零难度");
        That(DungeonEntryPlan.TryCreate(catalog,heart,233,3778,0,out var heartPlan,out var heartReason,quest: heartQuest,quests: quests) &&
            heartPlan is { Difficulty: 0, StartMap.Id: 310064, BossMap.Id: 310065, IsQuestReplay: false } &&
            heartPlan.QuestAcceptanceId == heart.ActiveMainQuests.Single().AcceptanceId && heartPlan.Maze.StaticTopology.Supported,
            "真实3778显式请求选择原故事路线及接取：" + heartReason);
        That(DungeonEntryPlan.TryCreate(catalog,heart,233,0,0,out var autoHeart,out _,quests: quests) &&
            autoHeart is { Difficulty: 0, RouteQuestId: 3778, StartMap.Id: 310064, BossMap.Id: 310065, QuestId: 0 },
            "普通0请求的活动3778路线同样使用故事难度");
        var replayHeart = new DfoServer.Game.Quests.QuestReplayPlan(heart with { ActiveMainQuests = [], CompletedQuests = [3777,3778] },heartQuest,233);
        That(DungeonEntryPlan.TryCreateReplay(catalog,replayHeart,out var heartReplay,out _) &&
            heartReplay is { IsQuestReplay: true, Difficulty: 0, QuestId: 0, RouteQuestId: 3778, BossMap.Id: 310065 } &&
            !DungeonEntryPlan.TryCreate(catalog,heart,233,3778,2,out _,out _,quest: heartQuest,quests: quests),
            "已完成3778回放保留故事零难度，活动任务不能借普通指定二难度进入");
        foreach (var sample in new[] { (Quest:6206,Previous:6205),(Quest:6214,Previous:6213) })
        {
            var storyQuest = quests.Get(sample.Quest);
            var pending = character with { Level = 95,CompletedQuests = [sample.Previous],
                ActiveMainQuests = [new(5,checked((ushort)sample.Quest),1) { AcceptanceId = Guid.NewGuid() }] };
            foreach (uint requested in new uint[] { checked((uint)sample.Quest),0 })
            {
                That(DungeonEntryPlan.TryCreate(catalog,pending,291100373,requested,0,out var storyPlan,out var storyReason,
                    quest: requested == 0 ? null : storyQuest,quests: quests) && storyPlan is
                    { Maze.ConnectionQuestId: var connection,StartMap.Id: 292106186,BossMap.Id: 292106190 } && connection == sample.Quest,
                    "真实多故事块显式及自动请求选择最终任务块：" + sample.Quest + "/" + storyReason);
                foreach (byte rejected in new byte[] { 1,2,3,4 })
                    That(!DungeonEntryPlan.TryCreate(catalog,pending,291100373,requested,rejected,out _,out var storyRejectedReason,
                        quest: requested == 0 ? null : storyQuest,quests: quests) && storyRejectedReason == "difficulty-unsupported",
                        "真实故事size1不借其他块或普通难度放行");
                var plans = new List<DungeonMonsterPlan>();
                ushort nextKey = 100;
                foreach (var map in storyPlan!.Maze.MapSpecification!)
                {
                    var actors = DungeonMonsterPlan.Create(rooms,map,pending.Id,nextKey,storyPlan.BasisLevel,storyPlan.PlayerTeam);
                    plans.Add(actors);
                    if (actors.Monsters.Count > 0 || actors.SpecialObjects.Count > 0) nextKey = checked((ushort)actors.NextObjectKey);
                }
                That(plans.Count == 5 && plans.Sum(room => room.Monsters.Count) > 0 && plans.All(room =>
                    room.Room.Monsters.All(source => room.Monsters.Any(actor => actor.Source == source))),
                    "真实五房保留全部源怪物，不用空房替代任务路线");
            }
        }
        var hero = character with { Job = 13, GrowType = 36, Level = 90, ActiveMainQuests = [new(3,3240,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,hero,151,3240,0,out var heroPlan,out string heroReason,quest: quests.Get(3240),quests: quests),
            "向暗黑城前进可进入英雄冢：" + heroReason);
        That(heroPlan is { QuestId: 3240, RouteQuestId: 3240, ActiveQuestSlot: 3, Maze.SourceOrdinal: 7, StartMap.Id: 91391, BossMap.Id: 91398 } &&
            heroPlan.QuestAcceptanceId == hero.ActiveMainQuests[0].AcceptanceId && heroPlan.Maze.StaticTopology.Supported,
            "英雄冢沿真实任务接取身份及八房路线规划，不替换成普通图");
        That(DungeonEntryPlan.TryCreate(catalog,hero,151,0,0,out var autoHero,out _,quests: quests) &&
            autoHero is { QuestId: 0, RouteQuestId: 3240, Maze.SourceOrdinal: 7 }, "普通选图同样匹配英雄冢活动任务路线");
        var noir = hero with { Job = 0, GrowType = 0, CompletedQuests = [3272,5149],
            ActiveMainQuests = [new(5,3273,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,noir,239,3273,0,out var noirParent,out string noirReason,
            quest: quests.Get(3273),quests: quests), "真实抵达诺伊佩拉父请求选择当前子路线：" + noirReason);
        That(noirParent is { QuestId: 3273, RouteQuestId: 3273, ActiveQuestSlot: 5, StartMap.Id: 70148, BossMap.Id: 70154 } &&
            noirParent.QuestAcceptanceId == noir.ActiveMainQuests[0].AcceptanceId && noirParent.QuestAcceptances.SequenceEqual(noir.ActiveMainQuests),
            "父身份与持久接取保留，只借用5150迷宫，不在入场规划中接取子任务");
        That(DungeonEntryPlan.TryCreate(catalog,noir,239,0,0,out var noirAutomatic,out _,quests: quests) &&
            noirAutomatic is { QuestId: 0, RouteQuestId: 3273, BossMap.Id: 70154, QuestAcceptanceId: var noAcceptance } && noAcceptance == Guid.Empty,
            "普通0请求也能由已接父任务和真实子完成记录选择诺伊佩拉路线，不伪造请求任务身份");
        var parentStory = catalog.Get(239) with { RawSections = StoryBlocks(5150,3273) };
        foreach (uint requested in new uint[] { 3273,0 })
        {
            That(DungeonEntryPlan.TryCreate(parentStory,noir,239,requested,0,out var connected,out var connectedReason,
                quest: requested == 0 ? null : quests.Get(3273),quests: quests) && connected is
                { Maze.ConnectionQuestId: 5150,RouteQuestId: 3273 },
                "多故事块使用实际子maze连接身份，保留父接取及自动请求身份：" + connectedReason);
            That(!DungeonEntryPlan.TryCreate(parentStory,noir,239,requested,1,out _,out var rejectedStory,
                quest: requested == 0 ? null : quests.Get(3273),quests: quests) && rejectedStory == "difficulty-unsupported",
                "父任务所属另一故事块的较高难度不能泄漏到实际子路线");
        }
        var replayStory = heartResource with { RawSections = StoryBlocks(3778,9999) };
        That(DungeonEntryPlan.TryCreate(replayStory,replayHeart.Expected,233,3778,0,out var storyReplay,out _,
            quest: heartQuest,isReplay: true) && storyReplay is { IsQuestReplay: true,Maze.ConnectionQuestId: 3778 },
            "回放同样按最终maze选择故事块");
        That(!DungeonEntryPlan.TryCreate(catalog,noir with { CompletedQuests = [3272], ActiveMainQuests = [noir.ActiveMainQuests[0] with { ConditionState = 2 }] },
            239,3273,0,out _,out _,quest: quests.Get(3273),quests: quests), "5149材料步骤未完成时不跳到5150迷宫");
        var heroDefinition = catalog.Get(151);
        var heroLevels = heroDefinition.RawSections.First(section => section.Tag == "difficulty level");
        That(heroDefinition.RawSections.Count(section => section.Tag == "difficulty level") == 2 &&
            Enumerable.Range(0,5).All(value => heroDefinition.AllowsDifficulty((byte)value,false) == (value < 3)) &&
            !heroDefinition.AllowsDifficulty(1,true), "相同重复难度表保留普通难度范围与剧情难度限制");
        foreach (var tokens in new PvfToken[][]
        {
            [new(0,0),new(0,1),new(0,1),new(0,0),new(0,0)],
            [new(0,1)],
            [new(0,1),new(0,1),new(0,2),new(0,0),new(0,0)],
            [new(7,1),new(0,1),new(0,1),new(0,0),new(0,0)]
        })
            That(!(heroDefinition with { RawSections = [heroLevels,new("difficulty level",tokens)] }).AllowsDifficulty(0,false),
                "冲突、残缺、非法值与引用难度表仍拒绝");
        var maze = selected.Maze;
        RejectDefinition(definition with { Id = 1 }, "dungeon-resource-mismatch");
        RejectDefinition(definition with { MinimumLevel = null }, "dungeon-level-unknown");
        RejectDefinition(definition with { MinimumLevel = 2 }, "dungeon-level-too-low");
        RejectDefinition(definition with { Mazes = [definition.Mazes[0]] }, "quest-maze-unavailable");
        var duplicate = maze with { SourceOrdinal = maze.SourceOrdinal + 1 };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [maze, duplicate] },character,3,3145,0,out var deduplicated,out _) &&
            deduplicated!.Maze == maze, "显式任务的完整等价路线保留首次声明");
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [maze, duplicate] },character,3,0,0,out var autoDuplicate,out _,quests: quests) &&
            autoDuplicate is { RouteQuestId: 3145, Maze.SourceOrdinal: 1 }, "普通选图的活动任务路线也去重");
        var completedMaze = maze with { QuestConnectionTokens = [new(0,1),new(0,3145),new(0,-1)] };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [completedMaze,completedMaze with { SourceOrdinal = 9 }] },
            character with { ActiveMainQuests = [], CompletedQuests = [3145] },3,0,0,out var completedRoute,out _,quests: quests) &&
            completedRoute!.RouteQuestId == 3145, "普通选图的完成任务路线也去重");
        foreach (var different in new[]
        {
            maze with { ResourceError = "fixture" },
            maze with { ClearConditions = [new("hunt monster",999,1)] },
            maze with { WarpMaps = [new(maze.Starts[0],maze.Bosses[0])] },
            maze with { BossEntrance = new(0,0,[new(999,0,1)]) },
            maze with { MapSpecification = maze.MapSpecification!.Select((map,index) => index == 0 ? map with { CandidateIds = [map.Id,999] } : map).ToArray() }
        })
        {
            RejectDefinition(definition with { Mazes = [maze,different] }, "quest-maze-ambiguous");
            That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze,duplicate,different] },character,3,0,0,out _,out var mixed,quests: quests) &&
                mixed == "quest-maze-ambiguous", "重复项不能掩盖不同路线的真实歧义");
        }
        var differentQuest = maze with { QuestConnectionTokens = [new(0,0),new(0,3146),new(0,-1)] };
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze,differentQuest] },
            character with { ActiveMainQuests = [new(0,3145,1),new(1,3146,1)] },3,0,0,out _,out _,quests: quests),
            "地图相同但任务身份不同仍拒绝歧义");
        var difficultyRoute = maze with { QuestConnectionTokens = [new(0,0),new(0,3145),new(0,0)] };
        RejectDefinition(definition with { Mazes = [maze,difficultyRoute] }, "quest-maze-ambiguous");

        foreach (ushort id in new ushort[] { 7104,7105 })
        {
            var actual = character with { Level = 20, GrowType = 5, ActiveMainQuests = [new(0,id,1)] };
            That(DungeonEntryPlan.TryCreate(catalog,actual,1000,id,0,out _,out _,quest: quests.Get(id),quests: quests) &&
                DungeonEntryPlan.TryCreate(catalog,actual,1000,0,0,out var routed,out _,quests: quests) && routed!.RouteQuestId == id,
                "匹配剑影转职资源的显式和普通选图入口均可规划");
        }
        var vagabond = quests.Get(13102);
        var candidate = character with { Job = 11, Level = 15, GrowType = 0, CompletedQuests = [3155],
            ActiveMainQuests = [new(0,13102,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,candidate,7143,13102,0,out var vagabondPlan,out var vagabondReason,
            quest: vagabond,quests: quests), "流浪武士转职使用资源声明的无连接迷宫: " + vagabondReason);
        That(vagabondPlan is { QuestId: 13102, RouteQuestId: 13102, StartMap.Id: 76115, BossMap.Id: 76118 } &&
            vagabondPlan.QuestAcceptanceId == candidate.ActiveMainQuests[0].AcceptanceId && vagabondPlan.Maze.StaticTopology.Supported,
            "转职路线保留原任务接取身份、真实四房终点和可走拓扑");
        RejectMaze(maze with { QuestConnectionTokens = [new(0, 0), new(7, 3145), new(0, -1)] }, "quest-maze-unavailable");
        RejectMaze(maze with { QuestConnectionTokens = [new(0, 0), new(0, 3145)] }, "quest-maze-unavailable");
        RejectMaze(maze with { QuestConnectionTokens = [new(0, 1), new(0, 3145), new(0, -1)] }, "quest-maze-unavailable");
        RejectMaze(maze with { MapSpecification = null }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { Starts = [] }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { Starts = [maze.Starts[0], maze.Starts[0]] }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { Bosses = [] }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { Bosses = [new DungeonPoint(maze.Width, 0)] }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { MapSpecification = maze.MapSpecification!.Where(map => map.Id != selected.StartMap.Id).ToArray() }, "quest-maze-map-unavailable");
        RejectMaze(maze with { MapSpecification = [.. maze.MapSpecification!, selected.StartMap] }, "quest-maze-map-unavailable");
        RejectMaze(maze with { MapSpecification = maze.MapSpecification!.Select(map => map == selected.BossMap ? map with { Kind = "map" } : map).ToArray() }, "quest-maze-map-unavailable");

        // 改动 fixture 的资源坐标与编号，证明计划使用输入定义而非内嵌的地图或坐标常量。
        var fixtureStart = new DungeonMap("map", new(1, 0), 123, "map/fixture-start.map");
        var fixtureBoss = new DungeonMap("boss", new(0, 0), 456, "map/fixture-boss.map");
        var movedMaze = maze with { SourceOrdinal = 7, Width = 2, Height = 1, Starts = [fixtureStart.Position], Bosses = [fixtureBoss.Position], MapSpecification = [fixtureStart, fixtureBoss] };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [movedMaze] }, character, 3, 3145, 0, out var moved, out _)
            && moved is not null && moved.StartMap == fixtureStart && moved.BossMap == fixtureBoss && moved.Maze.SourceOrdinal == 7,
            "plan derives map IDs, coordinates and source ordinal from resource definitions");
        var parent = QuestEligibilityTests.Quest(100) with { Condition = "[quest clear]", SubType = 2, IntData = [101,102] };
        var otherMaze = movedMaze with { QuestConnectionTokens = [new(0,0),new(0,100),new(0,-1)] };
        var otherCharacter = character with { ActiveMainQuests = [new(0,100,2)] };
        That(DungeonEntryPlan.TryCreate(definition with { Id = 17, Mazes = [otherMaze] },otherCharacter,17,100,0,out var general,out _,quest: parent) &&
            general is { QuestId: 100, DungeonId: 17, BossMap.Id: 456 }, "不同副本和任务号、父聚合初值2按显式资源路线生成");
        var clearTarget = parent with { Condition = "[clear map]", SubType = -1, IntData = [999] };
        That(!DungeonEntryPlan.TryCreate(definition with { Id = 17, Mazes = [otherMaze] },otherCharacter with { ActiveMainQuests = [new(0,100,1)] },
            17,100,0,out _,out var mismatch,quest: clearTarget) && mismatch == "quest-terminal-map-unavailable", "任务目标地图不属于路线Boss时拒绝，不替换路线");
        var child = parent with { Id = 101, MainQuest = 100 };
        var unlinkedMaze = otherMaze with { QuestConnectionTokens = [] };
        var unlinkedDefinition = definition with { Id = 17, Mazes = [unlinkedMaze] };
        var unlinkedQuest = clearTarget with { IntData = [456], Definition = clearTarget.Definition! with { Dungeons = [new(17,-1)] } };
        var pendingClear = otherCharacter with { ActiveMainQuests = [new(0,100,1)] };
        var alias = fixtureBoss with { Kind = "layered", Position = fixtureStart.Position };
        var aliasMaze = otherMaze with { LayeredMaps = [alias] };
        var aliasAccepted = new ActiveMainQuest(0,100,1) { AcceptanceId = Guid.NewGuid() };
        var aliasCharacter = otherCharacter with { ActiveMainQuests = [aliasAccepted] };
        That(DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [aliasMaze] },aliasCharacter,17,100,0,
            out var aliasEntry,out _,quest: unlinkedQuest) && aliasEntry!.TerminalLayerMap == alias &&
            aliasEntry.BossMap == fixtureBoss && aliasEntry.TerminalLayerAcceptanceId == aliasAccepted.AcceptanceId,
            "不同编号及坐标的单地图资源同样冻结末层，不依赖真实任务号");
        foreach (var invalid in new[]
        {
            aliasMaze with { LayeredMaps = [alias,alias with { Path = "map/second.map" }] },
            aliasMaze with { LayeredMaps = [alias with { Path = "map/other.map" }] },
            aliasMaze with { LayeredMaps = [alias,fixtureStart with { Kind = "layered" }] }
        })
            That(!DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [invalid] },aliasCharacter,17,100,0,
                out _,out _,quest: unlinkedQuest), "歧义、异路径及非末层不能回退为基础Boss通关");
        That(!DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [aliasMaze] },pendingClear,17,100,0,
            out _,out _,quest: unlinkedQuest), "末层别名缺少真实接取身份时拒绝");
        var clearedAlias = aliasCharacter with { ActiveMainQuests = [aliasAccepted with { ConditionState = 0 }] };
        That(!DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [aliasMaze] },clearedAlias,17,100,0,
            out _,out _,quest: unlinkedQuest) &&
            DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [aliasMaze] },clearedAlias,17,100,0,
                out var repeatedAlias,out _,allowClearedQuest:true,quest: unlinkedQuest) &&
            repeatedAlias!.TerminalLayerMap == alias && repeatedAlias.TerminalLayerAcceptanceId == aliasAccepted.AcceptanceId,
            "裸state0拒绝；原入口已核对持久清图凭据的同接取重入保留实际末层");
        var baseMapMaze = otherMaze with { MapSpecification = [fixtureStart,fixtureBoss with { Kind = "map" }] };
        var baseMapDefinition = definition with { Id = 17, Mazes = [baseMapMaze] };
        That(DungeonEntryPlan.TryCreate(baseMapDefinition,pendingClear,17,100,0,out var baseMapPlan,out _,quest: unlinkedQuest) &&
            baseMapPlan!.BaseBossMap == fixtureBoss && baseMapMaze.MapSpecification.Single(map => map.Position == fixtureBoss.Position).Kind == "map",
            "不同编号的显式清图目标仅投影唯一Boss坐标，不改原始map声明");
        var sharedStartMaze = baseMapMaze with { Starts = [fixtureBoss.Position] };
        That(DungeonEntryPlan.TryCreate(baseMapDefinition with { Mazes = [sharedStartMaze] },pendingClear,17,100,0,
            out var sharedStart,out _,quest: unlinkedQuest) && sharedStart!.StartMap == sharedStart.BaseBossMap &&
            sharedStart.Maze.MapSpecification!.Contains(sharedStart.StartMap) &&
            sharedStartMaze.MapSpecification!.Single(map => map.Position == fixtureBoss.Position).Kind == "map",
            "起点与Boss共图时同步本次投影身份，正式Run可复用迷宫内的起点，原资源仍保持map");
        var conditionalBoss = fixtureStart with { Position = fixtureBoss.Position, IsConditional = true };
        var conditionalStart = fixtureBoss with { Position = fixtureStart.Position, Kind = "map", IsConditional = true };
        var conditionalMaze = baseMapMaze with { BossEntrance = new(0,0,[]),
            WarpMaps = [new(fixtureBoss.Position,fixtureStart.Position),new(fixtureStart.Position,fixtureBoss.Position)],
            ConditionalMaps = [conditionalBoss,conditionalStart] };
        That(DungeonEntryPlan.TryCreate(baseMapDefinition with { Mazes = [conditionalMaze] },pendingClear,17,100,0,
            out var conditionalPlan,out _,quest: unlinkedQuest) &&
            conditionalPlan!.Maze.ConditionalMaps.SequenceEqual([conditionalBoss with { Kind = "boss" },conditionalStart]) &&
            conditionalMaze.ConditionalMaps[0].Kind == "map",
            "条件传送仅同步Boss来源的投影种类，保留替换图身份、其他来源及共享资源");
        foreach (var invalid in new[] { unlinkedQuest with { IntData = [fixtureStart.Id] }, parent })
            That(!DungeonEntryPlan.TryCreate(baseMapDefinition,pendingClear,17,100,0,out _,out _,quest: invalid),
                "中途目标及父聚合条件不能将基础map升级为Boss");
        That(DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear,17,100,0,out var unlinked,out _,quest: unlinkedQuest) &&
            unlinked is { QuestId: 100, RouteQuestId: 100, BossMap.Id: 456 },
            "合成不同任务和地图同样复用明确声明的唯一无连接清图路线，不硬编码转职编号");
        foreach (var unsupported in new[] { unlinkedQuest with { Definition = unlinkedQuest.Definition! with { Dungeons = [] } },
            unlinkedQuest with { Definition = unlinkedQuest.Definition! with { Dungeons = [new(18,-1)] } },
            unlinkedQuest with { Condition = "[hunt enemy]" } })
            That(!DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear,17,100,0,out _,out _,quest: unsupported),
                "缺少地下城归属或非清图目标不能借用无连接路线");
        That(!DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear,17,100,0,out _,out var wrongTarget,
                quest: unlinkedQuest with { IntData = [999] }) && wrongTarget == "quest-terminal-map-unavailable" &&
            !DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [unlinkedMaze,unlinkedMaze with { SourceOrdinal = 8 }] },
                pendingClear,17,100,0,out _,out var multiple,quest: unlinkedQuest) && multiple == "quest-maze-ambiguous",
            "无连接路线仍核对目标且拒绝多个候选，不猜测地图或随机绕过歧义");
        That(!DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [unlinkedMaze,otherMaze with {
                QuestConnectionTokens = [new(0,0),new(0,100),new(0,1)] }] },pendingClear,17,100,0,out _,out _,quest: unlinkedQuest) &&
            !DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear with { ActiveMainQuests = [] },17,100,0,out _,out _,quest: unlinkedQuest),
            "既有任务连接的难度不符和未接取不能被无连接回退放行");
        That(DungeonEntryPlan.TryCreate(definition with { Id = 17, Mazes = [otherMaze,otherMaze with { SourceOrdinal = 8 }] },
            otherCharacter with { ActiveMainQuests = [new(0,101,1)] },17,101,0,out var fallback,out _,quest: child) &&
            fallback!.RouteQuestId == 100, "子任务回退到父任务的重复路线也去重");
        var sequentialChild = unlinkedQuest with { Id = 102, MainQuest = 100 };
        var sequentialCatalog = new DfoServer.GameWorld.Quests.QuestCatalog(new()
            { [100] = parent, [101] = QuestEligibilityTests.Quest(101) with { MainQuest = 100 }, [102] = sequentialChild });
        var childMaze = otherMaze with { QuestConnectionTokens = [new(0,0),new(0,102),new(0,-1)] };
        var childDefinition = definition with { Id = 17, Mazes = [childMaze] };
        var sequence = otherCharacter with { CompletedQuests = [101],
            ActiveMainQuests = [new(5,100,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(childDefinition,sequence,17,100,0,out var parentRoute,out _,quest: parent,quests: sequentialCatalog) &&
            parentRoute is { QuestId: 100, RouteQuestId: 100, ActiveQuestSlot: 5, BossMap.Id: 456 } &&
            parentRoute.QuestAcceptances.SequenceEqual(sequence.ActiveMainQuests),
            "不同父子编号同样按真实完成集选择当前子路线，保留父请求身份和接取快照");
        var childAccepted = sequence with { ActiveMainQuests = [sequence.ActiveMainQuests[0],new(6,102,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(childDefinition,childAccepted,17,100,0,out var acceptedRoute,out _,quest: parent,quests: sequentialCatalog) &&
            acceptedRoute!.QuestAcceptances.SequenceEqual(childAccepted.ActiveMainQuests) &&
            DungeonEntryPlan.TryCreate(childDefinition,childAccepted,17,102,0,out var directChild,out _,quest: sequentialChild,quests: sequentialCatalog) &&
            directChild is { QuestId: 102, BossMap.Id: 456 }, "已接取子任务保留自己的身份，父/子两种真实请求都匹配相同路线");
        foreach (var invalid in new[] { sequence with { CompletedQuests = [] }, sequence with { CompletedQuests = [101,102] },
            sequence with { CompletedQuests = [100,101] }, sequence with { ActiveMainQuests = [sequence.ActiveMainQuests[0] with { ConditionState = 2 }] },
            sequence with { ActiveMainQuests = [sequence.ActiveMainQuests[0] with { AcceptanceId = Guid.Empty }] },
            childAccepted with { ActiveMainQuests = [sequence.ActiveMainQuests[0],childAccepted.ActiveMainQuests[1] with { ConditionState = 0 }] } })
            That(!DungeonEntryPlan.TryCreate(childDefinition,invalid,17,100,0,out _,out _,quest: parent,quests: sequentialCatalog),
                "顺序完成、父scalar或接取身份不一致时不借用子路线");
        var wrongChild = sequentialChild with { MinimumLevel = 95 };
        var wrongCatalog = new DfoServer.GameWorld.Quests.QuestCatalog(new() { [100] = parent, [101] = sequentialCatalog.Get(101), [102] = wrongChild });
        That(!DungeonEntryPlan.TryCreate(childDefinition,sequence,17,100,0,out _,out _,quest: parent,quests: wrongCatalog) &&
            !DungeonEntryPlan.TryCreate(childDefinition with { Mazes = [childMaze with { QuestConnectionTokens = [new(0,0),new(0,102),new(0,1)] }] },
                sequence,17,100,0,out _,out _,quest: parent,quests: sequentialCatalog),
            "当前子任务等级和迷宫难度不符仍拒绝，不以父请求绕过资格");
        Console.WriteLine($"PASS {Count - checks} dungeon entry plan assertions");

        void Reject(Character input, uint dungeonId, uint questId, byte difficulty, string expected)
        {
            bool accepted = DungeonEntryPlan.TryCreate(catalog, input, dungeonId, questId, difficulty, out var rejected, out string actual);
            That(!accepted && rejected is null && actual == expected, "resource request rejects " + expected);
        }

        void RejectDefinition(DungeonDefinition input, string expected)
        {
            bool accepted = DungeonEntryPlan.TryCreate(input, character, 3, 3145, 0, out var rejected, out string actual);
            That(!accepted && rejected is null && actual == expected, "resource definition rejects " + expected);
        }

        void RejectMaze(DungeonMaze input, string expected) => RejectDefinition(definition with { Mazes = [input] }, expected);

        static DungeonRawSection[] StoryBlocks(int firstQuest, int secondQuest) =>
        [
            new("story mode",[]),new("difficulty size",[new(0,1)]),new("quest list",[new(0,firstQuest)]),
            new("/quest list",[]),new("/story mode",[]),
            new("story mode",[]),new("difficulty size",[new(0,3)]),new("quest list",[new(0,secondQuest)]),
            new("/quest list",[]),new("/story mode",[])
        ];
    }

    static void AuxiliaryEquipmentHuntRoutes(DungeonCatalog catalog, QuestCatalog quests, Character basis)
    {
        var results = new List<string>();
        foreach (var sample in new[] { (Quest:674,Dungeon:163,Difficulty:(byte)2), (Quest:675,Dungeon:82,Difficulty:(byte)2),
            (Quest:675,Dungeon:82,Difficulty:(byte)3) })
        {
            var quest = quests.Get(sample.Quest);
            var active = new ActiveMainQuest(5,checked((ushort)sample.Quest),QuestHuntProgress.Initial(quest)) { AcceptanceId=Guid.NewGuid() };
            var character = basis with { Level=checked((byte)quest.MinimumLevel),ActiveMainQuests=[active] };
            bool created = DungeonEntryPlan.TryCreate(catalog,character,checked((uint)sample.Dungeon),checked((uint)sample.Quest),
                sample.Difficulty,out var plan,out var reason,quest:quest,draw:_ => 0,quests:quests);
            if (!created || plan is not { IsOrdinaryHunt:true,IsOrdinary:true,UsesFatigue:true,RouteQuestId:0 } ||
                plan.QuestId != sample.Quest || plan.Difficulty != sample.Difficulty || plan.ActiveQuestSlot != active.Slot ||
                plan.QuestAcceptanceId != active.AcceptanceId || !plan.QuestAcceptances.SequenceEqual(character.ActiveMainQuests))
                results.Add($"{sample.Quest}/{sample.Dungeon}/{sample.Difficulty}: {reason}");
            if (!created || plan is null) continue;
            var after = active;
            That(QuestHuntProgress.TryTargets(quest,out var targets) && targets.Length == 3,"真实资格任务保留三个猎杀通道");
            foreach (var target in targets)
            {
                var monster = new DungeonPlannedMonster(0,1,target.MonsterId,1,null);
                after = QuestHuntProgress.MonsterDeath([after],plan,quests,monster).Single();
                That(QuestHuntProgress.MonsterDeath([active with { AcceptanceId=Guid.NewGuid() }],plan,quests,monster).Single().ConditionState ==
                    active.ConditionState,"真实入场计划的旧接取不能推进重接任务");
            }
            That(after.ConditionState == 0 && after.AcceptanceId == active.AcceptanceId,"真实入场计划绑定三个实际模板的权威死亡推进");
            foreach (byte rejected in new byte[] { 0,1,4 }.Concat(sample.Dungeon == 163 ? new byte[] { 3 } : []).Distinct())
                That(!DungeonEntryPlan.TryCreate(catalog,character,checked((uint)sample.Dungeon),checked((uint)sample.Quest),rejected,
                    out _,out _,quest:quest,quests:quests),"资格任务保留最低难度与真实DUN上限："+sample.Quest+"/"+rejected);
            That(!DungeonEntryPlan.TryCreate(catalog,character,sample.Dungeon == 163 ? 82u : 163u,checked((uint)sample.Quest),2,
                out _,out _,quest:quest,quests:quests),"资格任务不能进入未声明副本");
            foreach (var invalid in new[] { character with { ActiveMainQuests=[] },character with { CompletedQuests=[sample.Quest] },
                character with { ActiveMainQuests=[active with { AcceptanceId=Guid.Empty }] },
                character with { ActiveMainQuests=[active,new(5,100,1) { AcceptanceId=Guid.NewGuid() }] } })
                That(!DungeonEntryPlan.TryCreate(catalog,invalid,checked((uint)sample.Dungeon),checked((uint)sample.Quest),sample.Difficulty,
                    out _,out _,quest:quest,quests:quests),"资格任务保留唯一活动接取门禁");
            var definition = catalog.Get(sample.Dungeon);
            var normal = definition.Mazes.First(maze => maze.QuestConnectionTokens.Count == 0);
            foreach (var connection in new[] { new PvfToken[] {new(0,0),new(0,sample.Quest),new(0,0)},
                [new(0,2),new(0,100),new(0,-1)] })
                That(!DungeonEntryPlan.TryCreate(definition with { Mazes=[normal,normal with { QuestConnectionTokens=connection }] },
                    character,checked((uint)sample.Dungeon),checked((uint)sample.Quest),sample.Difficulty,out _,out _,quest:quest),
                    "资格任务不绕过自身不满足或未知连接");
            That(!DungeonEntryPlan.TryCreate(definition with { Mazes=[] },character,checked((uint)sample.Dungeon),checked((uint)sample.Quest),
                sample.Difficulty,out _,out _,quest:quest),"资格任务仍要求真实普通迷宫");
        }
        That(results.Count == 0,"真实装备槽资格任务按最低难度进入普通路线并保留接取身份："+string.Join("; ",results));
        var probe = quests.Get(675);
        var pending = basis with { Level=65,ActiveMainQuests=[new(5,675,QuestHuntProgress.Initial(probe)) { AcceptanceId=Guid.NewGuid() }] };
        foreach (int parameter in new[] { -2,-1,0,1,2,3,4 })
            That(DungeonEntryPlan.TryCreate(catalog,pending,82,675,2,out _,out _,quest:probe with
                { Definition=probe.Definition! with { Dungeons=[new(82,parameter)] } }) == (parameter is >= -1 and <= 2),
                "支线声明仅接受不限或不高于请求的0..3最低难度："+parameter);
        foreach (var invalid in new[] { probe with { MainQuest=100 },probe with { Grade="[story]" },probe with { Grade="[achievement]" },
            probe with { SubType=1 },probe with { IntData=[82,3,61710,1] },probe with { IntData=[82,2,-5,1] },
            probe with { Definition=probe.Definition! with { Dungeons=[] } } })
            That(!DungeonEntryPlan.TryCreate(catalog,pending,82,675,2,out _,out _,quest:invalid),
                "最低难度扩展仍拒绝父任务、其它分类、未知子类、较高目标与未知选择符");
        foreach (var sample in new[] { (Quest:649,Dungeon:163,Previous:674,Level:(byte)60), (Quest:650,Dungeon:82,Previous:675,Level:(byte)65) })
        {
            var material = basis with { Level=sample.Level,CompletedQuests=[sample.Previous],
                ActiveMainQuests=[new(5,checked((ushort)sample.Quest),1) { AcceptanceId=Guid.NewGuid() }] };
            That(DungeonEntryPlan.TryCreate(catalog,material,checked((uint)sample.Dungeon),0,2,out var ordinary,out _,quests:quests) &&
                ordinary is { QuestId:0,IsOrdinary:true,IsOrdinaryHunt:false } &&
                !DungeonEntryPlan.TryCreate(catalog,material,checked((uint)sample.Dungeon),checked((uint)sample.Quest),2,out _,out _,
                    quest:quests.Get(sample.Quest),quests:quests),"材料后继活动时普通0仍可入场，不补造材料任务战斗路线");
        }
    }

    static void OrdinaryHuntRoutes(DungeonCatalog catalog, QuestCatalog quests, DungeonRoomCatalog rooms, Character basis)
    {
        int[] letters = [3921,2948,2967,3919,4894,4876,2958,2973,4915,3793,6114];
        foreach (int id in letters)
        {
            var quest = quests.Get(id);
            uint dungeonId = checked((uint)quest.Definition!.Dungeons.Single().DungeonId);
            var active = new ActiveMainQuest(5,checked((ushort)id),QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
            var character = basis with { Level = checked((byte)quest.MinimumLevel), ActiveMainQuests = [active] };
            var alternatives = new HashSet<int>();
            int choices = dungeonId == 40 ? 3 : dungeonId == 162 ? 2 : 1;
            for (int choice = 0; choice < choices; choice++)
            {
                That(DungeonEntryPlan.TryCreate(catalog,character,dungeonId,checked((uint)id),0,out var plan,out var reason,
                    quest:quest,draw:bound => choice % bound,quests:quests) &&
                    plan is { IsOrdinaryHunt:true,IsOrdinary:true,UsesFatigue:true,RouteQuestId:0 } && plan.QuestId == id &&
                    plan.ActiveQuestSlot == active.Slot && plan.QuestAcceptanceId == active.AcceptanceId &&
                    plan.QuestAcceptances.SequenceEqual(character.ActiveMainQuests),
                    "真实匿名信显式任务请求保留接取身份并沿普通疲劳路线："+id+"/"+reason);
                var entry = plan!;
                alternatives.Add(dungeonId == 40 ? entry.Maze.MapSpecification!.Single(map => map.Position == new DungeonPoint(1,2)).Id :
                    entry.Maze.SourceOrdinal);
                var monsters = entry.Maze.MapSpecification!.SelectMany(map => DungeonMonsterPlan.FromRoom(
                    rooms.Get(map,requirePassiveDefinitions:false),entry.CharacterId,1,entry.BasisLevel,playerTeam:entry.PlayerTeam).Monsters).ToArray();
                var target = monsters.First(monster => monster.MonsterId == quest.IntData[2] && monster.IsBlocking && monster.CanReportDeath);
                That(QuestHuntProgress.MonsterDeath([active],entry,quests,target).Single().ConditionState == active.ConditionState-1 &&
                    QuestHuntProgress.MonsterDeath([active with { AcceptanceId = Guid.NewGuid() }],entry,quests,target).Single().ConditionState == active.ConditionState,
                    "每个真实有限候选均可由原怪物死亡推进，放弃重接不消费旧实例："+id+"/"+choice);
            }
            That(alternatives.Count == choices,"普通随机保留全部有限迷宫或地图候选："+id);
            That(DungeonEntryPlan.TryCreate(catalog,character,dungeonId,0,0,out var ordinary,out _,draw:_ => 0,quests:quests) &&
                ordinary is { QuestId:0,RouteQuestId:0,IsOrdinaryHunt:false,IsOrdinary:true },"原匿名信活动快照的普通0入口保持原模式");
        }

        var letter = quests.Get(2948);
        var accepted = new ActiveMainQuest(5,2948,QuestHuntProgress.Initial(letter)) { AcceptanceId = Guid.NewGuid() };
        var pending = basis with { Level=45,ActiveMainQuests=[accepted] };
        var definition = catalog.Get(40);
        var normal = definition.Mazes.Single(maze => maze.QuestConnectionTokens.Count == 0);
        foreach (var sample in new[] { (Level:(byte)93,Count:10),(Level:(byte)95,Count:11) })
        {
            var active = letters.Take(sample.Count).Select((id,slot) => new ActiveMainQuest(checked((byte)slot),checked((ushort)id),
                QuestHuntProgress.Initial(quests.Get(id))) { AcceptanceId=Guid.NewGuid() }).ToArray();
            That(DungeonEntryPlan.TryCreate(catalog,basis with { Level=sample.Level,ActiveMainQuests=active },40,2948,0,out var multiple,out _,
                quest:letter,draw:_ => 0,quests:quests) && multiple is { IsOrdinaryHunt:true,RouteQuestId:0 } &&
                multiple.QuestAcceptances.SequenceEqual(active),"合法等级的10/11活动匿名信分别冻结全部原接取，不混称截图现场");
        }
        var twoMazes = quests.Get(4894);
        var twoActive = new ActiveMainQuest(5,4894,QuestHuntProgress.Initial(twoMazes)) { AcceptanceId=Guid.NewGuid() };
        foreach (int invalid in new[] {-1,2})
            Reject<InvalidDataException>(() => DungeonEntryPlan.TryCreate(catalog,basis with { Level=60,ActiveMainQuests=[twoActive] },
                162,4894,0,out _,out _,quest:twoMazes,draw:_ => invalid,quests:quests),"普通猎杀迷宫随机索引越界仍拒绝");
        foreach (var invalid in new[] {
            letter with { MainQuest=100 }, letter with { Grade="[daily]" }, letter with { SubType=1 },
            letter with { IntData=[-1,-1,-5,2] }, letter with { IntData=[41,-1,1014,2] },
            letter with { IntData=[40,1,1014,2] }, letter with { IntData=[-1,-1,1014,0] },
            letter with { Definition=letter.Definition! with { Dungeons=[] } },
            letter with { Definition=letter.Definition! with { Dungeons=[new(41,-1)] } },
            letter with { Definition=letter.Definition! with { Dungeons=[new(40,-2)] } } })
            That(!DungeonEntryPlan.TryCreate(definition,pending,40,2948,0,out _,out _,quest:invalid),
                "普通猎杀只接入已证任务形状、正目标、本图及难度声明");
        foreach (var connection in new[] {
            new PvfToken[] {new(0,0),new(0,2948),new(0,1)},
            [new(0,1),new(0,2948),new(0,-1)], [new(0,2),new(0,100),new(0,-1)],
            [new(0,0),new(0,100)] })
            That(!DungeonEntryPlan.TryCreate(definition with { Mazes=[normal,normal with { QuestConnectionTokens=connection }] },
                pending,40,2948,0,out _,out _,quest:letter),"本任务不满足的连接或未知格式不能触发普通猎杀回退");
        foreach (var invalid in new[] { pending with { ActiveMainQuests=[] }, pending with { CompletedQuests=[2948] },
            pending with { ActiveMainQuests=[accepted with { AcceptanceId=Guid.Empty }] },
            pending with { ActiveMainQuests=[accepted,new(5,100,1) { AcceptanceId=Guid.NewGuid() }] } })
            That(!DungeonEntryPlan.TryCreate(definition,invalid,40,2948,0,out _,out _,quest:letter),"普通猎杀保留活动任务与唯一接取门禁");
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes=definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count != 0).ToArray() },
            pending,40,2948,0,out _,out _,quest:letter),"缺少真实普通迷宫仍拒绝");
    }

    static void HarlemSeekingRoutes(DungeonCatalog catalog, QuestCatalog quests, Character basis)
    {
        // #256：黑市EXP 6053/6056/6059（seeking sub type 0）沿普通路线进入断头谷7322；6060同样入场，并带上精英出现率。
        foreach (int id in new[] { 6053, 6056, 6059 })
        {
            var quest = quests.Get(id);
            var accepted = basis with { Level = 93, CompletedQuests = [6050],
                ActiveMainQuests = [new(5,checked((ushort)id),1) { AcceptanceId = Guid.NewGuid() }] };
            That(DungeonEntryPlan.TryCreate(catalog,accepted,7322,checked((uint)id),0,out var plan,out var reason,quest: quest,quests: quests) &&
                plan is { IsOrdinaryHunt: true,RouteQuestId: 0 } && plan.QuestAcceptances.Single().QuestId == id,
                "黑市寻物任务沿普通路线入场断头谷：" + id + "/" + reason);
            That(!DungeonEntryPlan.TryCreate(catalog,accepted,7322,checked((uint)id),0,out _,out _,
                quest: quest with { IntData = [quest.IntData[0],4] },quests: quests),"形状被改动的寻物任务不借用该路线：" + id);
            That(!DungeonEntryPlan.TryCreate(catalog,accepted,3,checked((uint)id),0,out _,out _,quest: quest,quests: quests),
                "未声明本图的寻物任务不能进入其它地下城：" + id);
        }
        var negative = basis with { Level = 93, CompletedQuests = [6050],
            ActiveMainQuests = [new(5,6060,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,negative,7322,6060,0,out var elitePlan,out var eliteReason,quest: quests.Get(6060),quests: quests) &&
            elitePlan is { IsOrdinaryHunt: true } && elitePlan.ChampionPercents.SequenceEqual(new[] { 25, 40, 60, 60 }),
            "6060走普通路线进断头谷，并带上精英出现率：" + eliteReason);

        // #256 系统排查：6051-6060 每个任务按其 PVF 声明的目标副本逐个走入场规划（已接取、等级达标）。
        // 6054 等 [hunt monster] 的 -3（普通 Boss）目标曾被入场规划误拒为 quest-maze-unavailable。
        var swept = new List<string>();
        foreach (int id in Enumerable.Range(6051,10))
        {
            var quest = quests.Get(id);
            foreach (var row in quest.Definition!.Dungeons)
            {
                var resource = catalog.Get(row.DungeonId);
                uint scalar = quest.Condition == "[hunt monster]" ? QuestHuntProgress.Initial(quest) : 1;
                var character = basis with { Level = checked((byte)Math.Max(quest.MinimumLevel,resource.MinimumLevel ?? 1)),
                    CompletedQuests = [6050], ActiveMainQuests = [new(5,checked((ushort)id),scalar) { AcceptanceId = Guid.NewGuid() }] };
                byte difficulty = checked((byte)Math.Max(row.Parameter,0));
                bool clearType = quest.Condition == "[condition under clear]"; // 通关类任务无任务号入场（普通入场，由通关进度计数）
                bool ok = DungeonEntryPlan.TryCreate(catalog,character,checked((uint)row.DungeonId),clearType ? 0u : checked((uint)id),difficulty,
                    out var plan,out var why,quest:clearType ? null : quest,quests:quests);
                swept.Add($"{id}|{quest.Condition}|dungeon {row.DungeonId}|{(ok ? "OK" : why)}");
                That(ok && (clearType ? plan is { IsOrdinary: true } : plan is { IsOrdinaryHunt: true }),"EXP 任务按声明副本可入场：" + id + "/" + row.DungeonId + "/" + why);
            }
        }
        Console.WriteLine("SWEEP-HARLEM-EXP " + string.Join(" ; ",swept));
    }
    static void AwakeningDungeonRoutes(DungeonCatalog catalog, QuestCatalog quests, DungeonRoomCatalog rooms, Character basis)
    {
        // #258：一次/二次觉醒的疑惑之村杀怪与收集阶段声明“dungeon info 53 2”，没有专用迷宫连接，只能沿普通迷宫进入。
        var resource = catalog.Get(53);
        var awakening = quests.All.Where(quest => quest.Grade == "[achievement]" && quest.Definition is { JobChange: 2 or 3 } declared &&
            declared.Dungeons.Any(row => row.DungeonId == 53 && row.Parameter == 2) &&
            quest.Condition is "[hunt monster]" or "[seeking]").OrderBy(quest => quest.Id).ToArray();
        That(awakening.Count(quest => quest.Condition == "[hunt monster]") >= 50 && awakening.Count(quest => quest.Condition == "[seeking]") >= 50,
            "真实疑惑之村觉醒杀怪与收集任务资源均已读取");
        foreach (var quest in awakening)
        {
            uint scalar = quest.Condition == "[hunt monster]" ? QuestHuntProgress.Initial(quest) : 1;
            var active = new ActiveMainQuest(3,checked((ushort)quest.Id),scalar) { AcceptanceId = Guid.NewGuid() };
            var character = basis with { Level = checked((byte)Math.Max(quest.MinimumLevel,resource.MinimumLevel ?? 1)), ActiveMainQuests = [active] };
            That(DungeonEntryPlan.TryCreate(catalog,character,53,checked((uint)quest.Id),2,out var plan,out var reason,quest:quest,draw:_ => 0,quests:quests) &&
                plan is { IsOrdinaryHunt:true,IsOrdinary:true,RouteQuestId:0,Difficulty:2 } && plan.QuestId == quest.Id &&
                plan.ActiveQuestSlot == active.Slot && plan.QuestAcceptanceId == active.AcceptanceId &&
                plan.QuestAcceptances.SequenceEqual(character.ActiveMainQuests),"觉醒任务请求在勇士难度进入普通迷宫并保留接取身份："+quest.Id+"/"+reason);
            foreach (byte low in new byte[] { 0,1 })
                That(!DungeonEntryPlan.TryCreate(catalog,character,53,checked((uint)quest.Id),low,out _,out _,quest:quest,draw:_ => 0,quests:quests),
                    "低于任务声明难度的觉醒任务请求仍拒绝："+quest.Id+"/"+low);
            That(DungeonEntryPlan.TryCreate(catalog,character,53,0,2,out var ordinary,out _,draw:_ => 0,quests:quests) &&
                ordinary is { QuestId:0,IsOrdinaryHunt:false,IsOrdinary:true },"觉醒任务活动时普通0入口保持原模式");
        }
    }

    // 实验性（#259，依据来自 Go 项目反汇编，未经 V31 客户端验证）：比赛副本放行指定难度，起点/Boss 取地图反向声明的唯一比赛图。
    sealed class SequenceComparer : IEqualityComparer<int[]>
    {
        public bool Equals(int[]? x,int[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(int[] value) => value.Length;
    }

    static void TournamentEntry(DungeonCatalog catalog,Character character,DungeonRoomCatalog rooms)
    {
        foreach (var scenario in new[] { (Dungeon:123,Map:95265),(Dungeon:124,Map:95266) })
        {
            var resource = catalog.Get(scenario.Dungeon);
            var eligible = character with { Level = 75, ActiveMainQuests = [], CompletedQuests = [] };
            That(resource.IsTournament && resource.TryGetDesignatedDifficulty(out var designated) && designated == 2 &&
                resource.AllowsDifficulty(2,false) && !resource.AllowsDifficulty(0,false) && !resource.AllowsDifficulty(1,false),
                "比赛副本只放行指定难度：" + scenario.Dungeon);
            bool created = DungeonEntryPlan.TryCreate(catalog,eligible,checked((uint)scenario.Dungeon),0,2,out var plan,out var reason);
            That(created && plan is { Difficulty: 2 } && plan.StartMap.Id == scenario.Map && plan.BossMap.Id == scenario.Map,
                "比赛副本按反向声明确定唯一比赛图：" + scenario.Dungeon + "/" + reason);
            // 单图房间 + 四轮对手：对阵表/对手一致，能创建 Run，并能编出 NOTI386/387 包体。
            var declared = plan!;
            var room = rooms.Get(declared.StartMap,requirePassiveDefinitions: false,allowUnresolvedSpecialMetadata: false);
            var source = rooms.ReadTournamentSource(declared.StartMap);
            var roomPlan = DungeonTournamentPlan.CreateRoomPlan(rooms,room,source,12345,eligible.Id,100,declared.BasisLevel!.Value,declared.PlayerTeam);
            var route = new DungeonFixedRoute([roomPlan],[]);
            var tournament = roomPlan.Tournament!;
            That(source.PartyCount == 1 && source.Enemies.Count >= 15 && source.HasStartArea && tournament.Rounds.Count == 4 &&
                tournament.Rounds.Select(round => round.Count).SequenceEqual([16,8,4,2]) && tournament.Rounds.All(round => round.Count(team => team.Player) == 1) &&
                tournament.Opponents.Count == 4 && tournament.Opponents.Select(row => row.Round).SequenceEqual([1,2,3,4]) &&
                tournament.Opponents.Select(row => row.ObjectKey).Distinct().Count() == 4 &&
                tournament.Opponents.Take(3).All(row => row.Type == 0) && tournament.Opponents[3].Type == 3 &&
                tournament.Opponents.All(row => row.Level == declared.BasisLevel) &&
                roomPlan.Monsters.Count(monster => monster.IsBlocking) == 4 &&
                tournament.Rounds[0].Single(team => team.Player) is not null,
                "比赛对阵表与四轮对手：" + scenario.Dungeon);
            That(declared.Maze.StaticTopology.Supported && !declared.UsesFatigue && declared.IsTournament &&
                SinglePlayerDungeonRun.TryCreate(declared,1,100,route,new NativeDungeonCombatAuthority(),out _,out _),
                "比赛单图可装载房间并创建 Run：" + scenario.Dungeon);
            var info = DfoServer.Network.Builders.DungeonPackets.TournamentInfo(declared,tournament);
            // 头 6B + 四轮对阵（每轮 2B + 队数 * 7B，16+8+4+2 队，玩家队成员 code/战力为 0）+ 四轮对手（每轮 1B + 8B）
            That(info.Length == 6 + 4 * 2 + 30 * 7 + 4 * (1 + 8) &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info) == (uint)scenario.Dungeon && info[4] == 2 && info[5] == 1 &&
                DfoServer.Network.Builders.DungeonPackets.TournamentMapInfo(declared,12345) is { Length: 15 } mapInfo &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mapInfo.AsSpan(8)) == (uint)scenario.Map && mapInfo[7] == 1,
                "NOTI386/387 包体长度与字段：" + scenario.Dungeon);
            // 同一 seed 对阵表可重现，不同 seed 通常不同。
            That(DungeonTournamentPlan.PlanBracket(source,7).Select(round => round.Select(team => team.Members.Sum(row => row.Code)).ToArray())
                .SequenceEqual(DungeonTournamentPlan.PlanBracket(source,7).Select(round => round.Select(team => team.Members.Sum(row => row.Code)).ToArray()),
                    new SequenceComparer()),"对阵表按 seed 可重现");            That(!DungeonEntryPlan.TryCreate(catalog,eligible,checked((uint)scenario.Dungeon),0,0,out _,out var rejected) && rejected == "difficulty-unsupported",
                "比赛副本非指定难度被拒绝：" + scenario.Dungeon);
        }
        That(!catalog.Get(3).IsTournament && !catalog.Get(3).AllowsDifficulty(2,false),"非比赛副本的难度规则不变");
    }}
