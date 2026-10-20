using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestPassiveClearTcpTests
{
    // 借用quests组已启动的隔离服务和通知消费者；不另建资源、监听或冷启动。
    internal static void Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain)
    {
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        foreach (bool ordinary in new[] { false, true }) VerifyCase(fixture,resources,growth,drain,dungeons,ordinary);
        VerifyZeroKill(fixture,resources,growth,drain);
        VerifyLayeredCinematic(fixture,resources,growth,drain,dungeons);
        VerifyPassiveHunt(fixture,resources,growth,drain,dungeons,3738,102,16120,76530,
            [0,0,7,5,4,6,5,15],[76532,76533,76534]);
        VerifyPassiveHunt(fixture,resources,growth,drain,dungeons,5964,291100308,109022849,59594,
            [2,1,1,7,1,1,2,1,2,1],[59598,59600,59601,59602,59603,59599,59595,59594,59596,59597]);
        VerifyPassiveHunt(fixture,resources,growth,drain,dungeons,3646,74,10624,91901,
            [0,8,0,3,3,5,6,0,2],[91902,91904,91909,91905],
            [91901,91908,91902,91903,91909,91904,91905,91906,91907],expectedStartMap:91901);
    }

    // #181/#218/#240：准确任务复用同一热服务；单份、多份及跨房回访通过合成33驱动，不把消费槽解释成实例号。
    static void VerifyPassiveHunt(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain,DungeonCatalog dungeons,
        ushort questId,uint dungeonId,int templateId,int firstMap,int[] copies,int[] targetMaps,int[]? mapOrder=null,
        int? expectedStartMap=null)
    {
        string account = "passive-hunt-"+questId;
        var quest = resources.Quests.Get(questId);
        That(QuestHuntProgress.TryTargets(quest,out var targets) && targets is [{ EnemyType:3,Count:15 }] &&
            targets[0].DungeonId == dungeonId && targets[0].MonsterId == templateId,
            $"{questId} 保留 D{dungeonId} 被动物件的单通道15次真实目标");
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,dungeonId);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            var candidate = CharacterCreation.Prepare("Hunt"+questId,resources.Characters.Get(0),
                entrance.Position,entrance.Position,resources.Learning.Get);
            byte level = checked((byte)quest.MinimumLevel);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
            {
                Level=level,AcceptedStoryLevel=level,CompletedQuests=entrance.Completed,
                InitialLearning=SkillPointLedger.Synchronize(0,0,level,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(level) },resources.Learning)
            });
        }
        string acceptance = "";
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,questId,dungeonId,row => information=row);
            var maze = dungeons.Get(checked((int)dungeonId)).Mazes.Single(row => row.SourceOrdinal == information[7]);
            var route = maze.MapSpecification!;
            var actualRooms = route.Select(row => rooms.Get(row,requirePassiveDefinitions:false)).ToArray();
            That(route.Select(row => row.Id).SequenceEqual(mapOrder ?? Enumerable.Range(firstMap,copies.Length).ToArray()) &&
                actualRooms.All(row => row.PassiveObjects.Count(source => source.ObjectId == templateId) == copies[row.Map.Id-firstMap] &&
                    row.PassiveObjects.Where(source => source.ObjectId == templateId).All(source =>
                        source.Path is { Length:>0 } path && resources.Archive.Contains(path))),
                "准确任务地图集合和各房实际目标放置数保持原资源");
            if (expectedStartMap is { } start)
                That(map.Length >= 19 && map[13] != 0 && U32(map,14) == start,
                    "准确任务实际入场保持声明起点，不跳过无目标房验证");
            FormalDungeonTcpTests.Load(peer); Drain();
            acceptance = (string)fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId)[0][0];
            That(Scalar() == 15,$"{questId}真实31初始化剩余15，入场和加载不推进");
            string before = State();
            if (copies[CurrentMap().Id-firstMap] == 0)
            {
                Trigger(15);
                That(State() == before,"无目标起始房的33不写条件、资产或事件");
            }
            peer.Send(34,[34,0,(byte)questId,(byte)(questId >> 8),255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == before,$"{questId}进度未完成时34拒绝且不产生奖励");
            uint remaining = 15;
            var consumed = new HashSet<int>();
            var expectedPerMap = new Dictionary<int,int>();
            foreach (int id in targetMaps)
            {
                var target = route.Single(row => row.Id == id);
                var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),target.Position);
                foreach (var next in path.Skip(1))
                {
                    foreach (int offset in Enumerable.Range(0,map[13] == 0 ? 0 : map[18]).Select(at => 19+22*at)
                        .Where(at => map[at+21] == 0 && map[at+13] != 9))
                    {
                        peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12])); Drain();
                    }
                    var move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
                    peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                    var arrived = CurrentMap();
                    That(arrived.Position == next && (map[13] == 0
                        ? map.Length == 16 && map[14] == 0 && map[15] == byte.MaxValue
                        : U32(map,14) == arrived.Id),"沿真实坐标核对首次与缓存入房；缓存包不含地图ID或演员表");
                    FormalDungeonTcpTests.Load(peer); Drain();
                    if (consumed.Contains(arrived.Id))
                    {
                        string saved = State(); Trigger(remaining);
                        That(State() == saved,"回访已耗尽物件的房间不重置放置槽或重复进度");
                    }
                }
                That(CurrentMap().Id == id,$"{questId}沿真实连接到达目标房，不借用相邻任务地图");
                before = State();
                if (copies[id-firstMap] > 1)
                {
                    peer.Send(39,PassiveDeath(checked((uint)templateId))); Drain();
                    That(State() == before,"同模板多份39仍不消费猎杀33的放置槽");
                }
                var assets = fixture.Snapshot("SELECT experience,gold FROM characters WHERE id=$p0",peer.Id);
                int count = Math.Min(copies[id-firstMap],checked((int)remaining));
                expectedPerMap.Add(id,count);
                for (int at=0;at<count;at++) Trigger(--remaining);
                That(fixture.Snapshot("SELECT experience,gold FROM characters WHERE id=$p0",peer.Id) == assets,
                    $"{questId}物件33只推进任务，不伪造普通怪物经验和金币");
                before = State(); Trigger(remaining);
                That(State() == before,"本房放置槽耗尽或目标归零后，相同33不继续递减");
                consumed.Add(id);
            }
            var events = fixture.Query("SELECT acceptance_id,scene_event FROM character_quest_lifecycle_receipts WHERE character_id=$p0 AND scene_event IS NOT NULL",peer.Id);
            var sources = events.Select(row => JsonSerializer.Deserialize<QuestSceneEvent>((string)row[1])!).ToArray();
            That(remaining == 0 && events.Length == 15 && events.All(row => (string)row[0] == acceptance) &&
                sources.All(row => row.DungeonId == dungeonId && row.TemplateId == templateId) &&
                sources.GroupBy(row => row.MapId).All(group => expectedPerMap.GetValueOrDefault(group.Key) == group.Count()) &&
                sources.Select(row => row.OperationId).Distinct().Count() == 15 &&
                sources.Select(row => (row.MapId,row.SourceOrdinal)).Distinct().Count() == 15 &&
                sources.Select(row => row.Identity.InstanceId).Distinct().Count() == 1,
                "15次事务沿原接取及同Run保存独立消费槽与操作回执，不把重复正文当实例号");

            DungeonMap CurrentMap() => route.Single(row => row.Position == new DungeonPoint(map[0],map[1]));
            void Drain() => drain(peer,null);
            uint Scalar() => checked((uint)(long)fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId)[0][0]);
            void Trigger(uint expected)
            {
                // 真实发送器的同通道正文相同；明确合成事件，不声称客户端逐物件触发或ACK重发已实测。
                peer.Send(33,[33,0,(byte)questId,(byte)(questId >> 8),16,0,0]);
                var ack = peer.Expect(1,33).Body;
                That(ack.Length == 7 && ack[0] == 1 && U16(ack,1) == questId && U32(ack,3) == expected,
                    $"{questId} ACK33返回实际提交后的剩余数量："+expected);
                Drain(); That(Scalar() == expected,$"{questId} ACK与独立读库进度一致");
            }
        }
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(fixture.Query("SELECT condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId)
                is [[0L,string restored]] && restored == acceptance,"断线重登保存原接取及15次已提交进度");
            string before = State();
            fixture.Sql("CREATE TRIGGER abort_passive_hunt_finish AFTER INSERT ON character_quest_completion_receipts WHEN NEW.quest_id="+questId+" BEGIN SELECT RAISE(ABORT,'passive hunt finish rollback'); END");
            peer.Send(34,[34,0,(byte)questId,(byte)(questId >> 8),255,255,1,0,255,255]); peer.Closed();
            That(State() == before,$"{questId}交付SQL末段失败以EOF收尾，条件、完成位及奖励整笔回滚");
            fixture.Sql("DROP TRIGGER abort_passive_hunt_finish");
        }
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            peer.Send(34,[34,0,(byte)questId,(byte)(questId >> 8),255,255,1,0,255,255]);
            var ack = peer.Expect(1,34).Body; drain(peer,null);
            That(ack.Length >= 4 && ack[0] == 1 && U16(ack,1) == questId && ack[3] == 6 && fixture.Query(
                "SELECT acceptance_id FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId)
                is [[string completed]] && completed == acceptance && fixture.Query(
                "SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId) is [[0L]],
                $"{questId}重连后真实34以猎杀分类交付原接取，只保存一份完成回执");
            string before = State();
            peer.Send(34,[34,0,(byte)questId,(byte)(questId >> 8),255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); drain(peer,null);
            That(State() == before,$"{questId}重复34不重复资产或交付回执");
        }
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(peer.CompletedQuestHistory[4+questId] == 1 && fixture.Query(
                "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",peer.Id,questId) is [[1L]],
                $"{questId}最终重登回放完成位和唯一奖励回执");
        }
        string State() => CharacterState(fixture,created.Id)+fixture.Snapshot(
            "SELECT * FROM character_quest_lifecycle_receipts WHERE character_id=$p0 ORDER BY revision",created.Id)+fixture.Snapshot(
            "SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id",created.Id)+fixture.Snapshot(
            "SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",account);
    }

    // 一个跨层成功代表：真实70201基础Boss、70487末层、独立39/33和34；事件正文仍明确合成。
    static void VerifyLayeredCinematic(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain, DungeonCatalog dungeons)
    {
        const string account = "layered-movie-3858";
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,resources.Quests.Get(3858),193);
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            var candidate = CharacterCreation.Prepare("LayerMovie",resources.Characters.Get(0),
                entrance.Position,entrance.Position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,86,candidate.InitialLearning with
                { Experience=growth.GetLevelStartExperience(86) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level=86,AcceptedStoryLevel=86,CompletedQuests=entrance.Completed,InitialLearning=learning });
        }
        byte[] passive = PassiveDeath(69292,74);
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id,"3858选中本条隔离角色，不借用其他路线活动槽");
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,3858,193,row => information=row,requestQuestId:0);
            var maze = dungeons.Get(193).Mazes.Single(row => row.SourceOrdinal == information[7]);
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),new(information[8],information[9]));
            var acceptance = fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3858",peer.Id)[0][0];
            int enabled = 0, bossChecks = 0, deaths = 0;
            var report = DungeonSettlementProtocolTests.Report(peer.Id,99);
            for (int room = 0; room < path.Length; room++)
            {
                FormalDungeonTcpTests.Load(peer); Drain();
                string before = State();
                peer.Send(39,passive); Drain();
                That(State() == before && enabled == 0,"3858基础房不能提前采用末层69292报告");
                for (int at = 0; at < map[18]; at++)
                {
                    int offset = 19+22*at;
                    if (map[offset+21] != 0) continue;
                    if (room == path.Length-1 && map[offset+13] == 3)
                        That(U32(map,offset+8) == 70201,"3858基础Boss保持真实70201而非dummy或物件");
                    peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12]));
                    Drain(); deaths++;
                }
                That(enabled == 0 && bossChecks == 0,"3858基础Boss死亡仍等待独立末层条件，不提前发115或31");
                if (room+1 < path.Length) Move(path[room+1],0);
            }
            That(U32(map,14) == 70484,"3858沿完整真实拓扑抵达基础70484");
            foreach (var layer in maze.LayeredMaps.Where(row => row.Position == path[^1]))
            {
                Move(layer.Position,1);
                That(U32(map,14) == layer.Id,"3858原叠层顺序保留实际地图身份");
                FormalDungeonTcpTests.Load(peer); Drain();
            }
            That(U32(map,14) == 70487 && map[18] == 3 && U32(map,27) == 56501 && map[32] == 5 && map[40] == 1 &&
                U32(map,49) == 68004 && map[54] == 9 && map[62] == 1 &&
                U32(map,71) == 68004 && map[76] == 9 && map[84] == 1 &&
                new[] { U16(map,25), U16(map,47), U16(map,69) }.Distinct().Count() == 3 &&
                enabled == 0 && Scalar() is [[1L]],
                "真实70487保留友方APC56501及两个独立68004特殊父对象，加载不达成演出或清图");
            string saved = State();
            peer.Send(34,[34,0,18,15,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == saved,"3858未收到演出终末时34拒绝且不写奖励");
            peer.Send(39,passive); peer.Expect(0,574); peer.Expect(0,31,[]); enabled++; Drain();
            That(enabled == 1 && bossChecks == 0 && Scalar() is [[1L]] && fixture.Query(
                "SELECT source_type,dungeon_id,map_id,template_id,length(death_events)/16,quest_id FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                is [["PassiveObject",193L,70487L,69292L,long sceneDeaths,0L]] && sceneDeaths == deaths,
                "3858普通0绑定唯一末层scene与实际死亡集合，39不代替type65，空31不造115");
            saved = State(); peer.Send(39,passive); Drain();
            That(State() == saved && enabled == 1,"3858重复物件39不重复场景或结算许可");
            peer.Send(33,[33,0,18,15,0,0]); peer.Expect(1,33,[1,18,15,0,0,0,0]); Drain();
            That(Scalar() is [[0L]] && enabled == 1 && fixture.Query(
                "SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3858",peer.Id)[0][0].Equals(acceptance),
                "3858终末33沿原AcceptanceId达成演出，已经发布的31不重复");
            saved = State(); peer.Send(33,[33,0,18,15,0,0]); peer.Expect(1,33,[1,18,15,0,0,0,0]); Drain();
            That(State() == saved && enabled == 1,"3858重复33不重复保存或发布");
            peer.Send(46,report); Drain();
            That(fixture.Query("SELECT map_id,quest_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                is [[70487L,0L]],"3858正式46保留实际末层70487与普通0实例身份");
            peer.Send(34,[34,0,18,15,255,255,1,0,255,255]);
            var finished = peer.Expect(1,34).Body; Drain();
            That(finished.Length >= 4 && finished[0] == 1 && U16(finished,1) == 3858 && finished[3] == 65 &&
                Scalar().Length == 0,"3858真实Handler以type65交付并清理活动槽，不把物件凭据冒充任务目标");
            saved = State();
            peer.Send(46,report); Drain(); peer.Send(39,passive); Drain();
            peer.Send(34,[34,0,18,15,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == saved && enabled == 1,"3858已交付的46、39和34重放不重复任何资产或凭据");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

            void Move(DungeonPoint point,byte kind)
            {
                var request = new byte[100]; request[0] = checked((byte)point.X); request[1] = checked((byte)point.Y); request[10] = kind;
                peer.Send(45,request); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
            }
            void Drain() => drain(peer,packet =>
            {
                if (packet.Opcode == 31) { Bytes(packet.Body,[],"3858结算许可保持完整空正文"); enabled++; }
                if (packet.Opcode == 115) bossChecks++;
            });
            object[][] Scalar() => fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3858",peer.Id);
        }
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(peer.CompletedQuestHistory[4+3858] == 1 && fixture.Query(
                "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3858",peer.Id) is [[1L]] && fixture.Query(
                "SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id) is [[1L]] && fixture.Query(
                "SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                "3858回城重连恢复完成位与三类独立唯一回执");
            string before = State(); peer.Send(39,passive); drain(peer,null);
            That(State() == before,"3858重连迟到物件报告不能复活旧实例");
        }
        string State() => CharacterState(fixture,created.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY event_id",created.Id)+
            fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",account);
    }

    static void VerifyCase(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain, DungeonCatalog dungeons, bool ordinary)
    {
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 85 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 191));
        var done = new HashSet<int>(area.Quests) { 3841 };
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            // 只准备入口等级和真实前置；接取、击杀、清图及交付均走TCP，不改条件或造怪。
            var candidate = CharacterCreation.Prepare(ordinary ? "PassiveZero" : "QuestPassive",resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,85,candidate.InitialLearning with
                { Experience=growth.GetLevelStartExperience(85) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level=85,AcceptedStoryLevel=85,CompletedQuests=done.ToArray(),InitialLearning=learning });
        }
        byte[] passive = PassiveDeath(69291);
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,3842,191,row => information=row,requestQuestId:ordinary ? (ushort)0 : (ushort)3842);
            var maze = dungeons.Get(191).Mazes.Single(row => row.SourceOrdinal == information[7]);
            That(maze.ClearConditions is [{ Type:"destroy object",TargetId:69291,Count:1 }],
                "3842 TCP按实际单物件清图条件选择D191路线");
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),new(information[8],information[9]));
            var visited = new List<uint>();
            var killed = new Dictionary<uint,int>();
            int enabled = 0, bossChecks = 0;
            for (int room = 0; room < path.Length; room++)
            {
                visited.Add(U32(map,14));
                FormalDungeonTcpTests.Load(peer);
                Drain();
                That(enabled == 0,"3842加载或前序死亡不提前发布物件终点31");
                bool terminal = U32(map,14) == 70407;
                if (terminal)
                {
                    That(map[18] == 1 && U32(map,27) == 808 && map[40] == 1,
                        "70407保留真实友方APC808和清房排除位，不伪造Boss或杀友方");
                    break;
                }
                if (room == 0) RejectPassive(passive,"前房合成52B物件报告不能提前清图");
                for (int at = 0; at < map[18]; at++)
                {
                    int offset = 19+22*at;
                    uint template = U32(map,offset+8);
                    if (map[offset+21] != 0) continue;
                    // 两个猎杀模板之外的真实敌人同样完整击杀，不以任务目标过滤房间演员。
                    peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12]));
                    Drain();
                    killed[template] = killed.GetValueOrDefault(template)+1;
                }
                That(enabled == 0,"完整前房击杀仍须等待69291物件来源提交");
                That(room+1 < path.Length,"3842物件终点必须位于实际路线末端");
                var next = path[room+1];
                var request = new byte[100];
                request[0] = checked((byte)next.X);
                request[1] = checked((byte)next.Y);
                peer.Send(45,request);
                peer.Expect(1,45,[1]);
                map = peer.Expect(0,29).Body;
            }
            That(visited.SequenceEqual(Enumerable.Range(70400,8).Select(id => (uint)id)) &&
                killed.GetValueOrDefault(69722u) == 22 && killed.GetValueOrDefault(69723u) == 21 &&
                fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3842",peer.Id) is [[0L]],
                "TCP完整走过70400..70407，完整击杀两路22/21及其他真实阻挡敌人而未提前清图");
            int deathCount = killed.Values.Sum();
            var wrongChecksum = passive.ToArray();
            wrongChecksum[^1] ^= 1;
            RejectPassive(wrongChecksum,"终点错误摘要不保存场景清图或许可结算");
            RejectPassive(PassiveDeath(69292),"终点错误模板不能借用本房69291资格");
            peer.Send(39,passive);
            var conditions = peer.Expect(0,574).Body;
            That(conditions.Length >= 4 && conditions.Length == 4+6*U32(conditions,0) &&
                Enumerable.Range(0,checked((int)U32(conditions,0))).Any(at => U16(conditions,4+6*at) == 3842 && U32(conditions,6+6*at) == 0),
                "场景事务提交后先发布完整574，保留猎杀任务实际归零状态");
            peer.Expect(0,31,[]);
            enabled++;
            Drain();
            That(enabled == 1 && bossChecks == 0 && fixture.Query(
                "SELECT source_type,dungeon_id,map_id,template_id,length(death_events)/16 FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                is [["PassiveObject",191L,70407L,69291L,long sceneDeaths]] && sceneDeaths == deathCount && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[long growthDeaths]] && growthDeaths == deathCount,
                "合成52B39沿独立物件来源提交完整实际死亡集合，并发送空31而无115");
            That(fixture.Query("SELECT quest_id FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                    is [[long instanceQuest]] && instanceQuest == (ordinary ? 0 : 3842) &&
                fixture.Query("SELECT quest_id FROM character_quest_lifecycle_receipts WHERE character_id=$p0 AND scene_event IS NOT NULL",peer.Id)
                    is [[3842L]],
                "普通0保留实例请求号0，生命周期仍绑定真实3842，不伪造请求或接取身份");
            string committed = State();
            peer.Send(39,passive);
            Drain();
            That(State() == committed && enabled == 1,"重复物件39不重写凭据、资产或重复发送31");
            foreach (byte channel in new byte[] { 0x10,0x20 })
            {
                peer.Send(33,[33,0,2,15,channel,0,0]);
                peer.Expect(1,33,[1,2,15,0,0,0,0]);
                Drain();
                That(State() == committed,"猎杀33只读取已持久归零结果，不把普通怪物条件当物件事件");
            }
            var report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
            peer.Send(46,report);
            Drain();
            That(fixture.Query("SELECT quest_id,map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                    is [[long settledQuest,70407L]] && settledQuest == (ordinary ? 0 : 3842),
                "真实46按物件终末70407提交正式通关奖励");
            committed = State();
            peer.Send(46,report);
            Drain();
            That(State() == committed,"重复46不重复发放物件终末通关奖励");
            peer.Send(34,[34,0,2,15,255,255,1,0,255,255]);
            var finished = peer.Expect(1,34).Body;
            That(finished.Length >= 4 && finished[0] == 1 && U16(finished,1) == 3842 && finished[3] == 6,
                "3842沿真实34猎杀分类交付，不将物件来源伪造为怪物击杀");
            Drain();
            committed = State();
            peer.Send(34,[34,0,2,15,255,255,1,0,255,255]);
            peer.Expect(1,34,[0,19,0]);
            Drain();
            That(State() == committed,"重复34不增加完成回执或奖励");
            peer.Send(42);
            peer.Expect(1,42,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24);
            peer.Expect(0,21);
            peer.Expect(0,356);
            peer.Barrier();

            void Drain() => drain(peer,packet =>
            {
                if (packet.Opcode == 31) enabled++;
                if (packet.Opcode == 115) bossChecks++;
            });
            void RejectPassive(byte[] body, string label)
            {
                string before = State();
                peer.Send(39,body);
                Drain();
                That(State() == before && enabled == 0 && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],label);
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true);
            peer.Barrier();
            That(peer.CompletedQuestHistory[4+3842] == 1 && fixture.Query(
                "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3842",peer.Id) is [[1L]] &&
                fixture.Query("SELECT map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[70407L]],
                "回城重连恢复3842完成位、唯一交付及物件终末结算");
            string before = State();
            peer.Send(39,passive);
            drain(peer,null);
            That(State() == before,"重选后的迟到52B物件报告不能复活旧实例");
        }

        string State() => CharacterState(fixture,created.Id);
    }

    static void VerifyZeroKill(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        var area = resources.Towns.Areas.Values.First(row=>row.Level<=54 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option=>option.DungeonId==7156));
        var done = new HashSet<int>(area.Quests) { 3446 };
        if (area.AnyQuests.Length>0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row=>row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var candidate = CharacterCreation.Prepare("ZeroKillTCP",resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,54,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(54) },resources.Learning);
            var repository = new CharacterRepository(fixture.Database);
            created = repository.Create(owner,candidate with
                { Level=54,AcceptedStoryLevel=54,CompletedQuests=done.ToArray(),InitialLearning=learning });
            created = repository.CommitInventory(owner,created,new(created.Inventory,created.Inventory with { Gold=10000 },"test_zero_card"));
        }
        byte[] passive = PassiveDeath(69292,74);
        int enabled=0,bossChecks=0;
        byte[]? clearReward=null;
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            var map = FormalDungeonTcpTests.Enter(peer,3447,7156);
            That(U32(map,14)==70184 && map[18]==1 && U32(map,27)==60409 && map[40]==1,
                "3447真实70184保留唯一友方APC60409，不造敌怪");
            FormalDungeonTcpTests.Load(peer); Drain();
            string before = State();
            peer.Send(39,passive); Drain();
            That(State()==before && enabled==0,"未到70185的74B物件报告不能提前清图");
            var request = new byte[100]; request[0]=1;
            peer.Send(45,request); peer.Expect(1,45,[1]); map=peer.Expect(0,29).Body;
            That(U32(map,14)==70185 && map[18]==0,"真实终点70185没有怪物行，不能填充假Boss或死亡");
            FormalDungeonTcpTests.Load(peer); Drain();
            before=State();
            var corrupted = passive.ToArray(); corrupted[^1]^=1;
            peer.Send(39,corrupted); Drain();
            That(State()==before && enabled==0,"终点错误摘要不能保存空死亡scene或发布31");
            peer.Send(39,passive); peer.Expect(0,574,[1,0,0,0,119,13,0,0,0,0]); peer.Expect(0,31,[]); enabled++;
            Drain();
            That(enabled==1 && bossChecks==0 && fixture.Query(
                "SELECT source_type,dungeon_id,map_id,template_id,length(death_events),typeof(death_events) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                is [["PassiveObject",7156L,70185L,69292L,0L,"blob"]] && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "74B真实形状按原scene保存empty BLOB与精确零growth，574后只发空31而无115");
            before=State(); peer.Send(39,passive); Drain();
            That(State()==before && enabled==1,"零击杀物件精确重放不再次保存scene或推进任务");
            var report = DungeonSettlementProtocolTests.Report(peer.Id,99);
            peer.Send(46,report); Drain();
            That(clearReward is { Length:>=218 } && U32(clearReward,85)==0 && fixture.Query(
                "SELECT t.map_id,length(t.death_events),s.operation_id=t.scene_operation_id,t.total_experience,t.base_experience+t.rank_experience FROM character_dungeon_settlement_receipts t JOIN character_dungeon_scene_clear_receipts s ON s.character_id=t.character_id AND s.operation_id=t.scene_operation_id WHERE t.character_id=$p0",peer.Id)
                is [[70185L,0L,1L,long total,long parts]] && total>0 && total==parts+U32(clearReward,49),
                "真实46保留完整非零通关经验、零怪物契约经验及原scene来源，不用kill1进入奖励");
            var frozen = JsonSerializer.Deserialize<DungeonCardReward>((string)fixture.Query(
                "SELECT reward_json FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id)[0][0])!;
            That(frozen.Gold==1 && fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id) is [[0L]],
                "零击杀通关冻结最低金币免费牌，实际选牌前不发资产");
            before=State(); peer.Send(46,report); Drain();
            That(State()==before,"重复零击杀46只回放原结算，不重抽或二次发经验");
            peer.Send(69); peer.Expect(1,69,[1]); peer.Send(70); peer.Expect(1,70); peer.Send(71,[0,0]);
            var selected = peer.Expect(1,71).Body; Drain();
            That(selected[0]==1 && fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id) is [[1L]],
                "零击杀免费牌实际提交领取及库存差量");
            peer.Send(71,[1,0]); var paid=peer.Expect(1,71).Body; peer.ReadCardMembershipState(); Drain();
            That(paid[0]==1 && fixture.Query("SELECT paid_cost,paid_reward_json IS NOT NULL FROM character_dungeon_card_options WHERE character_id=$p0",peer.Id)
                is [[long cost,1L]] && cost==SelfTestCatalogs.CardPrices(resources.Archive).Price(57),
                "零击杀原价付费牌沿真实71合法领取并保存费用，不按kill0拒绝");
            peer.Send(34,[34,0,119,13,255,255,1,0,255,255]); var finished=peer.Expect(1,34).Body; Drain();
            That(finished.Length>=4 && finished[0]==1 && U16(finished,1)==3447 && finished[3]==1,
                "真实3447经34按condition-under-clear分类和独立物件来源交付");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]); peer.Expect(0,24);
            peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

            void Drain()=>drain(peer,packet=>
            {
                if (packet.Opcode==31) enabled++;
                if (packet.Opcode==115) bossChecks++;
                if (packet.Opcode==35) clearReward=packet.Body;
            });
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(peer.CompletedQuestHistory[4+3447]==1 && fixture.Query(
                "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3447",peer.Id) is [[1L]],
                "回城重连恢复3447完成位和一次34，不依赖在线Run");
            string before=State(); peer.Send(39,passive); drain(peer,null);
            That(State()==before,"重连后迟到74B物件报告不能复活旧零击杀实例");
        }
        string State()=>CharacterState(fixture,created.Id);
    }

    static string CharacterState(TcpTestFixture fixture,ushort id)=>fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",id)+
        fixture.Snapshot("SELECT * FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_dungeon_card_rewards WHERE character_id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_dungeon_card_options WHERE character_id=$p0",id)+
        fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",id)+
        fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",id)+
        fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",id)+
        fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",fixture.Options.DevelopmentAccount);

    internal static byte[] PassiveDeath(uint template,int length = 52)
    {
        var body = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,template);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),ushort.MaxValue);
        body[37] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(42),1);
        FormalDungeonTcpTests.SealDeath(body);
        return body;
    }
}
