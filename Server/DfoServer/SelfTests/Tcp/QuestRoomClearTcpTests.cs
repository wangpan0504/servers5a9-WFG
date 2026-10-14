using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestRoomClearTcpTests
{
    // 借用quests已启动服务与共享资源；真实3525/3560资源、生产Handler和合成普通39，不代表Windows演出验收。
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        var dungeons = DungeonCatalog.Load(resources.Archive);
        foreach (bool ordinary in new[] { false,true }) VerifyCase(fixture,resources,growth,drain,dungeons,ordinary);
    }

    static void VerifyCase(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain,DungeonCatalog dungeons,bool ordinary)
    {
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 65 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 82));
        var done = new HashSet<int>(area.Quests) { 3145,4873,3524,3558,3559 };
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            // 只准备真实入口等级和前置；父子接取、条件、完成及奖励全部走TCP。
            var candidate = CharacterCreation.Prepare(ordinary ? "RoomZero" : "RoomQuest",
                resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,65,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(65) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level = 65,AcceptedStoryLevel = 65,CompletedQuests = done.ToArray(),InitialLearning = learning });
        }
        byte[] lastDeath = [];
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id,"中途清房选角绑定本条实际角色");
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,3525,82,row => information = row,
                requestQuestId: ordinary ? (ushort)0 : (ushort)3525);
            var maze = dungeons.Get(82).Mazes.Single(row => row.SourceOrdinal == information[7]);
            var target = maze.MapSpecification!.Single(row => row.Id == 92247);
            var boss = maze.MapSpecification!.Single(row => row.Id == 92251);
            That(information[8] == boss.Position.X && information[9] == boss.Position.Y && target.Position != boss.Position &&
                fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",peer.Id) is [[3525L,1L]],
                "3525及普通0保留真实92251终点，入场规划不造3560接取");
            FormalDungeonTcpTests.Load(peer);
            int enabled = 0,bossChecks = 0,deathCount = 0;
            bool parentZeroPublished = false,childCompletePublished = false;
            var dead = new HashSet<ushort>();
            Drain();
            peer.Send(31,QuestRequest(31,3560,[]));
            peer.Expect(1,31,[1,232,13,1,0,0,0,0]);
            Drain();
            var acceptance = (string)fixture.Query(
                "SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3560",peer.Id).Single().Single();
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),target.Position);
            foreach (var next in path.Skip(1)) { KillRoom(); Move(next); }
            That(U32(map,14) == 92247 && map[18] == 7 &&
                Enumerable.Range(0,7).Count(at => U32(map,27+22*at) == 61801 && map[40+22*at] == 0) == 6 &&
                Enumerable.Range(0,7).Any(at => U32(map,27+22*at) == 10448 && map[40+22*at] == 1),
                "真实92247发送完整六只61801与非阻挡友方10448，不造Boss或删除演员");
            var enemies = Enumerable.Range(0,map[18]).Select(at => 19+22*at).Where(at => map[at+21] == 0).ToArray();
            foreach (int offset in enemies[..^1]) Kill(offset);
            That(enabled == 0 && bossChecks == 0 && !parentZeroPublished && !childCompletePublished && fixture.Query(
                "SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3560",peer.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "目标房少一敌人仍不能清图、推进父任务或发送115/31");
            lastDeath = FormalDungeonTcpTests.Death(U16(map,enemies[^1]+6),peer.Id,map[enemies[^1]+12]);
            Kill(enemies[^1]);
            That(enabled == 0 && bossChecks == 0 && parentZeroPublished && childCompletePublished && fixture.Query(
                "SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",peer.Id) is [[3525L,0L]] &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3560",peer.Id) is [[3560L]] &&
                fixture.Query("SELECT quest_id,experience_amount FROM character_quest_completion_receipts WHERE character_id=$p0",peer.Id) is [[3560L,0L]],
                "第六敌人死亡只清92247，生产39自动交付无奖励3560并推进真实父任务，不提前通关");
            var clearRows = fixture.Query("""
                SELECT r.acceptance_id,r.dungeon_id,r.map_id,r.epoch,g.epoch,g.quest_id
                FROM character_quest_clear_receipts r JOIN character_dungeon_growth_receipts g
                ON g.character_id=r.character_id AND g.event_id=r.growth_event_id
                WHERE r.character_id=$p0 AND r.quest_id=3560
                """,peer.Id);
            That(clearRows is [[string accepted,82L,92247L,long clearEpoch,long growthEpoch,long instanceQuest]] &&
                accepted == acceptance && clearEpoch == growthEpoch+1 && instanceQuest == (ordinary ? 0 : 3525) &&
                fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[long savedDeaths]] && savedDeaths == deathCount &&
                fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "中途清图严格绑定原接取及growth epoch+1，普通0保留实际实例请求号和完整死亡集合");
            string cleared = State();
            peer.Send(39,lastDeath); Drain();
            That(State() == cleared && enabled == 0 && bossChecks == 0,"RoomClear确认后的重复39仅忽略，不重发进度、奖励或结算");
            peer.Send(33,QuestRequest(33,3525,[0,0]));
            peer.Expect(1,33,[1,197,13,0,0,0,0]); Drain();
            That(State() == cleared,"父33读取实际子完成后的归零状态，不伪造任务条件");
            peer.Send(34,QuestRequest(34,3560,[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == cleared,"已自动归档子任务的迟到34不再次发奖");
            var toBoss = QuestStoryRouteTests.Path(maze.StaticTopology,target.Position,boss.Position);
            foreach (var next in toBoss.Skip(1)) { KillRoom(); Move(next); }
            That(U32(map,14) == 92251 && enabled == 0 && bossChecks == 0,"中途任务完成后继续原92251，加载Boss仍不提前结算");
            KillRoom();
            That(enabled == 1 && bossChecks == 1,"实际Boss死亡才沿原终点115/31许可结算");
            var report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
            peer.Send(46,report); Drain();
            That(fixture.Query("SELECT quest_id,map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                is [[long settledQuest,92251L]] && settledQuest == (ordinary ? 0 : 3525),"真实46只按原92251终点提交结算奖励");
            string settled = State();
            peer.Send(46,report); Drain();
            That(State() == settled,"重复46不增加结算经验或翻牌资产");
            peer.Send(34,QuestRequest(34,3525,[255,255,1,0,255,255]));
            var finished = peer.Expect(1,34).Body;
            That(finished.Length >= 4 && finished[0] == 1 && U16(finished,1) == 3525,"真实34交付已完成全部子项的3525父任务");
            Drain();
            string completed = State();
            peer.Send(34,QuestRequest(34,3525,[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == completed,"重复父34不再次授予任务奖励");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

            void Drain() => drain(peer,packet =>
            {
                if (packet.Opcode == 31) enabled++;
                if (packet.Opcode == 115) bossChecks++;
                if (packet.Opcode == 574)
                {
                    var body = packet.Body;
                    That(body.Length >= 4 && body.Length == 4+6*U32(body,0),"中途任务574独立消费完整活动条件列表");
                    for (int at = 4; at < body.Length; at += 6)
                        if (U16(body,at) == 3525 && U32(body,at+2) == 0) parentZeroPublished = true;
                }
                if (packet.Opcode == 356 && packet.Body.Length > 4+3560 && packet.Body[4+3560] == 1)
                    childCompletePublished = true;
            });
            void Kill(int offset)
            {
                ushort key = U16(map,offset+6);
                if (!dead.Add(key)) return;
                peer.Send(39,FormalDungeonTcpTests.Death(key,peer.Id,map[offset+12])); Drain(); deathCount++;
            }
            void KillRoom()
            {
                foreach (int offset in Enumerable.Range(0,map[18]).Select(at => 19+22*at).Where(at => map[at+21] == 0)) Kill(offset);
            }
            void Move(DungeonPoint point)
            {
                var request = new byte[100]; request[0] = checked((byte)point.X); request[1] = checked((byte)point.Y);
                peer.Send(45,request); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                FormalDungeonTcpTests.Load(peer); Drain();
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(peer.Id == created.Id,"中途清房重连仍绑定原角色");
            That(peer.CompletedQuestHistory[4+3525] == 1 && peer.CompletedQuestHistory[4+3560] == 1 && fixture.Query(
                "SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",peer.Id) is [[3525L],[3560L]] &&
                fixture.Query("SELECT map_id FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[92247L]] &&
                fixture.Query("SELECT map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[92251L]],
                "重连恢复真实父子完成、唯一中途凭据与独立原Boss结算");
            string before = State(); peer.Send(39,lastDeath); drain(peer,null);
            That(State() == before,"重登后的迟到死亡不能借旧Run再次清房");
        }

        string State() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
            fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY combat_sequence",created.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id)+
            fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",created.Id)+
            fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",fixture.Options.DevelopmentAccount);
    }

    static byte[] QuestRequest(byte command,ushort quest,ReadOnlySpan<byte> tail)
    {
        var body = new byte[4+tail.Length]; body[0] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),quest); tail.CopyTo(body.AsSpan(4)); return body;
    }
}
