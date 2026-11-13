using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
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
        VerifyMagtonium(fixture,resources,growth,drain);
        VerifyLayeredCinematic(fixture,resources,growth,drain,dungeons);
        VerifyLayeredCinematic(fixture,resources,growth,drain,dungeons,early:true);
        VerifyLayeredCinematic(fixture,resources,growth,drain,dungeons,escSkip:true);
        VerifyPassiveHunt(fixture,resources,growth,drain,dungeons,3738,102,16120,76530,
            [0,0,7,5,4,6,5,15],[76532,76533,76534]);
        VerifyPassiveHunt(fixture,resources,growth,drain,dungeons,5947,291100293,109034854,292105331,
            [0,4,3,1,3,1,2,4,3],[292105332,292105334,292105333,292105337],
            expectedStartMap:292105331,requiredCount:10);
        VerifyPassiveHunt(fixture,resources,growth,drain,dungeons,5964,291100308,109022849,59594,
            [2,1,1,7,1,1,2,1,2,1],[59598,59600,59601,59602,59603,59599,59595,59594,59596,59597]);
        VerifyPassiveHunt(fixture,resources,growth,drain,dungeons,3646,74,10624,91901,
            [0,8,0,3,3,5,6,0,2],[91902,91904,91909,91905],
            [91901,91908,91902,91903,91909,91904,91905,91906,91907],expectedStartMap:91901);
    }

    // #217：使用真实3735路线和合成74B报告，验证完整掉落、重放及34消费；不把正文附加值当实例号。
    static void VerifyMagtonium(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        const string account = "magtonium-3735";
        var quest = resources.Quests.Get(3735);
        That(QuestFixedRewardDefinition.Parse(quest).ForJob(0) is [{ ItemId:10164801, Count:50 }],
            "3735当前资源交付奖励为同一任务物品50份，不擅自替换为普通魔刹石3341");
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,102);
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            var candidate = CharacterCreation.Prepare("Magtonium",resources.Characters.Get(0),entrance.Position,entrance.Position,resources.Learning.Get);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
            {
                Level = 95, AcceptedStoryLevel = 95, CompletedQuests = entrance.Completed,
                InitialLearning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(95) },resources.Learning)
            });
        }
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,3735,102,row => information = row);
            var maze = dungeons.Get(102).Mazes.Single(row => row.SourceOrdinal == information[7]);
            var route = maze.MapSpecification!;
            FormalDungeonTcpTests.Load(peer); Drain();
            That(quest.Condition == "[seeking]" && quest.IntData.SequenceEqual(new[] {10164801,15}),"3735真实寻物目标为研究用魔刹石15份，不用猎杀33补进度");
            foreach (var scenario in new[] {(Map:76511,Sources:4,Total:4),(Map:76512,Sources:9,Total:13),(Map:76513,Sources:7,Total:15)})
            {
                var target = route.Single(row => row.Id == scenario.Map);
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
                    FormalDungeonTcpTests.Load(peer); Drain();
                }
                That(U32(map,14) == scenario.Map,"3735沿真实连接进入对应MAP，不借用相邻任务路线");
                var room = rooms.Get(target,requirePassiveDefinitions:false);
                var sources = room.PassiveObjects.Select(row => row.ObjectId).Concat(room.SpecialObjects.Select(row => row.ObjectId))
                    .Where(id => id is 16111 or 16112).ToArray();
                That(sources.Length == scenario.Sources,"3735普通放置与特殊父物件的真实资源份数");
                var counters = new Dictionary<int,ushort>();
                foreach (int template in sources)
                {
                    ushort stamp = counters[template] = (ushort)(counters.GetValueOrDefault(template)+1);
                    byte[] body = Report(template,stamp);
                    string before = State();
                    var damaged = body.ToArray(); damaged[^1] ^= 1;
                    peer.Send(39,damaged); Drain();
                    That(State() == before,"物件报告坏摘要不能消费资源槽或发任务材料");
                    int count = Quantity(10164801);
                    peer.Send(39,body); Drain();
                    That(Quantity(10164801) == Math.Min(15,count+1),"同模板不同正文沿原掉落事务逐份入包，并受实际15份持有上限约束");
                    before = State(); peer.Send(39,body); Drain();
                    That(State() == before,"同房精确重放不消费下一放置槽或重复发物品");
                }
                That(Quantity(10164801) == scenario.Total,"三个实机故障房间分别累计4、13、15份材料");
                string complete = State();
                foreach (int template in new[] {16111,16112}) { peer.Send(39,Report(template,300)); Drain(); }
                That(State() == complete,"资源槽耗尽或任务材料已满后，新正文也不能无界发放");
            }
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3735",peer.Id) is [[0L]],
                "15份实际入包才由寻物进度归零");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3); peer.Expect(0,24); Drain();
            string pendingQuest = fixture.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id);
            foreach (uint requested in new uint[] { 3735,0 })
            {
                peer.Send(15,new byte[4]); peer.Expect(1,15,[1]); peer.Expect(0,3); peer.Expect(0,27);
                byte[] selection = Convert.FromHexString("660000000300000000FFFF0000000000970E00000000000000");
                BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),requested);
                peer.Send(16,selection);
                var ordinaryInfo = peer.Expect(0,28).Body;
                var ordinaryMap = peer.Expect(0,29).Body;
                That(ordinaryInfo[7] == 0 && U32(ordinaryMap,14) == 42151,
                    "3735达成未交时显式任务与普通0请求均可用难度3进入真实普通地图");
                FormalDungeonTcpTests.Load(peer); Drain();
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3); peer.Expect(0,24); Drain();
                That(Quantity(10164801) == 15 && pendingQuest == fixture.Snapshot(
                    "SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id) &&
                    fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3735",peer.Id) is [[0L]],
                    "普通高难度入场、加载及退图保留待交付任务与15份材料，不自动交付或奖励");
            }
            peer.Send(34,[34,0,151,14,255,255,1,0,255,255]);
            var ack = peer.Expect(1,34).Body; Drain();
            That(ack[0] == 1 && Quantity(10164801) == 50 && Quantity(3341) == 0 && fixture.Query(
                "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3735",peer.Id) is [[1L]],
                "3735真实34原子消耗15份研究石并只发一次资源声明的同物品50份");
            using var consumed = JsonDocument.Parse((string)fixture.Query(
                "SELECT consumed FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3735",peer.Id)[0][0]);
            That(consumed.RootElement.GetArrayLength() == 1 && consumed.RootElement[0].GetProperty("ItemId").GetInt32() == 10164801 &&
                consumed.RootElement[0].GetProperty("Count").GetInt32() == 15,
                "同物品奖励不能掩盖消费：34回执独立记录实际扣除的原15份");
            string finished = State();
            peer.Send(34,[34,0,151,14,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
            peer.Send(39,Report(16111,301)); Drain();
            That(State() == finished,"交付重放及退出后的旧物件报告不重复材料或奖励");

            void Drain() => drain(peer,null);
        }
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(peer.CompletedQuestHistory[4+3735] == 1 && Quantity(10164801) == 50 && Quantity(3341) == 0,
                "重连恢复3735完成位、消费结果和一次正式奖励");
        }
        int Quantity(int item) => checked((int)(long)fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) " +
            "FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",created.Id,item)[0][0]);
        string State() => CharacterState(fixture,created.Id) + fixture.Snapshot(
            "SELECT scene_event FROM character_quest_lifecycle_receipts WHERE character_id=$p0 ORDER BY revision",created.Id);
        static byte[] Report(int template,ushort value)
        {
            var body = PassiveDeath(checked((uint)template),74);
            // 只制造不同的合法正文；该附加值仍保持未知，不用作客户端演员身份。
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(39),value);
            FormalDungeonTcpTests.SealDeath(body);
            return body;
        }
    }

    // #181/#218/#238/#240：准确任务复用同一热服务；单份、多份及跨房回访通过合成33驱动，不把消费槽解释成实例号。
    static void VerifyPassiveHunt(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain,DungeonCatalog dungeons,
        ushort questId,uint dungeonId,int templateId,int firstMap,int[] copies,int[] targetMaps,int[]? mapOrder=null,
        int? expectedStartMap=null,int requiredCount=15)
    {
        string account = "passive-hunt-"+questId;
        var quest = resources.Quests.Get(questId);
        That(QuestHuntProgress.TryTargets(quest,out var targets) && targets is [{ EnemyType:3 }] && targets[0].Count == requiredCount &&
            targets[0].DungeonId == dungeonId && targets[0].MonsterId == templateId,
            $"{questId} 保留 D{dungeonId} 被动物件的单通道{requiredCount}次真实目标");
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
            var area = resources.Towns.Areas[(entrance.Position.Town,entrance.Position.Area)];
            byte? daybreakRemaining = area.DungeonWorldMap is { } world && resources.Towns.DungeonMaps[world].Options
                .Any(option => option.DungeonId == 530) ? (byte)2 : null;
            var map = FormalDungeonTcpTests.Enter(peer,questId,dungeonId,row => information=row,
                daybreakRemaining:daybreakRemaining);
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
            That(Scalar() == requiredCount,$"{questId}真实31初始化剩余{requiredCount}，入场和加载不推进");
            string before = State();
            if (copies[CurrentMap().Id-firstMap] == 0)
            {
                Trigger(checked((uint)requiredCount));
                That(State() == before,"无目标起始房的33不写条件、资产或事件");
            }
            peer.Send(34,[34,0,(byte)questId,(byte)(questId >> 8),255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == before,$"{questId}进度未完成时34拒绝且不产生奖励");
            uint remaining = checked((uint)requiredCount);
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
            That(remaining == 0 && events.Length == requiredCount && events.All(row => (string)row[0] == acceptance) &&
                sources.All(row => row.DungeonId == dungeonId && row.TemplateId == templateId) &&
                sources.GroupBy(row => row.MapId).All(group => expectedPerMap.GetValueOrDefault(group.Key) == group.Count()) &&
                sources.Select(row => row.OperationId).Distinct().Count() == requiredCount &&
                sources.Select(row => (row.MapId,row.SourceOrdinal)).Distinct().Count() == requiredCount &&
                sources.Select(row => row.Identity.InstanceId).Distinct().Count() == 1,
                $"{requiredCount}次事务沿原接取及同Run保存独立消费槽与操作回执，不把重复正文当实例号");

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
                is [[0L,string restored]] && restored == acceptance,$"断线重登保存原接取及{requiredCount}次已提交进度");
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

    // 一个跨层成功代表：真实70201基础Boss、70487末层、独立39/33（含ESC跳过只发33）和34；事件正文仍明确合成。
    static void VerifyLayeredCinematic(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain, DungeonCatalog dungeons, bool early = false, bool escSkip = false)
    {
        string account = early ? "layered-movie-3858-early" : escSkip ? "layered-movie-3858-esc" : "layered-movie-3858";
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,resources.Quests.Get(3858),193);
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            var candidate = CharacterCreation.Prepare(early ? "LayerMovieE" : escSkip ? "LayerMovieS" : "LayerMovie",resources.Characters.Get(0),
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
            RejectExit(path[^2],0);
            foreach (var layer in maze.LayeredMaps.Where(row => row.Position == path[^1]))
            {
                Move(layer.Position,1);
                That(U32(map,14) == layer.Id,"3858原叠层顺序保留实际地图身份");
                if (early && layer.Id == 70487)
                {
                    peer.Send(33,[33,0,18,15,0,0]); peer.Expect(1,33,[1,18,15,0,0,0,0]); Drain();
                    That(Scalar() is [[0L]] && enabled == 0 && fixture.Query(
                        "SELECT count(*) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                        "3858进入目标层的33先于37保存演出条件，不代替物件39或签发31");
                }
                FormalDungeonTcpTests.Load(peer); Drain();
            }
            That(U32(map,14) == 70487 && map[18] == 3 && U32(map,27) == 56501 && map[32] == 5 && map[40] == 1 &&
                U32(map,49) == 68004 && map[54] == 9 && map[62] == 1 &&
                U32(map,71) == 68004 && map[76] == 9 && map[84] == 1 &&
                new[] { U16(map,25), U16(map,47), U16(map,69) }.Distinct().Count() == 3 &&
                enabled == (early ? 1 : 0) && Scalar() is [[long scalar]] && scalar == (early ? 0 : 1),
                "真实70487保留独立对象；早到终末在加载后补交清图");
            string saved = State();
            if (!early) { RejectExit(path[^2],0); RejectExit(path[^1],1); }
            if (!early)
            {
                peer.Send(34,[34,0,18,15,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
                That(State() == saved,"3858未收到演出终末时34拒绝且不写奖励");
            }
            if (escSkip || early)
            {
                if (!early) { peer.Send(33,[33,0,18,15,0,0]); peer.Expect(1,33,[1,18,15,0,0,0,0]); Drain(); }
                That(enabled == 1 && bossChecks == 0 && Scalar() is [[0L]] && fixture.Query(
                    "SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3858",peer.Id)[0][0].Equals(acceptance) &&
                    fixture.Query("SELECT source_type,dungeon_id,map_id,template_id,length(death_events)/16,quest_id FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                    is [["PassiveObject",193L,70487L,69292L,long escDeaths,0L]] && escDeaths == deaths,
                    "3858仅发一次33时，提交原AcceptanceId及69292场景清图并签发空31");
                saved = State();
                peer.Send(33,[33,0,18,15,0,0]); peer.Expect(1,33,[1,18,15,0,0,0,0]); Drain();
                peer.Send(39,passive); Drain();
                That(State() == saved && enabled == 1,"3858跳过动画清图后的重复33或迟到39不重复场景或结算许可");
            }
            else
            {
                peer.Send(39,passive); peer.Expect(0,31,[]); enabled++; Drain();
                That(enabled == 1 && bossChecks == 0 && Scalar() is [[long sceneScalar]] && sceneScalar == (early ? 0 : 1) && fixture.Query(
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
            }
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
            var originalDaily = fixture.Query("SELECT condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=13053",peer.Id);
            That(originalDaily is [[long,string]],"86级原地区每日仍在，升级不能撤销原接取");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356);
            int dailyNotices=0;
            drain(peer,packet =>
            {
                if(packet.Opcode!=574) return;
                Bytes(packet.Body,Build(writer=> { writer.Write(2u); writer.Write((ushort)13053); writer.Write(checked((uint)(long)originalDaily[0][0])); writer.Write((ushort)13056); writer.Write(1686u); }),
                    "升到87级回城后完整574保留原地区每日并分配新每日，不伪造31接取成功");
                dailyNotices++;
            });
            That(dailyNotices==1 && fixture.Query("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",peer.Id) is [[13053L,long oldState,string oldId],[13056L,1686L,string]] && oldState == (long)originalDaily[0][0] && oldId == (string)originalDaily[0][1],
                "3858奖励升级后仅分配一次真实每日，原剧情任务已经交付");

            void Move(DungeonPoint point,byte kind)
            {
                var request = new byte[100]; request[0] = checked((byte)point.X); request[1] = checked((byte)point.Y); request[10] = kind;
                peer.Send(45,request); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
            }
            void RejectExit(DungeonPoint point,byte kind)
            {
                string before = State();
                var request = new byte[100]; request[0] = checked((byte)point.X); request[1] = checked((byte)point.Y); request[10] = kind;
                peer.Send(45,request); peer.Expect(1,45,[0,19,0]);
                drain(peer,packet => That(packet.Opcode != 29,"3858待物件终末的拒绝出门不能发布换图"));
                That(State() == before && enabled == 0,"3858基础Boss或末层物件未完成时拒绝出门，存档与结算许可不变");
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
        QuestDailyCoexistence? daily=null;
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            daily=new(fixture,created.Id,13037,1636);
            That(fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=13037",created.Id) is [[0L]],"54级地区每日保留原0槽");
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
            peer.Send(39,passive); peer.Expect(1,33,Convert.FromHexString("01770D00000000")); peer.Expect(0,31,[]); enabled++;
            Drain();
            That(enabled==1 && bossChecks==0 && fixture.Query(
                "SELECT source_type,dungeon_id,map_id,template_id,length(death_events),typeof(death_events) FROM character_dungeon_scene_clear_receipts WHERE character_id=$p0",peer.Id)
                is [["PassiveObject",7156L,70185L,69292L,0L,"blob"]] && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "74B真实形状按原scene保存empty BLOB与精确零growth，3447原位33后只发空31而无115");
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
        string State() { daily?.Check(); return CharacterState(fixture,created.Id); }
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
