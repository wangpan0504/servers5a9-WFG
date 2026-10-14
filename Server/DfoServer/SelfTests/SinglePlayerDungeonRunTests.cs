using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class SinglePlayerDungeonRunTests
{
    public static void RunCore()
    {
        int checks = Count;
        VerifyLayeredBossTerminal();
        VerifyExplicitTerminalLayer();
        VerifyPassiveClearCondition();
        var authority = new FixtureCombatAuthority();
        var start = new DungeonMap("map", new(0, 0), 1, "map/fixture-start.map");
        var friendly = new DungeonMap("map", new(1, 0), 2, "map/fixture-friendly.map");
        var boss = new DungeonMap("boss", new(2, 0), 3, "map/fixture-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "fixture-teams", [start.Position], [boss.Position], [start, friendly, boss], []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, start, boss);
        var route = new DungeonFixedRoute([WithTeams(Plan(start, 10, 11), 100, 0),
            WithTeams(Plan(friendly, 12), 0), WithTeams(BossPlan(boss, 13, 14), 100, 0)],
            [new(start.Position, friendly.Position), new(friendly.Position, boss.Position)]);
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var created, out _),
            "resource-qualified friendly actors preserve the full route and key namespace");
        var run = created!;
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Snapshot.ConsumedFatigue == 0, "任务专用路线不计入普通房间疲劳");
        var fatigueRoute = new DungeonFixedRoute([WithTeams(Plan(start, 30), 0), WithTeams(Plan(friendly, 31), 0),
            BossPlan(boss, 32)], route.Connections);
        That(SinglePlayerDungeonRun.TryCreate(entry with { QuestId = 0, RouteQuestId = 0, ActiveQuestSlot = byte.MaxValue }, 2, 200, fatigueRoute,
            authority, out var fatigueRun, out _) && fatigueRun!.Snapshot.ConsumedFatigue == 0,
            "普通实例在加载成功前不计应耗疲劳");
        var fatigue = fatigueRun!;
        var loadingIdentity = fatigue.Snapshot.Identity;
        Guid loadingAction = Guid.NewGuid();
        fatigue.CompleteLoading(loadingIdentity, loadingAction);
        var firstFatigueRoom = fatigue.Snapshot;
        That(firstFatigueRoom.ConsumedFatigue == 1 &&
            fatigue.CompleteLoading(loadingIdentity, loadingAction).Outcome == DungeonRunOutcome.Duplicate &&
            fatigue.CompleteLoading(fatigue.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected &&
            fatigue.Snapshot.ConsumedFatigue == 1, "首次房间计一点，重复加载及重放不重复计数");
        fatigue.Move(fatigue.Snapshot.Identity, Guid.NewGuid(), friendly.Position);
        That(fatigue.Snapshot.ConsumedFatigue == 1, "过房预留不提前计应耗疲劳");
        fatigue.CompleteLoading(fatigue.Snapshot.Identity, Guid.NewGuid());
        fatigue.Move(fatigue.Snapshot.Identity, Guid.NewGuid(), start.Position);
        fatigue.CompleteLoading(fatigue.Snapshot.Identity, Guid.NewGuid());
        That(fatigue.Snapshot.ConsumedFatigue == 2 && firstFatigueRoom.ConsumedFatigue == 1 &&
            fatigue.CompleteLoading(loadingIdentity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected,
            "回访已加载房间及旧身份不计数，已发布快照保持原累计值");
        fatigue.Move(fatigue.Snapshot.Identity, Guid.NewGuid(), friendly.Position);
        fatigue.CompleteLoading(fatigue.Snapshot.Identity, Guid.NewGuid());
        fatigue.Move(fatigue.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        fatigue.CompleteLoading(fatigue.Snapshot.Identity, Guid.NewGuid());
        That(fatigue.Snapshot.ConsumedFatigue == 3, "累计应耗疲劳来自实际首次加载，不重复计算回访");
        var fatigueClear = fatigue.PrepareConfirmedDeath(authority.Death(fatigue.Snapshot.Identity, 32, 1)).Pending!;
        That(fatigueClear.Result.Snapshot.ConsumedFatigue == 3 &&
            fatigue.ConfirmCommittedDeath(fatigueClear).Snapshot.ConsumedFatigue == 3,
            "Boss死亡的待提交与已提交结算快照都保留当局应耗疲劳");
        var original = run.Snapshot;
        That(original.LivingMonsters.SequenceEqual(new ushort[] { 10, 11 }) && run.CurrentLivingMonster(10) is { IsBlocking: true } &&
            run.CurrentLivingMonster(11) is null, "snapshot retains the friendly actor while combat qualification excludes it");
        That(run.Move(original.Identity, Guid.NewGuid(), friendly.Position).Outcome == DungeonRunOutcome.Rejected,
            "a living hostile still blocks movement despite its friendly companion");
        var friendlyDeath = authority.Death(original.Identity, 11, 1);
        That(run.PrepareConfirmedDeath(friendlyDeath) is { Outcome: DungeonRunOutcome.Rejected, Pending: null } &&
            run.AcceptConfirmedDeath(friendlyDeath).Outcome == DungeonRunOutcome.Rejected && run.Snapshot.Identity == original.Identity &&
            run.Snapshot.LivingMonsters.SequenceEqual(original.LivingMonsters) && run.ClearFact is null,
            "friendly reports do not prepare deaths consume sequences change actors or generate clear events");
        var hostileDeath = authority.Death(original.Identity, 10, 1);
        That(run.AcceptConfirmedDeath(hostileDeath).Outcome == DungeonRunOutcome.Accepted &&
            run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }), "only the hostile death removes an actor");
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), friendly.Position).Outcome == DungeonRunOutcome.Accepted,
            "the surviving friendly companion does not block a cleared room");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 12 }) && run.CurrentLivingMonster(12) is null &&
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted,
            "an all-friendly intermediate room remains populated and permits its approved passage");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var beforeBoss = run.Snapshot;
        var bossDeath = authority.Death(beforeBoss.Identity, 13, 2);
        var pending = run.PrepareConfirmedDeath(bossDeath).Pending!;
        That(pending.Result is { Snapshot.Phase: SinglePlayerDungeonPhase.Settlement, ClearFact.ConfirmedDeathEvents.Count: 2 } &&
            pending.Result.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 14 }) &&
            pending.Result.ClearFact!.ConfirmedDeathEvents.SequenceEqual(new[] { hostileDeath.EventId, bossDeath.EventId }) &&
            run.Phase == SinglePlayerDungeonPhase.Dungeon && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 13, 14 }),
            "last hostile prepares settlement with friendly actors alive and no fabricated friendly event");
        var committed = run.ConfirmCommittedDeath(pending);
        That(committed.ClearFact is not null && ReferenceEquals(run.ClearFact, committed.ClearFact) &&
            run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 14 }) && run.Phase == SinglePlayerDungeonPhase.Settlement,
            "committed hostile death preserves the actual clear fact reference and friendly actor");
        That(run.AcceptConfirmedDeath(bossDeath).Outcome == DungeonRunOutcome.Duplicate &&
            run.AcceptConfirmedDeath(authority.Death(beforeBoss.Identity, 13, 3)).Outcome == DungeonRunOutcome.Rejected &&
            run.ClearFact!.ConfirmedDeathEvents.Count == 2 && run.ConfirmCommittedDeath(pending).ClearFact is null,
            "same monster repeats cannot create another death receipt or clear reward fact");
        foreach (int index in new[] { 0, 1 })
        {
            var firstRoom = route.Rooms[0];
            var changed = firstRoom.Monsters[index] with { IsBlocking = !firstRoom.Monsters[index].IsBlocking };
            var forged = firstRoom with { Monsters = firstRoom.Monsters.Select((monster, ordinal) => ordinal == index ? changed : monster).ToArray() };
            That(!SinglePlayerDungeonRun.TryCreate(entry, 2, 200, route with { Rooms = [forged, .. route.Rooms.Skip(1)] }, authority, out _, out _),
                "caller cannot forge blocking eligibility away from the same resource row");
        }
        var defaultRoute = route with { Rooms = [Plan(start, 10, 11), .. route.Rooms.Skip(1)] };
        That(SinglePlayerDungeonRun.TryCreate(entry, 3, 300, defaultRoute, authority, out var defaultRun, out _),
            "fixtures without team metadata retain their former lifecycle contract");
        defaultRun!.CompleteLoading(defaultRun.Snapshot.Identity, Guid.NewGuid());
        defaultRun.AcceptConfirmedDeath(authority.Death(defaultRun.Snapshot.Identity, 10, 1));
        That(defaultRun.CurrentLivingMonster(11) is { IsBlocking: true } &&
            defaultRun.Move(defaultRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position).Outcome == DungeonRunOutcome.Rejected,
            "missing team still requires every ordinary monster death");
        var emptyBossRoute = route with { Rooms = [route.Rooms[0], route.Rooms[1], WithTeams(Plan(boss, 13, 14), 0, 0)] };
        That(SinglePlayerDungeonRun.TryCreate(entry, 4, 400, emptyBossRoute, authority, out var emptyBossRun, out _),
            "all-friendly terminal fixture retains both declared actors");
        emptyBossRun!.CompleteLoading(emptyBossRun.Snapshot.Identity, Guid.NewGuid());
        var onlyDeath = authority.Death(emptyBossRun.Snapshot.Identity, 10, 1);
        emptyBossRun.AcceptConfirmedDeath(onlyDeath);
        emptyBossRun.Move(emptyBossRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position);
        emptyBossRun.CompleteLoading(emptyBossRun.Snapshot.Identity, Guid.NewGuid());
        emptyBossRun.Move(emptyBossRun.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        var loadedBoss = emptyBossRun.CompleteLoading(emptyBossRun.Snapshot.Identity, Guid.NewGuid());
        That(loadedBoss.ClearFact!.ConfirmedDeathEvents.SequenceEqual(new[] { onlyDeath.EventId }) &&
            loadedBoss.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
            loadedBoss.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 13, 14 }),
            "the original all-blocking-clear path preserves only real death events with friendly actors alive");
        var multiBoss = WithTeams(BossPlan(boss, 20, 21, 22, 23), 100, 100, 100, 0);
        var multiSources = multiBoss.Room.Monsters.Select((source, index) => index == 2 ? source with { RankKind = "[normal]" } : source).ToArray();
        multiBoss = DungeonMonsterPlan.FromRoom(multiBoss.Room with { Monsters = Array.AsReadOnly(multiSources) }, 99, 20, playerTeam: 0);
        var terminalMaze = maze with { Starts = [boss.Position], MapSpecification = [boss] };
        var terminalEntry = entry with { Maze = terminalMaze, StartMap = boss };
        That(SinglePlayerDungeonRun.TryCreate(terminalEntry, 5, 500, new([multiBoss], []), authority, out var multiRun, out _),
            "multiple blocking bosses normal hostiles and a friendly boss share the original run");
        multiRun!.CompleteLoading(multiRun.Snapshot.Identity, Guid.NewGuid());
        var firstBossDeath = authority.Death(multiRun.Snapshot.Identity, 20, 1);
        var firstBossResult = multiRun.AcceptConfirmedDeath(firstBossDeath);
        That(firstBossResult.ClearFact is null && multiRun.Phase == SinglePlayerDungeonPhase.Dungeon &&
            multiRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 21, 22, 23 }),
            "one dead boss cannot clear while another blocking boss remains alive");
        var finalBossDeath = authority.Death(multiRun.Snapshot.Identity, 21, 2);
        var multiClear = multiRun.AcceptConfirmedDeath(finalBossDeath);
        That(multiClear.ClearFact!.ConfirmedDeathEvents.SequenceEqual(new[] { firstBossDeath.EventId, finalBossDeath.EventId }) &&
            multiClear.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
            multiClear.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 23 }) && multiClear.CulledMonsterKeys.SequenceEqual(new ushort[] { 22 }),
            "全部敌对Boss死亡后清除剩余阻塞小怪，友方Boss保留且通关凭据不增加虚构击杀");
        That(multiRun.IsCulledMonster(22) && multiRun.CurrentLivingMonster(22) is null &&
            multiRun.AcceptConfirmedDeath(authority.Death(multiRun.Snapshot.Identity,22,3)).Outcome == DungeonRunOutcome.Rejected &&
            multiRun.ClearFact!.ConfirmedDeathEvents.Count == 2,
            "清場小怪不能再次签发击杀、经验或新的通关凭据");
        var native = new NativeDungeonCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(terminalEntry,7,700,new([multiBoss],[]),native,out var nativeRun,out _) &&
            native.BindTo(nativeRun!),"复用多Boss夹具验证原生迟到报告入口");
        nativeRun!.CompleteLoading(nativeRun.Snapshot.Identity,Guid.NewGuid());
        ushort reporter = checked((ushort)nativeRun.Snapshot.Identity.CharacterId);
        NativeDungeonDeathObservation Report(ushort key) => new(key,reporter,0x0111,reporter,0,0,0,0,0,0,0,true,new string('A',64));
        var firstNative = native.ValidateAndIssue(nativeRun,nativeRun.Snapshot.Identity,Report(20)).Death!;
        nativeRun.AcceptConfirmedDeath(firstNative);
        var lastNative = native.ValidateAndIssue(nativeRun,nativeRun.Snapshot.Identity,Report(21)).Death!;
        var preparedNative = nativeRun.PrepareConfirmedDeath(lastNative).Pending!;
        That(!nativeRun.IsCulledMonster(22) && nativeRun.Snapshot.LivingMonsters.Contains((ushort)22) &&
            preparedNative.Result.CulledMonsterKeys.SequenceEqual(new ushort[] { 22 }),"原生Boss死亡提交前仅规划清场");
        nativeRun.ConfirmCommittedDeath(preparedNative);
        var nativeClear = nativeRun.ClearFact;
        for (int repeat = 0; repeat < 2; repeat++)
            That(native.ValidateAndIssue(nativeRun,nativeRun.Snapshot.Identity,Report(22)) is { Outcome: DungeonRunOutcome.Duplicate, Death: null } &&
                ReferenceEquals(nativeRun.ClearFact,nativeClear) && nativeClear!.ConfirmedDeathEvents.Count == 2,
                "Boss后小怪合法迟到报告作为清场重复处理，不新增死亡或通关事实");
        That(native.ValidateAndIssue(nativeRun,nativeRun.Snapshot.Identity,Report(22) with { ChecksumMatches = false }).Outcome == DungeonRunOutcome.Rejected &&
            native.ValidateAndIssue(nativeRun,nativeRun.Snapshot.Identity,Report(22) with { ReporterKey = checked((ushort)(reporter+1)) })
                is { Outcome: DungeonRunOutcome.Duplicate, Death: null },
            "清场迟到报告保留正文检查值，来源仅审计且不影响空死亡应答");
        nativeRun.Return(nativeRun.Snapshot.Identity,Guid.NewGuid());
        That(native.ValidateAndIssue(nativeRun,nativeRun.Snapshot.Identity,Report(22)).Outcome == DungeonRunOutcome.Rejected,
            "退出实例后不能再使用清场重放入口");
        var ai = new DungeonRoomAiCharacter(2004,[2004,810,264,0,0,0],"[monster]","[boss]","aicharacter/fixture.aic",32);
        var apcPlan = DungeonMonsterPlan.FromRoom(new(boss,[],[],[]) { AiCharacters = [ai,ai with { Id = 2010, RawValues = [2010,500,264,0,0,0], Faction = "[character]" }] },99,30);
        That(SinglePlayerDungeonRun.TryCreate(terminalEntry,6,600,new([apcPlan],[]),authority,out var apcRun,out _),
            "纯APC终点保留敌对和友方演员，使用既有正式实例");
        var apcLoaded = apcRun!.CompleteLoading(apcRun.Snapshot.Identity,Guid.NewGuid());
        That(apcLoaded.ClearFact is null && apcRun.Phase == SinglePlayerDungeonPhase.Dungeon && apcRun.CurrentLivingMonster(30) is not null &&
            apcRun.CurrentLivingMonster(31) is null,"敌对APC Boss存活时不能把终点误判为空房清图，友方不具有死亡资格");
        var apcDeath = authority.Death(apcRun.Snapshot.Identity,30,1);
        var apcPending = apcRun.PrepareConfirmedDeath(apcDeath).Pending!;
        That(apcPending.Result.ClearFact is { ConfirmedDeathEvents.Count: 1 } && apcRun.ClearFact is null &&
            apcRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 30,31 }),"APC末次死亡提交前不提前修改演员或清图事实");
        var apcClear = apcRun.ConfirmCommittedDeath(apcPending);
        That(apcClear.ClearFact!.ConfirmedDeathEvents.SequenceEqual(new[] { apcDeath.EventId }) &&
            apcRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 31 }) &&
            apcRun.AcceptConfirmedDeath(apcDeath).Outcome == DungeonRunOutcome.Duplicate,
            "APC真实死亡仅确认一次，友方仍在场，清图事件与普通事务共用同一事实");
        var side = new DungeonMap("map", new(0, 1), 4, "map/fixture-side.map");
        var sideMaze = new DungeonMaze(0, 3, 2, "fixture-uncleared-side", [start.Position], [boss.Position], [start, friendly, boss, side], []);
        var sideRoute = route with
        {
            Rooms = [.. route.Rooms, Plan(side, 15)],
            Connections = [.. route.Connections, new(start.Position, side.Position)]
        };
        That(SinglePlayerDungeonRun.TryCreate(entry with { Maze = sideMaze }, 6, 600, sideRoute, authority, out var sideRun, out _),
            "uncleared branch fixture uses only approved room connections");
        sideRun!.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.AcceptConfirmedDeath(authority.Death(sideRun.Snapshot.Identity, 10, 1));
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        sideRun.CompleteLoading(sideRun.Snapshot.Identity, Guid.NewGuid());
        var branchClear = sideRun.AcceptConfirmedDeath(authority.Death(sideRun.Snapshot.Identity, 13, 2));
        That(branchClear.ClearFact is { ConfirmedDeathEvents.Count: 2 } && sideRun.Phase == SinglePlayerDungeonPhase.Settlement &&
            sideRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 14 }),
            "terminal boss death settles without visiting an optional branch or fabricating its deaths");
        That(sideRun.Move(sideRun.Snapshot.Identity, Guid.NewGuid(), friendly.Position).Outcome == DungeonRunOutcome.Rejected &&
            sideRun.ClearFact == branchClear.ClearFact,
            "settlement cannot resume room traversal or add an optional branch to the committed clear fact");
        RunLoadingCore();
        RunFactionCore();
        Console.WriteLine($"PASS {Count - checks} dungeon actor and confirmed-loading lifecycle assertions");
    }

    static void RunFactionCore()
    {
        var map = new DungeonMap("boss",new(0,0),99,"map/relative-team.map");
        var maze = new DungeonMaze(0,1,1,"fixture",[map.Position],[map.Position],[map],[]);
        var source = new DungeonRoomMonster(1,[1,0,20,100,100,0,1,1],"[fixed]","[normal]","monster/fixture.mob");
        var room = new DungeonRoomDefinition(map,[source,source with { RankKind = "[boss]" },source with { RankKind = "[boss]" }],[],[],[100,7,0]);
        var plan = DungeonMonsterPlan.FromRoom(room,99,10,playerTeam: 7);
        var entry = new DungeonEntryPlan(99,0,3,3145,0,0,maze,map,map) { PlayerTeam = 7 };
        var authority = new FixtureCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,100,new([plan],[]),authority,out var run,out _),
            "明确本人team7及原阵营生成计划可建立实例");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,11,1)).Outcome == DungeonRunOutcome.Rejected &&
            run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 10,11,12 }), "同阵营Boss不能签发战斗死亡或删除场景对象");
        var clear = run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,12,1));
        That(clear.ClearFact is { ConfirmedDeathEvents.Count: 1 } && clear.CulledMonsterKeys.SequenceEqual(new ushort[] { 10 }) &&
            run.Phase == SinglePlayerDungeonPhase.Settlement && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }),
            "异阵营team0 Boss真实死亡后结算，仅清退敌方小怪而保留同阵营Boss");
        var altered = plan with { Monsters = [plan.Monsters[0] with { IsFriendly = true,IsBlocking = false },.. plan.Monsters.Skip(1)] };
        That(!SinglePlayerDungeonRun.TryCreate(entry,2,200,new([altered],[]),authority,out _,out _) &&
            !SinglePlayerDungeonRun.TryCreate(entry,2,200,new([plan with { PlayerTeam = 0 }],[]),authority,out _,out _),
            "入场复验拒绝伪造友军资格或不匹配的本人阵营");
        var unknown = DungeonMonsterPlan.FromRoom(room,99,10);
        That(SinglePlayerDungeonRun.TryCreate(entry with { PlayerTeam = null },2,200,new([unknown],[]),authority,out var unknownRun,out _),
            "未知本人阵营不阻断原生成集合");
        unknownRun!.CompleteLoading(unknownRun.Snapshot.Identity,Guid.NewGuid());
        That(unknownRun.AcceptConfirmedDeath(authority.Death(unknownRun.Snapshot.Identity,12,1)).ClearFact is null,
            "未知本人阵营不因team7被猜为友军而提前通关");
    }

    private static void RunLoadingCore()
    {
        var authority = new FixtureCombatAuthority();
        var start = new DungeonMap("map", new(0, 0), 101, "map/loading-start.map");
        var middle = new DungeonMap("map", new(1, 0), 102, "map/loading-middle.map");
        var boss = new DungeonMap("boss", new(2, 0), 103, "map/loading-boss.map");
        var maze = new DungeonMaze(7, 3, 1, "BBFFEE", [start.Position], [boss.Position], [start, middle, boss], []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, start, boss) { InitializationCounterSupported = true };
        var route = new DungeonFixedRoute([Plan(start, 10), WithTeams(Plan(middle, 11), 0), BossPlan(boss, 12)],
            [new(start.Position, middle.Position), new(middle.Position, boss.Position)]);
        foreach (bool reuse in new[] { false, true })
        {
            That(SinglePlayerDungeonRun.TryCreate(entry with { IndividualMapMovement = reuse }, 1, 100, route, authority, out var created, out _),
                "ordinary loading fixture creates the existing identity-bound run");
            var run = created!;
            var firstIdentity = run.Snapshot.Identity;
            That(run.Snapshot.MapPayload == DungeonMapPayload.Build,
                "每个新实例首次入房独立构建地图，不借用其他实例缓存");
            var firstAction = Guid.NewGuid();
            That(run.ConfirmedInitializationCount == 0 && run.LastLoadingFact is null && run.MazeInitializationThreshold == 2 &&
                run.CompleteLoading(firstIdentity with { InstanceId = Guid.NewGuid() }, firstAction, true).Outcome == DungeonRunOutcome.Rejected &&
                run.CompleteLoading(firstIdentity, Guid.Empty, true).Outcome == DungeonRunOutcome.Rejected && run.LastLoadingFact is null,
                "rejected loads neither initialize cache nor count and maze threshold counts positive cells minus one");
            run.CompleteLoading(firstIdentity, firstAction, true);
            var firstFact = run.LastLoadingFact;
            That(firstFact is { IsInitialLoading: true, DidInitializeMap: true, ConfirmedInitializationCount: 0, MazeSourceOrdinal: 7 } &&
                firstFact.Identity == firstIdentity && firstFact.ActionId == firstAction && firstFact.DungeonId == entry.DungeonId && firstFact.Map == start,
                "first load freezes actual pre-epoch identity and resource map without counting the initial entry");
            That(run.CompleteLoading(firstIdentity, firstAction, true).Outcome == DungeonRunOutcome.Duplicate &&
                run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid(), true).Outcome == DungeonRunOutcome.Rejected &&
                ReferenceEquals(firstFact, run.LastLoadingFact) && run.ConfirmedInitializationCount == 0,
                "replayed or out-of-phase loading cannot replace a confirmed fact or count another initialization");
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 10, 1));
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position);
            run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
            That(run.Snapshot is { MapOperation: DungeonMapOperation.Current, MapPayload: DungeonMapPayload.Build },
                "首次进入相邻房间保留完整地图与演员初始化");
            That(run.LastLoadingFact is { IsInitialLoading: false, DidInitializeMap: true, ConfirmedInitializationCount: 0 } &&
                run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon,
                "a successful load without matching acceptance still installs cache but never completes a quest or dungeon");
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), start.Position);
            var staleIdentity = run.Snapshot.Identity with { Epoch = firstIdentity.Epoch };
            That(run.Snapshot is { MapOperation: DungeonMapOperation.Current, MapPayload: DungeonMapPayload.Cached } &&
                run.Snapshot.LivingMonsters.Count == 0,
                "回访使用原 MAP 缓存并保留死亡集，和任务初始化统计标志独立");
            var beforeRejected = run.LastLoadingFact;
            That(run.CompleteLoading(staleIdentity, Guid.NewGuid(), true).Outcome == DungeonRunOutcome.Rejected &&
                ReferenceEquals(beforeRejected, run.LastLoadingFact) && run.ConfirmedInitializationCount == 0,
                "an old loading epoch cannot advance shared counter or mark an unconfirmed room");
            run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid(), true);
            That(run.LastLoadingFact!.DidInitializeMap == !reuse && run.ConfirmedInitializationCount == (reuse ? 0u : 1u),
                "individual movement reuses the first-room cache while ordinary revisit initializes it again");
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position);
            run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid(), true);
            That(run.Snapshot.MapPayload == DungeonMapPayload.Cached,
                "再次回访已加载中间房间不重建对象");
            That(run.LastLoadingFact!.DidInitializeMap == !reuse && run.ConfirmedInitializationCount == (reuse ? 0u : 2u),
                "cache created before a matching quest is accepted stays reusable without retroactive progress");
            run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position);
            run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid(), true);
            uint count = reuse ? 1u : 3u;
            That(run.LastLoadingFact is { DidInitializeMap: true, IsInitialLoading: false } && run.LastLoadingFact.Map == boss &&
                run.ConfirmedInitializationCount == count && run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon,
                "a new boss room adds one matching initialization and reaching threshold does not fabricate clear");
            var clear = run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 12, 2));
            That(clear.ClearFact is { ConfirmedDeathEvents.Count: 2 } && run.ConfirmedInitializationCount == count &&
                run.LastLoadingFact!.ConfirmedInitializationCount == count && run.Phase == SinglePlayerDungeonPhase.Settlement,
                "real boss death retains shared initialization count for the existing clear transaction");
        }
        var singleMaze = maze with { Width = 1, Greed = "BB", Starts = [boss.Position with { X = 0 }], Bosses = [boss.Position with { X = 0 }],
            MapSpecification = [boss with { Position = new(0, 0) }] };
        var singleBoss = singleMaze.MapSpecification![0];
        var singleEntry = entry with { Maze = singleMaze, StartMap = singleBoss, BossMap = singleBoss, BaseBossMap = singleBoss };
        That(SinglePlayerDungeonRun.TryCreate(singleEntry, 2, 200, new([BossPlan(singleBoss, 20)], []), authority, out var single, out _) &&
            single!.InitializationCounterSupported && single.MazeInitializationThreshold is null && single.ConfirmedInitializationCount == 0,
            "a new run resets the counter while the unconfirmed single-room minus-one fallback stays unavailable");
        That(SinglePlayerDungeonRun.TryCreate(entry with { InitializationCounterSupported = false }, 3, 300, route, authority, out var unsupported, out _),
            "unconfirmed counter shapes retain the existing ordinary run admission");
        unsupported!.CompleteLoading(unsupported.Snapshot.Identity, Guid.NewGuid(), true);
        That(unsupported.LastLoadingFact is { InitializationCounterSupported: false, DidInitializeMap: false } &&
            unsupported.MazeInitializationThreshold is null && unsupported.ConfirmedInitializationCount == 0,
            "unconfirmed resources report the explicit loading limitation and no replacement counter");
        That(SinglePlayerDungeonRun.TryCreate(entry, 4, 400, route, authority, out var counting, out _),
            "positive threshold fixture uses an independent run");
        counting!.CompleteLoading(counting.Snapshot.Identity, Guid.NewGuid(), true);
        counting.AcceptConfirmedDeath(authority.Death(counting.Snapshot.Identity, 10, 1));
        for (uint expected = 1; expected <= 8; expected++)
        {
            var target = counting.Snapshot.CurrentMap == start ? middle.Position : start.Position;
            counting.Move(counting.Snapshot.Identity, Guid.NewGuid(), target);
            counting.CompleteLoading(counting.Snapshot.Identity, Guid.NewGuid(), true);
            That(counting.ConfirmedInitializationCount == expected && counting.ClearFact is null &&
                counting.MazeInitializationThreshold == 2,
                "positive eight counts actual repeated initializations independently of the maze minus-one threshold");
        }
        counting.BindQuestAcceptance(new(1, 3101, 0) { AcceptanceId = Guid.NewGuid() });
        That(counting.ConfirmedInitializationCount == 8 && counting.Entry.QuestAcceptances.Single().ConditionState == 0,
            "binding another active acceptance even with completed scalar does not reset the shared native counter");
        var renewed = new DfoServer.Game.Characters.ActiveMainQuest(0, 3145, 1) { AcceptanceId = Guid.NewGuid() };
        That(SinglePlayerDungeonRun.TryCreate(entry with { QuestAcceptanceId = renewed.AcceptanceId, QuestAcceptances = [renewed] },
            5, 500, route, authority, out var newRun, out _) && newRun!.ConfirmedInitializationCount == 0 &&
            newRun.LastLoadingFact is null &&
            newRun.CompleteLoading(counting.Snapshot.Identity, Guid.NewGuid(), true).Outcome == DungeonRunOutcome.Rejected &&
            newRun.ConfirmedInitializationCount == 0 && newRun.Entry.QuestAcceptances.Single().AcceptanceId == renewed.AcceptanceId,
            "old run loading identity cannot advance the same quest's new acceptance and new run starts its own counter");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var authority = new FixtureCombatAuthority();
        var a = new DungeonMap("map", new(0, 0), 1, "map/fixture-a.map");
        var b = new DungeonMap("map", new(1, 0), 2, "map/fixture-b.map");
        var c = new DungeonMap("boss", new(1, 1), 3, "map/fixture-c.map");
        var d = new DungeonMap("map", new(0, 1), 4, "map/fixture-d.map");
        var maps = Array.AsReadOnly(new[] { a, b, c, d });
        var maze = new DungeonMaze(0, 2, 2, "fixture-only", [a.Position], [c.Position], maps, []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, a, c);
        var route = new DungeonFixedRoute([Plan(a, 10, 11), Plan(b, 12), Plan(c, 13), Plan(d, 14)],
            [new(a.Position, b.Position), new(b.Position, c.Position), new(a.Position, d.Position)]);
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route, authority, out var created, out _), "explicit fixture creates identity-bound Loading run");
        var run = created!;
        DungeonRunIdentity first = run.Snapshot.Identity;
        That(run.Phase == SinglePlayerDungeonPhase.Loading && first.CharacterId == 99 && first.QuestId == 3145 && first.TaskAttempt == 1 &&
            first.InstanceId != Guid.Empty && first.Epoch == 23 && run.StartCharacterRevision == 17, "instance identity and opening revision snapshot are separate");
        foreach (DungeonRunIdentity wrong in new[]
        {
            first with { CharacterId = 98 }, first with { QuestId = 3146 }, first with { TaskAttempt = 2 },
            first with { InstanceId = Guid.NewGuid() }, first with { Epoch = 22 }
        })
            That(run.CompleteLoading(wrong, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected, "wrong character quest attempt instance or epoch rejected");
        That(run.AcceptConfirmedDeath(authority.Death(first, 10, 1)).Outcome == DungeonRunOutcome.Rejected &&
            run.Move(first, Guid.NewGuid(), b.Position).Outcome == DungeonRunOutcome.Rejected, "Loading accepts neither confirmed death nor room transition");
        Guid loaded = Guid.NewGuid();
        That(run.CompleteLoading(first, loaded).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Dungeon &&
            run.Snapshot.Identity.Epoch == 24, "load completion advances exact epoch");
        That(run.CompleteLoading(first, loaded).Outcome == DungeonRunOutcome.Duplicate && run.Snapshot.Identity.Epoch == 24,
            "accepted load replay does not advance again");
        That(run.CompleteLoading(first, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected, "unknown stale load action is rejected");
        DungeonRunIdentity inA = run.Snapshot.Identity;
        That(run.Move(inA, Guid.NewGuid(), b.Position).Outcome == DungeonRunOutcome.Rejected, "uncleared room blocks movement");
        That(run.AcceptConfirmedDeath(new FixtureCombatAuthority().Death(inA, 10, 1)).Outcome == DungeonRunOutcome.Rejected,
            "unbound combat issuer cannot assert death");
        That(run.AcceptConfirmedDeath(authority.Death(inA, 999, 1)).Outcome == DungeonRunOutcome.Rejected, "unknown object identity rejected");
        var death10 = authority.Death(inA, 10, 1);
        That(run.AcceptConfirmedDeath(death10).Outcome == DungeonRunOutcome.Accepted && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }),
            "formal typed death removes only its declared monster");
        That(run.AcceptConfirmedDeath(death10).Outcome == DungeonRunOutcome.Duplicate && run.Snapshot.LivingMonsters.Count == 1,
            "duplicate confirmed event has no repeated effect");
        That(run.AcceptConfirmedDeath(authority.Death(inA, 11, 2, death10.EventId)).Outcome == DungeonRunOutcome.Rejected,
            "same event identity with altered payload is rejected");
        That(run.AcceptConfirmedDeath(authority.Death(inA, 11, 1)).Outcome == DungeonRunOutcome.Rejected, "combat event sequence cannot be reused");
        That(run.AcceptConfirmedDeath(authority.Death(inA, 10, 2)).Outcome == DungeonRunOutcome.Rejected,
            "another event cannot kill already dead object again");
        That(run.AcceptConfirmedDeath(authority.Death(inA, 11, 2)).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Dungeon,
            "first room clear is not whole-instance settlement");
        That(run.Move(inA, Guid.NewGuid(), new(9, 9)).Outcome == DungeonRunOutcome.Rejected, "missing target room and jump cannot pass");
        Guid moveD = Guid.NewGuid();
        That(run.Move(inA, moveD, d.Position).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Loading,
            "explicit cleared-room edge enters next loading phase");
        That(run.Move(inA, moveD, d.Position).Outcome == DungeonRunOutcome.Duplicate, "accepted transition replay does not repeat loading");
        That(run.Move(inA, moveD, b.Position).Outcome == DungeonRunOutcome.Rejected, "replayed action cannot change its target");
        That(run.AcceptConfirmedDeath(death10).Outcome == DungeonRunOutcome.Duplicate, "old accepted death replay is harmless in another room");
        That(run.AcceptConfirmedDeath(authority.Death(inA, 14, 3)).Outcome == DungeonRunOutcome.Rejected, "old room epoch cannot kill new-room monster");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 14, 3));
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), c.Position).Outcome == DungeonRunOutcome.Rejected,
            "adjacent map across absent approved connection is a wall not a passage");
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), a.Position).Outcome == DungeonRunOutcome.Accepted, "cleared side room can revisit approved edge");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Snapshot.LivingMonsters.Count == 0, "revisited room retains confirmed deaths and does not respawn");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), b.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 12, 4)).Outcome == DungeonRunOutcome.Accepted,
            "new-room confirmed event accepted under new identity epoch");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), c.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var bossDeath = authority.Death(run.Snapshot.Identity, 13, 5);
        DungeonRunResult settled = run.AcceptConfirmedDeath(bossDeath);
        That(settled.Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Settlement &&
            settled.ClearFact!.ConfirmedDeathEvents.Count == 5 && settled.ClearFact!.Identity == run.Snapshot.Identity,
            "all route monsters confirmed dead produces single settlement fact");
        That(run.AcceptConfirmedDeath(bossDeath).Outcome == DungeonRunOutcome.Duplicate && run.AcceptConfirmedDeath(bossDeath).ClearFact is null,
            "settlement replay cannot reproduce clear fact or trigger reward");
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), b.Position).Outcome == DungeonRunOutcome.Rejected, "settlement cannot continue room transitions");
        DungeonRunIdentity terminal = run.Snapshot.Identity;
        Guid exit = Guid.NewGuid();
        That(run.Return(terminal, exit).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Returned,
            "exact settlement exit terminates instance and advances epoch");
        That(run.Return(terminal, exit).Outcome == DungeonRunOutcome.Duplicate && run.Return(terminal, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected,
            "exit replay is idempotent but another old exit is rejected");
        That(SinglePlayerDungeonRun.TryCreate(entry, 2, 100, route, authority, out var retry, out _) && retry!.Snapshot.Identity.InstanceId != first.InstanceId,
            "new challenge receives independent attempt instance and epoch");
        retry!.CompleteLoading(retry!.Snapshot.Identity, Guid.NewGuid());
        That(retry!.AcceptConfirmedDeath(death10).Outcome == DungeonRunOutcome.Rejected && retry!.Snapshot.LivingMonsters.Count == 2,
            "prior challenge confirmed event cannot affect newly spawned objects with same keys");
        That(SinglePlayerDungeonRun.TryCreate(entry, 3, 200, route, authority, out var cancelled, out _), "another attempt can start for cancellation");
        var cancelIdentity = cancelled!.Snapshot.Identity;
        That(cancelled!.Return(cancelIdentity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            cancelled!.CompleteLoading(cancelIdentity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected,
            "cancel during Loading permanently rejects delayed completion");
        That(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, null, authority, out _, out _) &&
            !SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route, null, out _, out _), "missing formal route or combat owner is Unsupported");
        That(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Connections = [new(a.Position, c.Position)] }, authority, out _, out _),
            "diagonal connection is rejected before instance publication");
        That(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Connections = [new(a.Position, b.Position)] }, authority, out _, out _),
            "disconnected route cannot become implicitly reachable");
        var unsupportedRoom = route.Rooms[0].Room with { Monsters = [route.Rooms[0].Room.Monsters[0] with { RankKind = "[unknown]" }] };
        That(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Rooms = [new(unsupportedRoom, []), .. route.Rooms.Skip(1)] }, authority, out _, out _),
            "unknown monster mechanism is not replaced by an empty cleared room");
        That(!SinglePlayerDungeonRun.TryCreate(entry, 0, 23, route, authority, out _, out _) &&
            !SinglePlayerDungeonRun.TryCreate(entry, 1, long.MaxValue, route, authority, out _, out _),
            "invalid attempt and exhausted epoch never publish an unusable instance");
        That(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Rooms = [route.Rooms[0], Plan(b, 10), .. route.Rooms.Skip(2)] }, authority, out _, out _),
            "all rooms share one unique object key namespace");
        That(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Rooms = [Plan(a, 99, 11), .. route.Rooms.Skip(1)] }, authority, out _, out _),
            "monster key cannot alias owning character identity");
        foreach (var changed in new[]
        {
            route.Rooms[0].Monsters[0] with { CandidateLevel = 1 },
            route.Rooms[0].Monsters[0] with { SpawnType = 3 }
        })
        {
            var changedRoom = route.Rooms[0] with { Monsters = [changed, .. route.Rooms[0].Monsters.Skip(1)] };
            That(!SinglePlayerDungeonRun.TryCreate(entry, 1, 23, route with { Rooms = [changedRoom, .. route.Rooms.Skip(1)] }, authority, out _, out _),
                "实例不接受与完整资源规划不一致的等级或生成类型");
        }
        That(retry!.AcceptConfirmedDeath(authority.Death(retry!.Snapshot.Identity, 10, 1, Guid.Empty)).Outcome == DungeonRunOutcome.Rejected,
            "empty formal event identity cannot become an accepted fact");
        That(retry!.Snapshot.LivingMonsters is IList<ushort> readonlyKeys && readonlyKeys.IsReadOnly,
            "published room roster cannot mutate instance ownership");
        var actualMaze = DungeonCatalog.Load(pvf).Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3 && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var actualMaps = actualMaze.MapSpecification!;
        var actualEntry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, actualMaze,
            actualMaps.Single(map => map.Position == actualMaze.Starts.Single()), actualMaps.Single(map => map.Position == actualMaze.Bosses.Single()));
        That(!SinglePlayerDungeonRun.TryCreate(actualEntry, 1, 23, null, authority, out _, out _),
            "actual PVF maze availability does not prove wall door or formal entry support");
        // 只验证真实地图字段能接入领域底座：怪物与战斗来源仍明确为合成fixture。
        var actualTopology = actualMaze.StaticTopology;
        var mapContractRoute = new DungeonFixedRoute(
            actualMaps.Select((map, index) => Plan(map, checked((ushort)(100 + index)))).ToArray(),
            actualTopology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray());
        That(SinglePlayerDungeonRun.TryCreate(actualEntry, 1, 23, mapContractRoute, authority, out var actualRun, out _) &&
            actualRun!.Snapshot.CurrentMap == actualEntry.StartMap, "真实目录map/boss种类与显式地图身份可接入合成状态机测试");
        var wrongMap = a with { Kind = "normal" };
        var wrongMaze = maze with { MapSpecification = [wrongMap, b, c, d] };
        That(!SinglePlayerDungeonRun.TryCreate(entry with { Maze = wrongMaze, StartMap = wrongMap }, 1, 23,
            route with { Rooms = [Plan(wrongMap, 10, 11), .. route.Rooms.Skip(1)] }, authority, out _, out _),
            "未知normal地图种类不能因fixture曾使用而进入实例");
        // 合成已确认事件验证提交协调，不代替生产战斗来源或真实奖励事务。
        var transactionMaze = new DungeonMaze(0, 2, 2, "fixture-commit", [c.Position], [c.Position], [c], []);
        var transactionEntry = entry with { Maze = transactionMaze, StartMap = c, BossMap = c };
        var transactionRoute = new DungeonFixedRoute([Plan(c, 20, 21)], []);
        That(SinglePlayerDungeonRun.TryCreate(transactionEntry, 4, 300, transactionRoute, authority, out var coordinated, out _),
            "提交协调fixture建立独立实例");
        var committingRun = coordinated!;
        committingRun.CompleteLoading(committingRun.Snapshot.Identity, Guid.NewGuid());
        var originalSnapshot = committingRun.Snapshot;
        var firstDeath = authority.Death(originalSnapshot.Identity, 20, 1);
        var preparation = committingRun.PrepareConfirmedDeath(firstDeath);
        var pending = preparation.Pending!;
        That(preparation.Outcome == DungeonRunOutcome.Accepted && pending.Result.ClearFact is null &&
            pending.Result.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 21 }) &&
            committingRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 20, 21 }) &&
            committingRun.Snapshot.Identity == originalSnapshot.Identity && committingRun.Phase == SinglePlayerDungeonPhase.Dungeon,
            "准备死亡只冻结候选，不消费死亡事件或改变实例");
        That(ReferenceEquals(committingRun.PrepareConfirmedDeath(firstDeath).Pending, pending) &&
            ReferenceEquals(committingRun.PrepareConfirmedDeath(authority.Death(originalSnapshot.Identity, 20, 1, firstDeath.EventId)).Pending, pending),
            "待提交相同事实重复准备保留同一候选与稳定事件ID");
        That(committingRun.PrepareConfirmedDeath(authority.Death(originalSnapshot.Identity, 21, 2, firstDeath.EventId)).Outcome == DungeonRunOutcome.Rejected &&
            committingRun.PrepareConfirmedDeath(new FixtureCombatAuthority().Death(originalSnapshot.Identity, 20, 1, firstDeath.EventId)).Outcome == DungeonRunOutcome.Rejected,
            "同ID篡改载荷或更换签发来源不能替换待提交事实");
        That(committingRun.PrepareConfirmedDeath(authority.Death(originalSnapshot.Identity, 21, 2)).Outcome == DungeonRunOutcome.Rejected &&
            committingRun.Return(originalSnapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected &&
            committingRun.Move(originalSnapshot.Identity, Guid.NewGuid(), b.Position).Outcome == DungeonRunOutcome.Rejected &&
            committingRun.AcceptConfirmedDeath(firstDeath).Outcome == DungeonRunOutcome.Rejected,
            "待提交期间新死亡、退出、过房与旧入口都不能抢先改变状态");
        var forged = new PreparedDungeonDeath(firstDeath, pending.Monster, pending.Result);
        That(committingRun.ConfirmCommittedDeath(forged).Outcome == DungeonRunOutcome.Rejected &&
            !committingRun.CancelUncommittedDeath(forged), "重新构造的候选不能确认或撤销原准备");
        That(committingRun.CancelUncommittedDeath(pending) && !committingRun.CancelUncommittedDeath(pending) &&
            committingRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 20, 21 }),
            "明确未提交时撤销准备，死亡集和epoch保持原样");
        var retryPending = committingRun.PrepareConfirmedDeath(firstDeath).Pending!;
        That(!ReferenceEquals(retryPending, pending) && retryPending.Death.EventId == firstDeath.EventId &&
            committingRun.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Rejected &&
            !committingRun.CancelUncommittedDeath(pending), "回滚后使用原事件重试，旧候选不能消费或释放新准备");
        That(committingRun.ConfirmCommittedDeath(retryPending).Outcome == DungeonRunOutcome.Accepted &&
            committingRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 21 }) &&
            committingRun.ConfirmCommittedDeath(retryPending).Outcome == DungeonRunOutcome.Duplicate &&
            committingRun.ConfirmCommittedDeath(retryPending).ClearFact is null &&
            committingRun.PrepareConfirmedDeath(firstDeath).Outcome == DungeonRunOutcome.Duplicate,
            "提交确认只消费一次；重复确认不重新输出奖励或清图");
        var lastDeath = authority.Death(committingRun.Snapshot.Identity, 21, 2);
        var lastPending = committingRun.PrepareConfirmedDeath(lastDeath).Pending!;
        That(committingRun.AcceptConfirmedDeath(firstDeath).Outcome == DungeonRunOutcome.Duplicate &&
            committingRun.IsPendingDeath(lastPending), "末次候选待提交时，先前已确认死亡仍按重复处理");
        That(lastPending.Result.ClearFact is { ConfirmedDeathEvents.Count: 2 } &&
            lastPending.Result.ClearFact.Identity == lastPending.Result.Snapshot.Identity &&
            lastPending.Result.ClearFact.ConfirmedDeathEvents.Contains(firstDeath.EventId) &&
            lastPending.Result.ClearFact.ConfirmedDeathEvents.Contains(lastDeath.EventId) &&
            lastPending.Result.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
            lastPending.Result.Snapshot.Identity.Epoch == originalSnapshot.Identity.Epoch + 1 &&
            committingRun.Phase == SinglePlayerDungeonPhase.Dungeon && committingRun.Snapshot.LivingMonsters.Count == 1,
            "末次死亡在提交前提供冻结清图候选，实际实例尚未结算");
        That(committingRun.CancelUncommittedDeath(lastPending) && committingRun.Phase == SinglePlayerDungeonPhase.Dungeon &&
            committingRun.Snapshot.Identity == originalSnapshot.Identity,
            "末次死亡回滚不丢失清图资格或提前推进epoch");
        var lastRetry = committingRun.PrepareConfirmedDeath(lastDeath).Pending!;
        That(lastRetry.Result.ClearFact!.ConfirmedDeathEvents.SequenceEqual(lastPending.Result.ClearFact!.ConfirmedDeathEvents) &&
            lastRetry.Result.Snapshot.Identity == lastPending.Result.Snapshot.Identity,
            "回滚后的同事实重试保持清图身份和全部死亡事件ID");
        That(SinglePlayerDungeonRun.TryCreate(transactionEntry, 5, 400, transactionRoute, authority, out var otherRun, out _),
            "另一个实例用于候选隔离验证");
        otherRun!.CompleteLoading(otherRun.Snapshot.Identity, Guid.NewGuid());
        That(otherRun.ConfirmCommittedDeath(lastRetry).Outcome == DungeonRunOutcome.Rejected &&
            !otherRun.CancelUncommittedDeath(lastRetry) && otherRun.Snapshot.LivingMonsters.Count == 2,
            "另一个实例不能消费或撤销原实例待提交候选");
        var committed = committingRun.ConfirmCommittedDeath(lastRetry);
        That(ReferenceEquals(committed, lastRetry.Result) && committingRun.Phase == SinglePlayerDungeonPhase.Settlement &&
            committingRun.Snapshot.Identity == lastRetry.Result.Snapshot.Identity && committingRun.Snapshot.LivingMonsters.Count == 0 &&
            committingRun.ConfirmCommittedDeath(lastRetry).ClearFact is null && !committingRun.CancelUncommittedDeath(lastRetry),
            "提交后发布准备时的同一清图结果，不能撤销或重复结算");
        That(pending.Result.Snapshot.LivingMonsters is IList<ushort> frozenLiving && frozenLiving.IsReadOnly &&
            lastRetry.Result.ClearFact!.ConfirmedDeathEvents is IList<Guid> frozenEvents && frozenEvents.IsReadOnly,
            "待提交名单与清图事件集保持只读快照");
        That(committingRun.Return(committingRun.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
            "确认完成后解除待提交门禁，正常退出仍可执行");
        Console.WriteLine($"PASS {Count - checks} single-player lifecycle identity and confirmed-event assertions");
    }

    private static void VerifyPassiveClearCondition()
    {
        var start = new DungeonMap("map", new(0, 0), 1, "map/passive-start.map");
        var terminal = new DungeonMap("boss", new(1, 0), 2, "map/passive-terminal.map");
        var passive = new DungeonRoomPassiveObject(80458, [80458, 10, 10, 0], "passiveobject/fixture.obj");
        var maze = new DungeonMaze(0, 2, 1, "passive-clear", [start.Position], [terminal.Position], [start, terminal], [])
            { ClearConditions = [new("destroy object", 80458, 1)] };
        var entry = new DungeonEntryPlan(99, 0, 25, 3205, 0, 0, maze, start, terminal);
        var end = WithTeams(Plan(terminal, 11), 0);
        end = end with { Room = end.Room with { PassiveObjects = [passive] } };
        var route = new DungeonFixedRoute([Plan(start, 10), end], [new(start.Position, terminal.Position)]) { PassiveClearTarget = 80458 };
        var authority = new FixtureCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var created, out _), "物件终点保持真实怪物与友方演员计划");
        var run = created!;
        var loading = run.Snapshot.Identity;
        That(run.ConfirmCommittedPassiveObject(loading, Guid.NewGuid(), 80458, 0).Outcome == DungeonRunOutcome.Rejected,
            "加载前的物件确认不能取得结算资格");
        run.CompleteLoading(loading, Guid.NewGuid());
        That(run.ConfirmCommittedPassiveObject(run.Snapshot.Identity, Guid.NewGuid(), 80458, 0).Outcome == DungeonRunOutcome.Rejected,
            "另一房间不能消费终点物件");
        var death = authority.Death(run.Snapshot.Identity, 10, 1);
        run.AcceptConfirmedDeath(death);
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), terminal.Position);
        var loaded = run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var before = run.Snapshot.Identity;
        That(loaded.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }),
            "终点加载保留友方演员，声明的物件未提交时不能提前结算");
        var action = Guid.NewGuid();
        That(run.ConfirmCommittedPassiveObject(before with { InstanceId = Guid.NewGuid() }, action, 80458, 0).Outcome == DungeonRunOutcome.Rejected &&
            run.ConfirmCommittedPassiveObject(before, action, 80457, 0).Outcome == DungeonRunOutcome.Rejected &&
            run.ConfirmCommittedPassiveObject(before, action, 80458, 1).Outcome == DungeonRunOutcome.Rejected &&
            run.ConfirmCommittedPassiveObject(loading, action, 80458, 0).Outcome == DungeonRunOutcome.Rejected && run.Snapshot.Identity == before,
            "旧实例、旧epoch、错误模板和行号均不推进场景");
        var prepared = run.PreparePassiveClear(before, action, 80458, 0)!;
        That(prepared is not null && run.IsPendingPassiveClear(prepared) && run.ClearFact is null && run.Snapshot.Identity == before &&
            run.Phase == SinglePlayerDungeonPhase.Dungeon && run.PreparePassiveClear(before, action, 80458, 0) == prepared &&
            run.PreparePassiveClear(before, Guid.NewGuid(), 80458, 0) is null,
            "物件准备冻结同一原操作，提交前不改变清图或epoch，不能另起终末");
        That(run.Return(before, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected &&
            run.Move(before, Guid.NewGuid(), start.Position).Outcome == DungeonRunOutcome.Rejected,
            "原物件准备等待提交时不能离开实例或改写场景");
        var clear = run.ConfirmCommittedPassiveClear(prepared!);
        That(clear.ClearFact is { ConfirmedDeathEvents.Count: 1, ClearMap: var map } && map == terminal &&
            clear.ClearFact.ConfirmedDeathEvents.Single() == death.EventId && !clear.ClearFact.ConfirmedDeathEvents.Contains(action) &&
            run.Snapshot.Identity.Epoch == before.Epoch + 1 && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }),
            "提交后形成一次实际终点清图，物件不伪造战斗死亡或清退友方");
        That(clear.ClearFact!.PassiveSource is { OperationId: var operation } && operation == action &&
            run.ConfirmCommittedPassiveClear(prepared!).Outcome == DungeonRunOutcome.Duplicate,
            "物件清图保留独立来源且同一准备确认不会再推进epoch");
        That(run.ConfirmCommittedPassiveObject(before, action, 80458, 0).Outcome == DungeonRunOutcome.Duplicate &&
            run.ConfirmCommittedPassiveObject(before, action, 80457, 0).Outcome == DungeonRunOutcome.Rejected &&
            run.ConfirmCommittedPassiveObject(before, Guid.NewGuid(), 80458, 0).Outcome == DungeonRunOutcome.Rejected &&
            run.ClearFact == clear.ClearFact && run.Snapshot.Identity == clear.Snapshot.Identity,
            "原操作精确重放不重复清图，同ID改内容或新ID旧身份仍拒绝");
        var ambiguous = route with { Rooms = [route.Rooms[0], end with { Room = end.Room with { PassiveObjects = [passive, passive] } }] };
        That(SinglePlayerDungeonRun.TryCreate(entry, 2, 200, ambiguous, authority, out var repeated, out _), "保留同模板多行资源而不反猜实例身份");
        repeated!.CompleteLoading(repeated.Snapshot.Identity, Guid.NewGuid());
        repeated.AcceptConfirmedDeath(authority.Death(repeated.Snapshot.Identity, 10, 1));
        repeated.Move(repeated.Snapshot.Identity, Guid.NewGuid(), terminal.Position);
        repeated.CompleteLoading(repeated.Snapshot.Identity, Guid.NewGuid());
        That(repeated.ConfirmCommittedPassiveObject(repeated.Snapshot.Identity, Guid.NewGuid(), 80458, 0).Outcome == DungeonRunOutcome.Rejected &&
            repeated.ClearFact is null, "重复模板行不能靠数量或模板任意认领一个物件并结算");
        var multiple = entry with { Maze = maze with { ClearConditions = [new("destroy object", 80458, 2)] } };
        That(!SinglePlayerDungeonRun.TryCreate(multiple, 3, 300, route, authority, out _, out _),
            "未确认的多实例数量声明不能借单物件批准字段接入");
        var emptyStart = route.Rooms[0] with { Monsters = [], Room = route.Rooms[0].Room with { Monsters = [] } };
        var zeroRoute = route with { Rooms = [emptyStart,end] };
        That(SinglePlayerDungeonRun.TryCreate(entry,4,400,zeroRoute,authority,out var zero,out _),
            "零击杀边界保留空首图与友方终点原资源，不制造敌人");
        zero!.CompleteLoading(zero.Snapshot.Identity,Guid.NewGuid());
        zero.Move(zero.Snapshot.Identity,Guid.NewGuid(),terminal.Position);
        zero.CompleteLoading(zero.Snapshot.Identity,Guid.NewGuid());
        That(zero.PreparePassiveClear(zero.Snapshot.Identity,Guid.NewGuid(),80458,0) is null &&
            zero.ClearFact is null && zero.Phase==SinglePlayerDungeonPhase.Dungeon,
            "尚未闭合的零击杀结算保持拒绝，不能用kill1或空死亡绕过翻牌合同");
    }

    private static void VerifyLayeredBossTerminal()
    {
        var start = new DungeonMap("map", new(0, 0), 10, "map/fixture-layer-start.map");
        var boss = new DungeonMap("boss", new(1, 0), 20, "map/fixture-dummy-boss.map");
        var middle = new DungeonMap("layered", boss.Position, 30, "map/fixture-middle-boss.map");
        var last = new DungeonMap("layered", boss.Position, 40, "map/fixture-real-boss.map");
        var maze = new DungeonMaze(0, 2, 1, "fixture-layers", [start.Position], [boss.Position], [start, boss], [])
            { LayeredMaps = [middle, last] };
        var entry = new DungeonEntryPlan(99, 0, 3, 3145, 0, 0, maze, start, boss);
        var dummy = Change(BossPlan(boss, 20), true);
        var route = new DungeonFixedRoute([WithTeams(Plan(start, 10), 0), dummy, BossPlan(middle, 30), BossPlan(last, 40)],
            [new(start.Position, boss.Position)]);
        var authority = new FixtureCombatAuthority();
        var run = Create(entry, route);
        That(run.Entry.BossMap == last && run.Entry.BaseBossMap == boss && entry.BossMap == boss,
            "Run冻结实际末层终点供结算及任务事务共享，输入计划和基础房身份保持原值");
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20, 1)).ClearFact is null &&
            run.Phase == SinglePlayerDungeonPhase.Dungeon, "基础dummy死亡不能绕过真实末层提前结算");
        Layer(run);
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 30, 2)).ClearFact is null,
            "同模板真实Boss在非末层死亡仍不能结算");
        Layer(run);
        var before = run.Snapshot.Identity;
        var death = authority.Death(before, 40, 3);
        var pending = run.PrepareConfirmedDeath(death).Pending!;
        That(pending.Result.ClearFact is { ClearMap: var map, ConfirmedDeathEvents.Count: 3 } && map == last &&
            run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon,
            "真实末层只规划原清图事实，提交前不改变状态");
        That(run.CancelUncommittedDeath(pending) && run.ClearFact is null && run.CurrentLivingMonster(40) is not null,
            "末层死亡回滚保留Boss存活和原状态");
        pending = run.PrepareConfirmedDeath(death).Pending!;
        var committed = run.ConfirmCommittedDeath(pending);
        That(ReferenceEquals(committed.ClearFact, run.ClearFact) && run.ClearFact!.ClearMap == last &&
            run.Phase == SinglePlayerDungeonPhase.Settlement && run.Snapshot.Identity.Epoch == before.Epoch + 1 &&
            run.AcceptConfirmedDeath(death).Outcome == DungeonRunOutcome.Duplicate &&
            run.MoveLayer(before, Guid.NewGuid(), boss.Position, true).Outcome == DungeonRunOutcome.Rejected,
            "末层提交后发布唯一清图事实，重复死亡和旧身份不能恢复基础房或再次结算");
        foreach (int variant in new[] { 0, 1, 2, 3 })
        {
            var changedEntry = variant == 0 ? entry with { HasExplicitClearMap = true } : entry;
            var changedRoute = route with { Rooms = [route.Rooms[0], variant == 1 ? BossPlan(boss, 20) : dummy,
                route.Rooms[2], variant == 2 ? Change(BossPlan(last, 40), false, 2) :
                    variant == 3 ? Change(BossPlan(last, 40), true) : route.Rooms[3]] };
            var unchanged = Create(changedEntry, changedRoute);
            Layer(unchanged);
            unchanged.AcceptConfirmedDeath(authority.Death(unchanged.Snapshot.Identity, 30, 1));
            Layer(unchanged);
            That(unchanged.AcceptConfirmedDeath(authority.Death(unchanged.Snapshot.Identity, 40, 2)).ClearFact is null &&
                unchanged.Phase == SinglePlayerDungeonPhase.Dungeon,
                "显式基础终点、真实基础Boss、异模板或末层dummy不被自动晋升 " + variant);
        }
        var explicitLayer = Create(entry with { BossMap = middle, HasExplicitClearMap = true }, route);
        Layer(explicitLayer);
        That(explicitLayer.AcceptConfirmedDeath(authority.Death(explicitLayer.Snapshot.Identity, 30, 1)).ClearFact?.ClearMap == middle,
            "任务明确声明的非末层清图地图仍按原终点结算");
        var sameIdLayer = middle with { Id = boss.Id };
        var sameIdEntry = entry with { Maze = maze with { LayeredMaps = [sameIdLayer,last] },
            BossMap = sameIdLayer,HasExplicitClearMap = true,QuestClearMap = sameIdLayer };
        var sameIdRoute = route with { Rooms = [route.Rooms[0],dummy,BossPlan(sameIdLayer,30),route.Rooms[3]] };
        var sameIdRun = Create(sameIdEntry,sameIdRoute);
        Layer(sameIdRun);
        That(sameIdRun.AcceptConfirmedDeath(authority.Death(sameIdRun.Snapshot.Identity,30,1)).ClearFact?.ClearMap == sameIdLayer,
            "已有基础图与叠层同ID的明确终点保留旧合同，中途目标唯一性不扩大终点准入限制");
        var roomTargetRun = Create(entry with { HasExplicitClearMap = true,QuestClearMap = middle },route);
        That(roomTargetRun.Entry.BossMap == last && roomTargetRun.Entry.QuestClearMap == middle,
            "明确中途目标不阻止原dummy基础Boss解析为末层真实Boss");
        That(roomTargetRun.AcceptConfirmedDeath(authority.Death(roomTargetRun.Snapshot.Identity,20,1)).ClearFact is null,
            "中途目标路线的基础dummy死亡不提前结算");
        Layer(roomTargetRun);
        var roomTargetClear = roomTargetRun.AcceptConfirmedDeath(authority.Death(roomTargetRun.Snapshot.Identity,30,2));
        That(roomTargetClear.RoomClearFact is { Map: var targetMap } && targetMap == middle && roomTargetClear.ClearFact is null &&
            roomTargetRun.Phase == SinglePlayerDungeonPhase.Dungeon,"中间叠层目标只产生独立清房，不抢占原末层结算");
        Layer(roomTargetRun);
        That(roomTargetRun.AcceptConfirmedDeath(authority.Death(roomTargetRun.Snapshot.Identity,40,3)).ClearFact?.ClearMap == last &&
            roomTargetRun.Phase == SinglePlayerDungeonPhase.Settlement,"中途目标后仍由原末层真实Boss完成通关");
        var livingDummy = Create(entry, route);
        Layer(livingDummy);
        livingDummy.AcceptConfirmedDeath(authority.Death(livingDummy.Snapshot.Identity, 30, 1));
        Layer(livingDummy);
        That(livingDummy.AcceptConfirmedDeath(authority.Death(livingDummy.Snapshot.Identity, 40, 2)).ClearFact is
            { ClearMap: var finalMap, ConfirmedDeathEvents.Count: 2 } && finalMap == last,
            "基础dummy仍存活时末层真实死亡正常结算，不补造基础dummy死亡事实");

        SinglePlayerDungeonRun Create(DungeonEntryPlan planned, DungeonFixedRoute fixedRoute)
        {
            That(SinglePlayerDungeonRun.TryCreate(planned, 1, 100, fixedRoute, authority, out var result, out _),
                "分层边界复用完整资源行与唯一对象key夹具");
            result!.CompleteLoading(result.Snapshot.Identity, Guid.NewGuid());
            That(result.Move(result.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted,
                "夹具沿已声明连接进入基础Boss房");
            result.CompleteLoading(result.Snapshot.Identity, Guid.NewGuid());
            return result;
        }
        void Layer(SinglePlayerDungeonRun target)
        {
            That(target.MoveLayer(target.Snapshot.Identity, Guid.NewGuid(), boss.Position, true).Outcome == DungeonRunOutcome.Accepted,
                "基础入口或已清叠层沿原MoveLayer进入下一层");
            target.CompleteLoading(target.Snapshot.Identity, Guid.NewGuid());
        }
        static DungeonMonsterPlan Change(DungeonMonsterPlan plan, bool isDummy, int template = 1)
        {
            var sources = plan.Room.Monsters.Select(source => source with { MonsterId = template,
                RawValues = Array.AsReadOnly(new[] { template }.Concat(source.RawValues.Skip(1)).ToArray()),
                Modifiers = isDummy ? ["[dummy]"] : [] }).ToArray();
            return DungeonMonsterPlan.FromRoom(plan.Room with { Monsters = sources }, 99, plan.Monsters[0].ObjectKey);
        }
    }

    private static void VerifyExplicitTerminalLayer()
    {
        var start = new DungeonMap("map", new(0,0), 10, "map/fixture-start.map");
        var entrance = new DungeonMap("map", new(1,0), 11, "map/fixture-entrance.map");
        var boss = new DungeonMap("boss", new(2,0), 20, "map/fixture-boss.map");
        var layer = boss with { Kind = "layered", Position = entrance.Position };
        var maze = new DungeonMaze(0,3,1,"fixture-terminal-alias",[start.Position],[boss.Position],[start,entrance,boss],[])
            { LayeredMaps = [layer] };
        var accepted = new DfoServer.Game.Characters.ActiveMainQuest(0,100,1) { AcceptanceId = Guid.NewGuid() };
        var entry = new DungeonEntryPlan(99,0,17,100,0,0,maze,start,boss)
        {
            HasExplicitClearMap = true, QuestAcceptanceId = accepted.AcceptanceId, QuestAcceptances = [accepted],
            TerminalLayerMap = layer, TerminalLayerQuestId = accepted.QuestId, TerminalLayerQuestSlot = accepted.Slot,
            TerminalLayerAcceptanceId = accepted.AcceptanceId
        };
        var terminal = BossPlan(layer,40);
        var source = terminal.Room.Monsters[0] with { Modifiers = ["[dummy]"] };
        terminal = DungeonMonsterPlan.FromRoom(terminal.Room with { Monsters = [source], DeclaredType = "[boss]" },99,40);
        var route = new DungeonFixedRoute([Plan(start,10),Plan(entrance,11),BossPlan(boss,20),terminal],
            [new(start.Position,entrance.Position),new(entrance.Position,boss.Position)]);
        var authority = new FixtureCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,100,route,authority,out var run,out _) &&
            run!.Entry.BossMap == boss && !run.Entry.IsTerminalMap(boss) && run.Entry.IsTerminalMap(layer),
            "异坐标显式末层是唯一领域终点，原BossMap投影保持不变");
        That(SinglePlayerDungeonRun.TryCreate(entry with { StartMap = boss, Maze = maze with { Starts = [boss.Position] } },
            2,200,route,authority,out var baseRun,out _), "单独核对基础Boss死亡，不跳过实际路线的必经叠层");
        baseRun!.CompleteLoading(baseRun.Snapshot.Identity,Guid.NewGuid());
        That(baseRun.AcceptConfirmedDeath(authority.Death(baseRun.Snapshot.Identity,20,1)).ClearFact is null,
            "相同MAP编号的基础Boss死亡不能提前完成末层任务");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,10,1));
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),entrance.Position);
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,11,2));
        That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),entrance.Position,true).Outcome == DungeonRunOutcome.Accepted,
            "末层沿真实声明位置和原移动入口进入");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        var before = run.Snapshot.Identity;
        var death = authority.Death(before,40,3);
        var pending = run.PrepareConfirmedDeath(death).Pending!;
        That(pending.Result.ClearFact?.ClearMap == layer && run.ClearFact is null &&
            DfoServer.Game.Quests.QuestClearEligibility.TryValidate(run.Entry,pending),
            "真实可报告dummy死亡沿原终末事实和任务准入，不由剧情消失授予成功");
        That(run.CancelUncommittedDeath(pending) && run.CurrentLivingMonster(40) is not null && run.Snapshot.Identity == before,
            "未提交末层死亡保留原epoch与存活状态");
        pending = run.PrepareConfirmedDeath(death).Pending!;
        run.ConfirmCommittedDeath(pending);
        That(run.Phase == SinglePlayerDungeonPhase.Settlement && run.Snapshot.Identity.Epoch == before.Epoch+1 &&
            run.AcceptConfirmedDeath(death).Outcome == DungeonRunOutcome.Duplicate,
            "同事件末层死亡仅提交一次通关且旧epoch不能退回基础层");
        foreach (var changed in new[]
        {
            entry with { HasExplicitClearMap = false }, entry with { TerminalLayerAcceptanceId = Guid.NewGuid() },
            entry with { TerminalLayerMap = layer with { Path = "map/other.map" } },
            entry with { TerminalLayerMap = layer with { Position = boss.Position } },
            entry with { IsQuestReplay = true }
        })
            That(!SinglePlayerDungeonRun.TryCreate(changed,2,200,route,authority,out _,out _),
                "末层别名拒绝非显式目标、异接取、异路径、基础坐标与回放模式");
        var unrelated = accepted with { QuestId = 101, Slot = 1, AcceptanceId = Guid.NewGuid() };
        That(!SinglePlayerDungeonRun.TryCreate(entry with { QuestAcceptances = [accepted,unrelated],
            TerminalLayerAcceptanceId = unrelated.AcceptanceId },2,200,route,authority,out _,out _) &&
            !SinglePlayerDungeonRun.TryCreate(entry with { TerminalLayerQuestSlot = 1 },2,200,route,authority,out _,out _),
            "另一已绑定UUID或槽位不能替代实际末层目标三元组");
        foreach (var invalidRoom in new[]
        {
            terminal.Room with { DeclaredType = null }, terminal.Room with { DeclaredType = "[normal]" },
            terminal.Room with { Monsters = [source with { RankKind = "[normal]", Modifiers = [] }] },
            terminal.Room with { Monsters = [source with { Team = 0 }] }
        })
        {
            var invalid = DungeonMonsterPlan.FromRoom(invalidRoom,99,40,playerTeam:0);
            That(!SinglePlayerDungeonRun.TryCreate(entry,2,200,route with { Rooms = [.. route.Rooms.Take(3),invalid] },authority,out _,out _),
                "真实MAP类型及敌方可报告Boss必须共同成立");
        }
    }

    private static DungeonMonsterPlan Plan(DungeonMap map, params ushort[] keys)
    {
        var sources = keys.Select(_ => new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
            "[fixed]", "[normal]", "monster/fixture.mob")).ToArray();
        var room = new DungeonRoomDefinition(map, Array.AsReadOnly(sources), [], []);
        return new(room, Array.AsReadOnly(keys.Select((key, index) => new DungeonPlannedMonster(index, key, 1, 3, sources[index])).ToArray()));
    }

    private static DungeonMonsterPlan WithTeams(DungeonMonsterPlan plan, params int[] teams)
        => DungeonMonsterPlan.FromRoom(plan.Room with { MonsterTeams = Array.AsReadOnly(teams) }, 99, plan.Monsters[0].ObjectKey,
            playerTeam: 0);

    private static DungeonMonsterPlan BossPlan(DungeonMap map, params ushort[] keys)
    {
        var plan = Plan(map, keys);
        var sources = plan.Room.Monsters.Select(source => source with { RankKind = "[boss]" }).ToArray();
        return DungeonMonsterPlan.FromRoom(plan.Room with { Monsters = Array.AsReadOnly(sources) }, 99, keys[0]);
    }

    // 仅为状态机单测合成已确认事实，不是实际伤害、HP 或死亡的战斗算法。
    private sealed class FixtureCombatAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key, long sequence, Guid? eventId = null)
            => ConfirmDeath(identity, eventId ?? Guid.NewGuid(), key, sequence);
    }
}
