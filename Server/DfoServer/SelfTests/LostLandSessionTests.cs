using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static class LostLandSessionTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int checks = Count;
        var time = new LostLandTestClock();
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        VerifyWhirlwindSources(pvf,rooms);
        VerifyRescueSources(pvf,rooms);
        var incounts = DungeonIncountCatalog.Load(pvf);
        using var f = new QuestSceneTests.Fixture(towns, characters, SelfTestCatalogs.SkillLearning(pvf),
            SelfTestCatalogs.Growth(pvf), SelfTestCatalogs.Quests(pvf), false, level: 95, job: 4,
            completed: [3156], position: new(40, 0, 845, 155), timeProvider: time, incounts: incounts);
        var session = f.Session;
        That(Enter() is null && session.LastFormalDungeonRejection == "lost-land-selection-mismatch", "直接16不能绕过乔安选择身份");
        That(session.BeginDungeonSelection() is not null, "乔安建立本角色本次选图身份");
        string before = State();
        f.Sql("CREATE TRIGGER lost_land_entry_failure BEFORE UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'lost land entry rollback'); END");
        bool failed = false;
        try { Enter(); } catch (SqliteException) { failed = true; }
        That(failed && State() == before && session.DungeonSnapshot is null && session.Phase == SessionPhase.MapSelect,
            $"入场SQL失败回滚角色、次数、尝试号和对象key，不安装半个正式实例：failed={failed},before={before},after={State()},phase={session.Phase},reason={session.LastFormalDungeonRejection}");
        f.Sql("DROP TRIGGER lost_land_entry_failure");
        var entered = Enter();
        That(entered is { Entry.LostLand: not null, Entry.Maze.StaticTopology.Rooms.Count: 87, Entry.Maze.StaticTopology.Links.Count: 81 },
            "匹配PVF整条87房正式资源准备成功，保留81条普通连接");
        string installed = State();
        That(Enter() is null && State() == installed && Convert.ToInt64(f.Query("SELECT daily_used FROM character_dungeon_incount WHERE dungeon=4000")) == 1,
            "重复16不再次扣次或递增尝试，沿既有角色次数事务扣一次");
        time.Advance(TimeSpan.FromHours(1));
        var loaded = session.CompleteFormalDungeonLoading()!;
        var revision = session.PlanAndCommitInventory(character => new(true, 0,
            new InventoryPlan(character.Inventory, character.Inventory with { Gold = character.Inventory.Gold + 1 }, "test_lost_land_revision")),
            requireTown: false, requiredPhase: SessionPhase.Dungeon);
        That(revision.Accepted && !session.CanPublishFormalDungeon(loaded), "正常资产提交使旧加载快照失效");
        var timer = session.StartedLostLandTimer(loaded) ?? throw new InvalidOperationException("资产revision变化后计时任务身份丢失");
        That(loaded.LostLandStarted && timer.Remaining.TotalSeconds == LostLandAdmission.InitialSeconds && timer.OpaqueValue == 1200 &&
            session.CompleteFormalDungeonLoading() is null, "首次37才启动1800秒，重复37不成功或重置");
        That(session.TickLostLandTimer(timer) is { Expired: false } && session.Phase == SessionPhase.Dungeon,
            "提前计时唤醒不结束实例");
        var originalKeys = loaded.Snapshot.LivingMonsters.ToArray();
        string beforeMove = State();
        That(loaded.Snapshot.CurrentMap.Id == 11200 &&
            loaded.Monsters.Monsters.Any(monster => monster.IsBlocking && originalKeys.Contains(monster.ObjectKey)),
            "真实起点保留尚未击杀的阻塞怪，换房不用空房夹具替代");
        var sea = session.MoveFormalDungeon(7,9);
        That(sea is { Snapshot.CurrentMap.Id: 11214, Snapshot.MapPayload: DungeonMapPayload.Build } && State() == beforeMove,
            "真实港口的水旋风可到达海域，沿原入场账本只扣一次");
        That(session.CompleteFormalDungeonLoading() is { LostLandStarted: false }, "水旋风转场不重新启动时钟");
        var seaKeys = sea!.Snapshot.LivingMonsters.ToArray();
        That(session.MoveFormalDungeon(8,6) is null && session.MoveFormalDungeon(0,6) is null,
            "海域不能借其他源房的传送声明或无关目标换房");
        var fromSea = session.MoveFormalDungeon(9,6);
        That(fromSea is { Snapshot.CurrentMap.Id: 11200, Snapshot.MapPayload: DungeonMapPayload.Cached } &&
            fromSea.Snapshot.LivingMonsters.SequenceEqual(originalKeys) && State() == beforeMove,
            "海域水旋风返回港口仍沿原缓存保留怪物key和次数");
        session.CompleteFormalDungeonLoading();
        That(session.MoveFormalDungeon(7,9) is { Snapshot.MapPayload: DungeonMapPayload.Cached } cachedSea &&
            cachedSea.Snapshot.LivingMonsters.SequenceEqual(seaKeys), "再次使用水旋风保留海域原演员");
        session.CompleteFormalDungeonLoading();
        session.MoveFormalDungeon(9,6); session.CompleteFormalDungeonLoading();
        time.Advance(TimeSpan.FromSeconds(100));
        var moved = session.MoveFormalDungeon(8, 6);
        That(moved is { Snapshot.CurrentMap.Id: 11251, Snapshot.MapPayload: DungeonMapPayload.Build } && State() == beforeMove,
            "真实首房有活怪时可向左进入11251，不重复提交次数、尝试号和对象key");
        That(session.CompleteFormalDungeonLoading() is { LostLandStarted: false }, "邻房加载不重复启动计时");
        var secondKeys = moved!.Snapshot.LivingMonsters.ToArray();
        That(moved.Monsters.Room.LostLandWarps.Select(warp => warp.Destination).OrderBy(point => point.X).ThenBy(point => point.Y)
            .SequenceEqual(new DungeonPoint[] { new(0,6),new(1,6),new(1,7) }), "真实第二房只从三个无条件ACT取得传送目标");
        foreach (var (target,id) in new (DungeonPoint,int)[] { (new(1,7),11206),(new(1,6),11205),(new(0,6),11252),(new(8,7),11202) })
        {
            var destination = session.MoveFormalDungeon((byte)target.X,(byte)target.Y);
            That(destination is not null && destination.Snapshot.CurrentMap.Id == id && State() == beforeMove,
                "第二房无条件传送及向下普通门均只推进原实例，不改变入场账本");
            That(session.CompleteFormalDungeonLoading() is { LostLandStarted: false }, "传送目标加载不重启时钟");
            var back = session.MoveFormalDungeon(8,6);
            That(back is { Snapshot.CurrentMap.Id: 11251, Snapshot.MapPayload: DungeonMapPayload.Cached } &&
                back.Snapshot.LivingMonsters.SequenceEqual(secondKeys), "传送回访保持第二房全部原怪物key");
            session.CompleteFormalDungeonLoading();
        }
        var returned = session.MoveFormalDungeon(9, 6);
        That(returned is { Snapshot.CurrentMap.Id: 11200, Snapshot.MapPayload: DungeonMapPayload.Cached } &&
            returned.Snapshot.LivingMonsters.SequenceEqual(originalKeys), "真实往返沿原缓存保留起点全部存活怪物key");
        That(session.CompleteFormalDungeonLoading() is { LostLandStarted: false } &&
            session.TickLostLandTimer(timer) is { Expired: false, Remaining.TotalSeconds: LostLandAdmission.InitialSeconds - 100 }, "往返加载不重发启动事实或重置1800秒总时限");
        // 按实收路线抵达铁笼房；返程仍是本地救援ACT发起的导航，不代替机关或战斗确认。
        foreach (var (target,id) in new (DungeonPoint,int)[]
            { (new(8,6),11251),(new(8,7),11202),(new(0,9),11209),(new(0,8),11212),(new(1,8),11213),(new(1,9),11211) })
        {
            That(session.MoveFormalDungeon((byte)target.X,(byte)target.Y) is { } step && step.Snapshot.CurrentMap.Id == id,
                "实际救援路线的普通门和已有传送均沿原实例抵达公主房");
            session.CompleteFormalDungeonLoading();
        }
        var rescued = session.MoveFormalDungeon(9,6);
        That(rescued is { Snapshot.CurrentMap.Id: 11200, Snapshot.MapPayload: DungeonMapPayload.Cached } &&
            rescued.Snapshot.LivingMonsters.SequenceEqual(originalKeys) && State() == beforeMove,
            "公主返程沿精确救援链回到起始缓存，不重扣次数或清除原怪物");
        That(session.CompleteFormalDungeonLoading() is { LostLandStarted: false } &&
            session.TickLostLandTimer(timer) is { Expired: false, Remaining.TotalSeconds: LostLandAdmission.InitialSeconds - 100 },
            "救援返程不重启1800秒时钟");
        time.Advance(TimeSpan.FromSeconds(LostLandAdmission.InitialSeconds - 100));
        var expired = session.TickLostLandTimer(timer)!;
        That(expired.Expired && session.DungeonSnapshot!.Phase == SinglePlayerDungeonPhase.TimedOut &&
            !session.CanPublishLostLandTimer(timer) && session.CanPublishLostLandTimer(expired), "到期冻结原Run并使开始通知失效");
        That(session.ReturnFromExpiredLostLand(expired) is { Character.Position.Town: 40, Character.Position.Area: 0 } &&
            session.Phase == SessionPhase.Town && session.DungeonSnapshot is null &&
            session.ReturnFromExpiredLostLand(expired) is null && session.ReturnFromFormalDungeon() is null,
            "自然到期沿原回城一次退出，重复到期和42不重复保存");
        That(Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts")) == 0,
            "限时退出不借普通结算产生奖励回执");
        session.BeginDungeonSelection();
        That(Enter() is null && session.LastFormalDungeonRejection == "dungeon-incount-exhausted",
            "资源每日一次用尽后仍拒绝再入场，计时退出不恢复次数");
        time.Now = time.Now.AddDays(1);
        That(session.BeginDungeonSelection() is not null && Enter() is not null, "次日新选择可创建独立新实例");
        var second = session.CompleteFormalDungeonLoading()!;
        var secondTimer = session.StartedLostLandTimer(second)!;
        That(session.TickLostLandTimer(timer) is null && session.ReturnFromExpiredLostLand(expired) is null &&
            session.CanPublishLostLandTimer(secondTimer), "旧实例回调不得结束新实例");
        That(session.ReturnFromFormalDungeon() is not null && session.TickLostLandTimer(secondTimer) is null,
            "主动退出使原计时回调失效");
        time.Now = time.Now.AddDays(1);
        session.BeginDungeonSelection(); Enter();
        var thirdTimer = session.StartedLostLandTimer(session.CompleteFormalDungeonLoading()!)!;
        session.ReturnToRoster();
        That(session.TickLostLandTimer(thirdTimer) is null && !session.CanPublishLostLandTimer(thirdTimer), "换角使旧计时失效");
        VerifyAcceptedQuestEntry(towns, pvf, characters, incounts, dungeons, rooms);
        Console.WriteLine($"PASS {Count - checks} 遗忘之地正式Session次数事务、1800秒和退出生命周期断言（非客户端验收）");

        FormalDungeonReceipt? Enter() => session.EnterFormalDungeon(4000, 0, 0, dungeons, rooms);
        string State() => $"{f.Query("SELECT printf('%d|%d|%d',c.revision,s.last_attempt,s.next_object_key) FROM characters c JOIN character_dungeon_state s ON s.character_id=c.id")};" +
            $"{f.Query("SELECT coalesce(sum(daily_used),0) FROM character_dungeon_incount WHERE dungeon=4000")};" +
            $"{f.Query("SELECT coalesce(sum(used),0) FROM account_dungeon_incount WHERE dungeon=4000")}";
    }

    static void VerifyAcceptedQuestEntry(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters,
        DungeonIncountCatalog incounts, DungeonCatalog dungeons, DungeonRoomCatalog rooms)
    {
        var quests = SelfTestCatalogs.Quests(pvf);
        var acceptance = Guid.NewGuid();
        var seeking = quests.Get(13613);
        var active = new ActiveMainQuest(0, 13613, 1) { AcceptanceId = acceptance };
        var seeker = new Character(7, 0, "LostLandQuest", 0, new(40, 0, 845, 155), null)
            { Level = 95, Job = 4, CompletedQuests = [13601, 3156], ActiveMainQuests = [active] };
        That(DungeonEntryPlan.TryCreate(dungeons, seeker, 4000, 13613, 0, out var planned, out var reason, quest: seeking, quests: quests) &&
            planned is { LostLand: not null, IsOrdinaryHunt: true, QuestId: 13613, RouteQuestId: 0, Difficulty: 0 } &&
            planned.QuestAcceptanceId == acceptance,
            "已接取13613沿普通迷宫进入遗忘之地：" + reason);
        var hunt = quests.Get(13602);
        var huntActive = new ActiveMainQuest(0, 13602, QuestHuntProgress.Initial(hunt)) { AcceptanceId = Guid.NewGuid() };
        That(DungeonEntryPlan.TryCreate(dungeons, seeker with { ActiveMainQuests = [huntActive] }, 4000, 13602, 0, out var hunted, out var huntReason,
                quest: hunt, quests: quests) &&
            hunted is { LostLand: not null, IsOrdinaryHunt: true, QuestId: 13602 },
            "已接取13602猎杀沿同一入口进入：" + huntReason);
        var meet = quests.Get(13609);
        var meetActive = new ActiveMainQuest(0, 13609, 1) { AcceptanceId = Guid.NewGuid() };
        That(DungeonEntryPlan.TryCreate(dungeons, seeker with { ActiveMainQuests = [meetActive] }, 4000, 13609, 0, out var met, out var meetReason,
                quest: meet, quests: quests) &&
            met is { LostLand: not null, IsOrdinaryHunt: true, QuestId: 13609 },
            "已接取13609会面沿同一入口进入：" + meetReason);
        var other = quests.Get(6084);
        var otherActive = new ActiveMainQuest(0, 6084, QuestHuntProgress.Initial(other)) { AcceptanceId = Guid.NewGuid() };
        That(!DungeonEntryPlan.TryCreate(dungeons, seeker with { ActiveMainQuests = [otherActive] }, 4000, 6084, 0, out _, out var otherReason,
                quest: other, quests: quests) && otherReason == "lost-land-mode-unsupported",
            "未声明4000的任务不能进入遗忘之地：" + otherReason);

        using var fixture = new QuestSceneTests.Fixture(towns, characters, SelfTestCatalogs.SkillLearning(pvf),
            SelfTestCatalogs.Growth(pvf), quests, false, level: 95, job: 4,
            completed: [3156], position: new(40, 0, 845, 155), incounts: incounts);
        fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) SELECT id,13601,0,NULL FROM characters");
        fixture.Sql($"INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) SELECT id,5,13613,1,'{acceptance:N}' FROM characters");
        fixture.Session.ReturnToRoster();
        fixture.Session.Select(0, resumeTown: true);
        fixture.Session.MarkReady();
        That(fixture.Session.EnterFormalDungeon(4000, 13613, 0, dungeons, rooms) is null &&
            fixture.Session.LastFormalDungeonRejection == "lost-land-selection-mismatch",
            "接取后直接16仍不能绕过乔安选图");
        That(fixture.Session.BeginDungeonSelection() is not null, "接取13613后乔安仍能打开选图");
        var entered = fixture.Session.EnterFormalDungeon(4000, 13613, 0, dungeons, rooms);
        That(entered is { Entry.LostLand: not null, Entry.IsOrdinaryHunt: true, Entry.QuestId: 13613, Entry.RouteQuestId: 0 } &&
            entered.Entry.QuestAcceptanceId == acceptance,
            "选图后携带13613可以进入遗忘之地：" + fixture.Session.LastFormalDungeonRejection);
    }

    static void VerifyRescueSources(PvfArchive pvf, DungeonRoomCatalog rooms)
    {
        var objects = DungeonRoomCatalog.ParsePassiveDirectory(pvf.ReadTokens("passiveobject/passiveobject.lst"),pvf.ResolveString);
        var room = rooms.Get(new("map",new(1,9),11211,"map/forgotten_land/downroom3.map"));
        int ordinal = room.PassiveObjects.ToList().FindIndex(source => source.ObjectId == 37073);
        var warp = DungeonLostLandRescue.Read(ordinal,room,objects,pvf.ReadTokens,pvf.ResolveString);
        var frozen = rooms.WithLostLandWarps(room).LostLandWarps.Single(candidate => candidate.ObjectId == 37073);
        That(warp is not null && warp.ObjectOrdinal == ordinal && warp.ObjectId == 37073 &&
            warp.Destination == new DungeonPoint(9,6) && warp.PlayerX == 376 && warp.PlayerY == 320 && warp.MoveParameter == 0 &&
            warp.SourceChain.Count == 4 && warp.SourceChain.Contains(objects[37073]) && warp.SourceChain.Contains(objects[37080]) &&
            frozen.Destination == warp.Destination && frozen.ActionPath == warp.ActionPath && frozen.SourceChain.SequenceEqual(warp.SourceChain),
            "真实公主房连接实际公主、铁笼、基本动作及救援返程，并由原房间目录冻结资格");
        var files = warp!.SourceChain.ToDictionary(file => file,file => (IReadOnlyList<PvfToken>)pvf.ReadTokens(file));
        foreach (string file in warp.SourceChain)
            That(DungeonLostLandRescue.Read(ordinal,room,objects,
                candidate => candidate == file ? [] : files[candidate],pvf.ResolveString) is null,
                "救援链中任一实际OBJ或ACT缺失都不能授予返程导航");
        foreach (string file in warp.SourceChain.Where(file => file.EndsWith(".act",StringComparison.Ordinal)))
            That(DungeonLostLandRescue.Read(ordinal,room,objects,
                candidate => candidate == file ? files[candidate].Append(new PvfToken(0,1)).ToArray() : files[candidate],pvf.ResolveString) is null,
                "救援ACT附加未知条件或行为不能借用返程导航");
        var cage = room.PassiveObjects.Single(source => source.ObjectId == 37080);
        foreach (var sources in new[]
        {
            room.PassiveObjects.Where(source => source.ObjectId != 37080).ToArray(),
            room.PassiveObjects.Append(cage).ToArray(),
            room.PassiveObjects.Select(source => source == cage ? source with { Path = objects[37074] } : source).ToArray(),
            room.PassiveObjects.Select(source => source.ObjectId == 37073 ? source with { Path = objects[37074] } : source).ToArray()
        })
            That(DungeonLostLandRescue.Read(ordinal,room with { PassiveObjects = sources },objects,pvf.ReadTokens,pvf.ResolveString) is null,
                "缺少铁笼、歧义铁笼或实际OBJ引用不符均不能取得救援链资格");
        string basic = warp.SourceChain.Single(file => file.EndsWith("/basic.act",StringComparison.Ordinal));
        var wrongIndex = files[basic].ToArray();
        wrongIndex[45] = new(0,1);
        That(DungeonLostLandRescue.Read(ordinal,room,objects,
            candidate => candidate == basic ? wrongIndex : files[candidate],pvf.ResolveString) is null,
            "基本动作切换到不存在的自定义索引时不能取得救援返程资格");
    }

    static void VerifyWhirlwindSources(PvfArchive pvf,DungeonRoomCatalog rooms)
    {
        var objects = DungeonRoomCatalog.ParsePassiveDirectory(pvf.ReadTokens("passiveobject/passiveobject.lst"),pvf.ResolveString);
        foreach (var (id,path,point,objectId,target) in new (int,string,DungeonPoint,int,DungeonPoint)[]
        {
            (11200,"map/forgotten_land/forgotten_land_1.map",new(9,6),37074,new(7,9)),
            (11214,"map/forgotten_land_hiden/forgotten_land_hiden1.map",new(7,9),37077,new(9,6))
        })
        {
            var room = rooms.Get(new("map",point,id,path));
            int ordinal = room.PassiveObjects.ToList().FindIndex(source => source.ObjectId == objectId);
            var source = room.PassiveObjects[ordinal];
            var warp = DungeonLostLandWhirlwind.Read(ordinal,source,objects,pvf.ReadTokens,pvf.ResolveString);
            That(warp is not null && warp.ObjectOrdinal == ordinal && warp.ObjectId == objectId &&
                warp.Destination == target && warp.MoveParameter == 550 &&
                warp.SourceChain.Contains(source.Path!) && warp.SourceChain.Contains(objects[objectId+1]) &&
                warp.SourceChain.Contains(objects[objectId+2]) && warp.SourceChain.Contains(warp.ActionPath),
                "真实去返旋风必须完整连接实际NPC、施法、两级生成、起始、传送和销毁资源");
            var files = warp!.SourceChain.ToDictionary(file => file,file => (IReadOnlyList<PvfToken>)pvf.ReadTokens(file));
            foreach (string file in warp.SourceChain)
                That(DungeonLostLandWhirlwind.Read(ordinal,source,objects,
                    candidate => candidate == file ? [] : files[candidate],pvf.ResolveString) is null,
                    "链中任一OBJ或ACT缺失均不得授予水旋风导航");
            foreach (string file in warp.SourceChain.Where(file => file.EndsWith(".act",StringComparison.Ordinal)))
                That(DungeonLostLandWhirlwind.Read(ordinal,source,objects,
                    candidate => candidate == file ? files[candidate].Append(new PvfToken(0,1)).ToArray() : files[candidate],pvf.ResolveString) is null,
                    "链中任一ACT附加未知条件或动作均不得授予水旋风导航");
            That(DungeonLostLandWhirlwind.Read(ordinal,source with { Path = objects[37073] },objects,pvf.ReadTokens,pvf.ResolveString) is null,
                "只报NPC编号而实际物件引用不符不能借用旋风链");
        }
    }
}
