using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
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
    // 借用quests已启动服务与共享资源；真实顺序父/末子资源、生产Handler和合成普通39，不代表Windows演出验收。
    // 3525/362、3540/57、3494/362 覆盖图内与结算父34拒绝、回城后交付；3492无父NPC沿结算父33归档末子。
    // 3578/1276、3146/1247 不在城镇地图，结算34仍按子完成集交付。
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        bool TownNpc(uint id) => resources.Towns.Areas.Values.Any(area =>
            area.Default.Npcs.Any(npc => npc.Id == id) ||
            area.Phases.Any(phase => phase.Npcs.Any(npc => npc.Id == id)));
        That(TownNpc(58) && TownNpc(362) && TownNpc(57) && TownNpc(76) &&
            !TownNpc(1247) && !TownNpc(1276),
            "顺序父城镇交付NPC在城镇地图，3146/3578对话NPC不在");
        foreach (bool ordinary in new[] { false,true }) VerifyCase(fixture,resources,growth,drain,dungeons,ordinary);
        foreach (bool ordinary in new[] { false,true }) VerifyGrandine(fixture,resources,growth,drain,dungeons,ordinary);
        foreach (bool ordinary in new[] { false,true }) VerifySeaTrain(fixture,resources,growth,drain,dungeons,ordinary);
        foreach (bool ordinary in new[] { false,true }) VerifyTownNpcParent(fixture,resources,growth,drain,dungeons,ordinary,
            3540,3564,3565,83,92360,"night");
        foreach (bool ordinary in new[] { false,true }) VerifyTownNpcParent(fixture,resources,growth,drain,dungeons,ordinary,
            3494,3817,3818,163,91599,"gbl");
        foreach (bool ordinary in new[] { false,true }) VerifyTownNpcParent(fixture,resources,growth,drain,dungeons,ordinary,
            3492,3510,3511,163,91577,"gblsec");
        QuestDarkElfGuardTcpTests.Verify(fixture,resources,growth,drain,dungeons);
        QuestHarlemRidingTcpTests.Verify(fixture,resources,growth,drain);
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
        QuestDailyCoexistence? daily = null;
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id,"中途清房选角绑定本条实际角色");
            daily = new(fixture,created.Id,13041,1636,progress:true);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,3525,82,row => information = row,
                requestQuestId: ordinary ? (ushort)0 : (ushort)3525);
            var maze = dungeons.Get(82).Mazes.Single(row => row.SourceOrdinal == information[7]);
            var target = maze.MapSpecification!.Single(row => row.Id == 92247);
            var boss = maze.MapSpecification!.Single(row => row.Id == 92251);
            That(information[8] == boss.Position.X && information[9] == boss.Position.Y && target.Position != boss.Position &&
                daily!.OtherRows("quest_id,condition_state") is [[3525L,1L]],
                "3525及普通0保留真实92251终点，入场规划不造3560接取");
            FormalDungeonTcpTests.Load(peer);
            int enabled = 0,bossChecks = 0,deathCount = 0;
            bool parentZeroPublished = false,childCompletePublished = false,clearConditionPublished = false;
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
            That(enabled == 0 && bossChecks == 0 && !parentZeroPublished && !childCompletePublished && clearConditionPublished &&
                daily!.OtherRows("quest_id,condition_state") is [[3525L,1L],[3560L,0L]] &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3560",peer.Id).Length == 0 &&
                fixture.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0",peer.Id).Length == 0,
                "第六敌人死亡只达成92247条件并原位33，保留NPC子任务，不推进父领奖或提前通关");
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
            peer.Send(34,QuestRequest(34,3525,[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == cleared && daily!.OtherRows("quest_id,condition_state") is [[3525L,1L],[3560L,0L]] &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3525",peer.Id).Length == 0,
                "92247尚未结算翻牌且没有CompleteNpc 362，不能在图内提前交付父任务");
            peer.Send(39,lastDeath); Drain();
            That(State() == cleared && enabled == 0 && bossChecks == 0,"RoomClear确认后的重复39仅忽略，不重发进度、奖励或结算");
            peer.Send(33,QuestRequest(33,3525,[0,0]));
            peer.Expect(1,33,[1,197,13,1,0,0,0]); Drain();
            That(State() == cleared,"父33保留尚待NPC交付的末子，不提前归零");
            peer.Send(34,QuestRequest(34,3560,[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == cleared,"结算前子34不关闭NPC任务或发奖");
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
            long goldBefore = (long)fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id).Single()[0];
            peer.Send(34,QuestRequest(34,3525,[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == settled && fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3525",peer.Id).Length == 0,
                "92251没有CompleteNpc 362，结算中不能交付父任务");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            peer.Send(34,QuestRequest(34,3525,[255,255,1,0,255,255]));
            var finished = peer.Expect(1,34).Body;
            That(finished.Length >= 4 && finished[0] == 1 && U16(finished,1) == 3525,"回城后真实34交付已完成全部子项的3525父任务");
            Drain();
            That((long)fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id).Single()[0] > goldBefore &&
                fixture.Query("SELECT quest_id,experience_amount FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",peer.Id)
                    is [[3525L,long parentExp]] && parentExp > 0 &&
                fixture.Query("SELECT json_extract(plan_snapshot,'$.TerminalClear.Clear.MapId') FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3525",peer.Id)
                    is [[92247L]],
                "回城父34原子发放经验金币并归档3560，唯一父回执保留92247实际清图凭据");
            string completed = State();
            peer.Send(34,QuestRequest(34,3525,[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == completed,"重复父34不再次授予任务奖励");

            void Drain() => drain(peer,packet =>
            {
                if (packet is { Kind: 1, Opcode: 33 } && U16(packet.Body,1) == 3560)
                {
                    That(U32(packet.Body,3) == 0,"中途清图原位33使用3560的真实零条件");
                    clearConditionPublished = true;
                }
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
                "SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",peer.Id) is [[3525L]] &&
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

    // 实际3753/D104资源链；39仅为独立编码的合法合成输入，不代表ACT/CMT自然送包或客户端验收。
    static void VerifyGrandine(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain,DungeonCatalog dungeons,bool ordinary)
    {
        var quest = resources.Quests.Get(3753);
        That(quest.Path == "n_quest/quest_renewal/epic_quest/slough/slough_epic_32.qst" &&
            quest.Condition == "[clear map]" && quest.SubType == -1 && quest.IntData.SequenceEqual(new[] { 76648 }) &&
            quest.MinimumLevel == 83 && quest.MaximumLevel == 99 && quest.Prerequisites.SequenceEqual(new[] { 3752 }) &&
            quest.Npc == 77 && quest.CompleteNpc == 76 && quest.Definition!.Dungeons.Any(row => row.DungeonId == 104),
            "3753开路使用真实83级前置3752、76648清图及D104声明");
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 83 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(Eligible));
        var option = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(Eligible);
        var done = new HashSet<int>(area.Quests) { 3145,4873,3752 };
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests.First(id => id != 3753));
        if (!option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
        done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(3753)).Quests);
        done.Remove(3753);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        string account = ordinary ? "grandine0" : "grandine3753";
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            // 仅准备等级和入口前置；3753接取、进度、清图凭据与交付均由真实TCP产生。
            var candidate = CharacterCreation.Prepare(ordinary ? "GrandineZero" : "GrandineQ",
                resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,83,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(83) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level = 83,AcceptedStoryLevel = 83,CompletedQuests = done.ToArray(),InitialLearning = learning });
        }
        ushort requested = ordinary ? (ushort)0 : (ushort)3753;
        byte[] lastDeath = [];
        using (var peer = Open(fixture,account))
        {
            peer.Select(0,prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id && fixture.Query(
                "SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",peer.Id) is [[13050L,1666L]],
                "3753独立账号未预写活动任务或完成状态");
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,3753,104,row => information = row,requestQuestId:requested);
            var active = fixture.Query("SELECT slot,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3753",peer.Id).Single();
            long questSlot = (long)active[0]; string acceptance = (string)active[2];
            That((long)active[1] == 1 && acceptance.Length != 0,"实际31接取3753生成唯一待清图槽及接取身份");
            var maze = dungeons.Get(104).Mazes.Single(row => row.SourceOrdinal == information[7]);
            var target = maze.MapSpecification!.Single(row => row.Id == 76648);
            var boss = maze.MapSpecification!.Single(row => row.Id == 76649);
            var targetRoom = SelfTestCatalogs.Rooms(resources.Archive).Get(target);
            That(targetRoom.Monsters is [{ MonsterId:61273 }] && target.Position != boss.Position &&
                information[8] == boss.Position.X && information[9] == boss.Position.Y,
                "真实76648唯一monster ordinal0为61273，D104保留76649原Boss终点");
            int enabled = 0,bossChecks = 0,deathCount = 0;
            bool zeroPublished = false;
            var dead = new HashSet<ushort>();
            FormalDungeonTcpTests.Load(peer); Drain();
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),target.Position);
            foreach (var next in path.Skip(1)) { KillRoom(); Move(next); }
            That(map.Length >= 19+22*map[18] && U32(map,14) == 76648,"本次正式Run到达实际76648并保留完整NOTI29演员行");
            int actor = Enumerable.Range(0,map[18]).Select(at => 19+22*at).Single(at => U32(map,at+8) == 61273);
            ushort key = U16(map,actor+6);
            That(U32(map,actor+2) == 0 && map[actor+21] == 0 && key != peer.Id,
                "61273以真实ordinal0敌对行发布；死亡key仅取本次NOTI29行+6，不借模板号或其他演员");
            string before = State();
            peer.Send(34,QuestRequest(34,3753,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == before && !zeroPublished && enabled == 0 && bossChecks == 0 && fixture.Query(
                "SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3753",peer.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "61273未退场时不能提前34、清图归零或产生原Boss结算许可");
            lastDeath = GrandineRetirement(key,peer.Id);
            peer.Send(39,lastDeath); Drain(); deathCount++;
            That(zeroPublished && enabled == 0 && bossChecks == 0 && fixture.Query(
                "SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3753",peer.Id) is [[0L]] &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3753",peer.Id).Length == 0,
                "61273的普通count0完整39推进3753并发布574归零，保持手动交付及原终点");
            var clear = fixture.Query("""
                SELECT r.acceptance_id,r.quest_slot,r.dungeon_id,r.map_id,r.epoch,g.epoch,g.quest_id,
                  r.instance_id,g.instance_id,r.task_attempt,g.task_attempt,g.monster_id
                FROM character_quest_clear_receipts r JOIN character_dungeon_growth_receipts g
                ON g.character_id=r.character_id AND g.event_id=r.growth_event_id
                WHERE r.character_id=$p0 AND r.quest_id=3753
                """,peer.Id);
            That(clear is [[string accepted,long slot,104L,76648L,long clearEpoch,long growthEpoch,long instanceQuest,
                    string instance,string growthInstance,long attempt,long growthAttempt,61273L]] &&
                accepted == acceptance && slot == questSlot && clearEpoch == growthEpoch+1 && instanceQuest == requested &&
                instance == growthInstance && attempt == growthAttempt && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[long savedDeaths]] &&
                savedDeaths == deathCount && fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "3753清图凭据绑定原接取、槽、D104/76648、61273成长事件及同实例attempt，不伪造整副本结算");
            That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id)
                    is [[long currentAttempt]] && currentAttempt == (long)clear[0][9],
                "3753清图来源属于当前持久化尝试，不仅是两份历史回执彼此相等");
            before = State(); peer.Send(39,lastDeath); Drain();
            That(State() == before && enabled == 0 && bossChecks == 0,"重复61273退场不重写清图、成长或资产");
            peer.Send(33,QuestRequest(33,3753,[0,0])); peer.Expect(1,33,[1,169,14,0,0,0,0]); Drain();
            That(State() == before,"真实33只读取3753已持久化的归零进度");
            peer.Send(34,QuestRequest(34,3753,[255,255,1,0,255,255]));
            var finished = peer.Expect(1,34).Body;
            That(finished.Length >= 4 && finished[0] == 1 && U16(finished,1) == 3753,"实际34交付开路3753"); Drain();
            That(fixture.Query("SELECT quest_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3753",peer.Id).Length == 0 &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3753",peer.Id) is [[3753L]] &&
                fixture.Query("""
                    SELECT acceptance_id,instance_id,task_attempt,epoch,rewards
                    FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3753
                    """,peer.Id) is [[string completedAcceptance,string completedInstance,long completedAttempt,long completedEpoch,"[]"]] &&
                completedAcceptance == acceptance && completedInstance == (string)clear[0][7] &&
                completedAttempt == (long)clear[0][9] && completedEpoch == (long)clear[0][4],
                "3753唯一34回执保留本次清图来源及接取身份，真实0/0物品声明不生成物品奖励");
            before = State();
            peer.Send(34,QuestRequest(34,3753,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
            peer.Send(39,lastDeath); Drain();
            That(State() == before,"交付后重复34及迟到39不再次修改凭据或资产");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
            peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

            void Drain() => drain(peer,packet =>
            {
                if (packet.Opcode == 31) enabled++;
                if (packet.Opcode == 115) bossChecks++;
                if (packet.Opcode != 574) return;
                var body = packet.Body;
                That(body.Length >= 4 && body.Length == 4+6*U32(body,0),"3753完整活动条件通知574");
                for (int at = 4; at < body.Length; at += 6)
                    if (U16(body,at) == 3753 && U32(body,at+2) == 0) zeroPublished = true;
            });
            void KillRoom()
            {
                foreach (int offset in Enumerable.Range(0,map[18]).Select(at => 19+22*at).Where(at => map[at+21] == 0 && map[at+13] != 9))
                {
                    ushort monsterKey = U16(map,offset+6);
                    if (!dead.Add(monsterKey)) continue;
                    peer.Send(39,FormalDungeonTcpTests.Death(monsterKey,peer.Id,map[offset+12])); Drain(); deathCount++;
                }
            }
            void Move(DungeonPoint point)
            {
                var request = new byte[100]; request[0] = checked((byte)point.X); request[1] = checked((byte)point.Y);
                peer.Send(45,request); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                FormalDungeonTcpTests.Load(peer); Drain();
            }
        }
        using (var peer = Open(fixture,account))
        {
            peer.Select(0,prepared:true); peer.Barrier();
            That(peer.Id == created.Id && peer.CompletedQuestHistory[4+3753] == 1 && fixture.Query(
                "SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3753",peer.Id) is [[3753L]] &&
                fixture.Query("SELECT map_id FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=3753",peer.Id) is [[76648L]],
                "重登回放3753完成及唯一76648清图、交付凭据");
            string before = State(); peer.Send(39,lastDeath); drain(peer,null);
            That(State() == before,"重登后的旧Run61273退场不能重放清图或发奖");
        }
        bool Eligible(DfoServer.GameWorld.Towns.TownDungeonOption row) => row.DungeonId == 104 &&
            (row.QuestId == -1 || row.InProgress && row.QuestId == 3753 || !row.InProgress && row.QuestId != 3753);
        string State() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
            fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY combat_sequence",created.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id)+
            fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",created.Id)+
            fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",account);
    }

    static void VerifySeaTrain(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain,DungeonCatalog dungeons,bool ordinary)
    {
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,resources.Quests.Get(3578),87);
        string account = ordinary ? "seatrain-zero" : "seatrain-parent";
        byte job = ordinary ? (byte)6 : (byte)15, grow = ordinary ? (byte)18 : (byte)20;
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            var candidate = CharacterCreation.Prepare(ordinary ? "TrainZero" : "TrainQuest",resources.Characters.Get(job),
                entrance.Position,entrance.Position,resources.Learning.Get);
            var free = resources.Learning.FreeSkills(job,grow);
            var learning = SkillLearning.Initialize(job,free,resources.Learning).State!;
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
            {
                Level = 73, GrowType = grow, AcceptedStoryLevel = 73, Skills = free.ToArray(),
                CompletedQuests = entrance.Completed,
                InitialLearning = SkillPointLedger.Synchronize(job,grow,73,
                    learning with { Experience = growth.GetLevelStartExperience(73) },resources.Learning)
            });
        }
        byte[] lastDeath = [];
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,3578,87,row => information = row,
                requestQuestId: ordinary ? (ushort)0 : (ushort)3578);
            FormalDungeonTcpTests.Load(peer); Drain();
            That(U32(map,14) == 57858,"死灵破坏武器从真实57858起始房进入");
            peer.Send(31,QuestRequest(31,3607,[])); peer.Expect(1,31,[1,23,14,1,0,0,0,0]); Drain();
            string before = State();
            peer.Send(31,QuestRequest(31,3607,[])); peer.Expect(1,31,[0,18,0]); Drain();
            peer.Send(33,QuestRequest(33,3607,[0,0])); peer.Expect(1,33,[0,19,0]); Drain();
            That(State() == before,"已接取拒绝仍保留，未清房的33不能凭客户端值推进");
            var enemies = Enumerable.Range(0,map[18]).Select(at => 19+22*at).Where(at => map[at+21] == 0).ToArray();
            That(enemies.Length == 3 && map[18] == 4,"首房保留三只敌人与一个非阻挡友方演员");
            foreach (int offset in enemies[..^1]) Kill(offset);
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3607",created.Id) is [[1L]],
                "首房少一敌人仍不能清3607");
            lastDeath = FormalDungeonTcpTests.Death(U16(map,enemies[^1]+6),peer.Id,map[enemies[^1]+12]);
            Kill(enemies[^1]);
            That(fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id IN (3578,3607) ORDER BY quest_id",created.Id) is [[3578L,1L]] &&
                fixture.Query("SELECT quest_id,map_id FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id) is [[3607L,57858L]],
                "最后敌人死亡自动归档3607，父任务仅剩第二阶段，保存真实清房凭据");
            before = State();
            peer.Send(39,lastDeath); Drain();
            peer.Send(33,QuestRequest(33,3607,[0,0])); peer.Expect(1,33,[0,19,0]); Drain();
            That(State() == before,"自动归档后的重复死亡及迟到33不重复推进或发奖");
            peer.Send(31,QuestRequest(31,3608,[])); peer.Expect(1,31,[1,24,14,1,0,0,0,0]); Drain();
            var maze = dungeons.Get(87).Mazes.Single(row => row.SourceOrdinal == information[7]);
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),new(information[8],information[9]));
            foreach (var next in path.Skip(1))
            {
                var move = new byte[100]; move[0] = checked((byte)next.X); move[1] = checked((byte)next.Y);
                peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                FormalDungeonTcpTests.Load(peer); Drain();
                foreach (int offset in Enumerable.Range(0,map[18]).Select(at => 19+22*at).Where(at => map[at+21] == 0)) Kill(offset);
            }
            That(U32(map,14) == 57866,"首房完成后仍沿原路线到57866终点");
            peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); Drain();
            before = State();
            peer.Send(34,QuestRequest(34,3578,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == before,"海上列车末子真实清图后，免费翻牌前不能交付父奖励");
            peer.Send(69); peer.Expect(1,69,[1]); peer.Send(70); peer.Expect(1,70);
            peer.Send(71,[0,0]); peer.Expect(1,71); Drain();
            peer.Send(34,QuestRequest(34,3578,[255,255,1,0,255,255]));
            var finished = peer.Expect(1,34).Body;
            That(finished[0] == 1 && U16(finished,1) == 3578,"死灵在第二子任务真实完成后交付破坏武器父任务");
            Drain(); before = State();
            peer.Send(34,QuestRequest(34,3578,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == before,"父任务重复交付不重复发奖");

            void Drain() => drain(peer,null);
            void Kill(int offset)
            {
                peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12])); Drain();
            }
        }
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(new[] {3578,3607,3608}.All(id => peer.CompletedQuestHistory[4+id] == 1) && fixture.Query(
                "SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id) is [[3578L],[3607L]] &&
                fixture.Query("SELECT json_extract(plan_snapshot,'$.TerminalClear.Clear.MapId') FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3578",created.Id)
                    is [[57866L]],
                "重连恢复父子三个实际完成，父回执合并末清图子事实，首阶段保留独立零奖励回执");
        }
        string State() => fixture.Snapshot("SELECT revision,experience,gold FROM characters WHERE id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
            fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
            fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY combat_sequence",created.Id);
    }

    // 3540/3494回城交付；3492翻牌后父33归零并在结算交付。目标图只从入场 maze.MapSpecification 取。
    static void VerifyTownNpcParent(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain,DungeonCatalog dungeons,bool ordinary,
        int parent,int previousChild,int child,uint dungeon,int targetMap,string tag)
    {
        var parentQuest = resources.Quests.Get(parent);
        var childQuest = resources.Quests.Get(child);
        // #972：带 CompleteNpc 的末清图子（如 3818）保留到结算翻牌后对话，回城父34一并关闭；无 CompleteNpc 的末子（如 3565）清图即归档。
        bool dialogueChild = QuestRelationProgress.ClearDialogueChild(childQuest,resources.Quests);
        That(QuestRelationProgress.SequentialParent(parentQuest,resources.Quests) &&
            parentQuest.IntData.Contains(previousChild) && parentQuest.IntData.Contains(child) &&
            childQuest.Condition == "[clear map]" && childQuest.IntData[0] == targetMap &&
            childQuest.Definition!.Dungeons.Any(row => row.DungeonId == dungeon),
            "顺序父保留真实子任务与末子清图目标：" + parent);
        byte level = checked((byte)parentQuest.MinimumLevel);
        var area = resources.Towns.Areas.Values.First(row => row.Level <= level && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon));
        var done = new HashSet<int>(area.Quests) { 3145,4873,previousChild };
        done.UnionWith(parentQuest.Prerequisites);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        string account = ordinary ? tag + "-zero" : tag + "-parent";
        Character created;
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            var candidate = CharacterCreation.Prepare(ordinary ? tag + "Zero" : tag + "Quest",
                resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,level,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(level) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                { Level = level,AcceptedStoryLevel = level,CompletedQuests = done.ToArray(),InitialLearning = learning });
        }
        ushort requested = ordinary ? (ushort)0 : checked((ushort)parent);
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            That(peer.Id == created.Id,"同类顺序父选角绑定本条实际角色：" + parent);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,checked((ushort)parent),dungeon,row => information = row,requestQuestId:requested);
            string parentAcceptance = fixture.Snapshot("SELECT slot,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,parent);
            FormalDungeonTcpTests.Load(peer);
            int enabled = 0,bossChecks = 0;
            var dead = new HashSet<ushort>();
            Drain();
            var maze = dungeons.Get(checked((int)dungeon)).Mazes.Single(row => row.SourceOrdinal == information[7]);
            var overlay = maze.MapSpecification?.FirstOrDefault(row => row.Id == targetMap);
            That(overlay is not null,
                "入场 maze 的 MapSpecification 须含任务叠层图，找不到不改规格房号：" + parent + "/" + targetMap +
                " 现有=" + string.Join("|",(maze.MapSpecification ?? []).Select(row => row.Id)));
            var target = overlay!;
            var boss = maze.MapSpecification!.Single(row => row.Position == maze.Bosses.Single());
            peer.Send(31,QuestRequest(31,checked((ushort)child),[]));
            peer.Expect(1,31,[1,(byte)child,(byte)(child >> 8),1,0,0,0,0]);
            Drain();
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),target.Position);
            foreach (var next in path.Skip(1)) { KillRoom(); Move(next); }
            That(U32(map,14) == targetMap,"正式Run到达任务叠层图：" + targetMap);
            KillRoom();
            if (dialogueChild)
                That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,child).Length == 0 &&
                    fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,child) is [[0L]] &&
                    fixture.Query("SELECT quest_id,map_id FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=$p1",peer.Id,child)
                        is [[long clearedChild,long clearedMap]] && clearedChild == child && clearedMap == targetMap,
                    "带对话NPC的末清图子保存清图凭据但保留到结算后对话：" + child);
            else
                That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,child) is [[long archived]] &&
                    archived == child &&
                    fixture.Query("SELECT quest_id,experience_amount FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",peer.Id,child)
                        is [[long childId,0L]] && childId == child,
                    "末子零奖励自动归档，不发金币：" + child);
            string cleared = State();
            peer.Send(34,QuestRequest(34,checked((ushort)parent),[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == cleared &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,parent).Length == 0,
                "叠层图没有城镇 CompleteNpc，末子归档后不能在图内提前交付父任务：" + parent);
            if (!dialogueChild)
            {
                peer.Send(33,QuestRequest(33,checked((ushort)parent),[0,0]));
                peer.Expect(1,33,[1,(byte)parent,(byte)(parent >> 8),0,0,0,0]); Drain();
                That(State() == cleared,"父33读取实际子完成后的归零状态：" + parent);
            }
            peer.Send(34,QuestRequest(34,checked((ushort)child),[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == cleared,(dialogueChild ? "结算翻牌前图内子34不交付：" : "已自动归档子任务的迟到34不再次发奖：") + child);
            if (target.Position != boss.Position)
            {
                var toBoss = QuestStoryRouteTests.Path(maze.StaticTopology,target.Position,boss.Position);
                foreach (var next in toBoss.Skip(1)) { KillRoom(); Move(next); }
                That(U32(map,14) == boss.Id,"中途叠层完成后继续原Boss终点：" + boss.Id);
                KillRoom();
            }
            That(enabled == 1 && bossChecks == 1,"实际Boss死亡才许可结算：" + parent);
            var report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
            peer.Send(46,report); Drain();
            string settled = State();
            peer.Send(34,QuestRequest(34,checked((ushort)parent),[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == settled &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,parent).Length == 0,
                "Boss房没有城镇 CompleteNpc，结算中不能交付父任务：" + parent);
            peer.Send(69); peer.Expect(1,69,[1]); peer.Send(70); peer.Expect(1,70);
            peer.Send(71,[0,0]); peer.Expect(1,71); Drain();
            That(fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id) is [[1L]],
                "实际71领取原免费牌，不能将69/70展示视为已翻牌");
            if (parentQuest.CompleteNpc <= 0)
            {
                peer.Send(33,QuestRequest(33,checked((ushort)parent),[0,0]));
                peer.Expect(1,33,[1,(byte)parent,(byte)(parent >> 8),0,0,0,0]); Drain();
                That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,child)
                    is [[long archivedChild]] && archivedChild == child &&
                    fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,parent)
                    is [[0L]],"无父交付NPC的清图链翻牌后父33须归档末子并归零，不被子任务的城镇NPC占位卡住");
                string ready = State();
                peer.Send(33,QuestRequest(33,checked((ushort)parent),[0,0]));
                peer.Expect(1,33,[1,(byte)parent,(byte)(parent >> 8),0,0,0,0]); Drain();
                That(State() == ready && fixture.Snapshot("SELECT slot,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,parent) == parentAcceptance,
                    "父33重放保持原接取身份，不重复归档或发放奖励");
            }
            else
            {
                string flipped = State();
                peer.Send(33,QuestRequest(33,checked((ushort)parent),[0,0]));
                peer.Expect(1,33,[1,(byte)parent,(byte)(parent >> 8),dialogueChild ? (byte)1 : (byte)0,0,0,0]); Drain();
                peer.Send(34,QuestRequest(34,checked((ushort)parent),[255,255,1,0,255,255]));
                peer.Expect(1,34,[0,19,0]); Drain();
                That(State() == flipped,"翻牌后仍保留真实父交付NPC限制，不能在缺NPC的结算房归档末子或领奖");
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            }
            long goldBefore = (long)fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id).Single()[0];
            peer.Send(34,QuestRequest(34,checked((ushort)parent),[255,255,1,0,255,255]));
            var finished = peer.Expect(1,34).Body;
            That(finished.Length >= 4 && finished[0] == 1 && U16(finished,1) == parent,"按真实父交付场景完成顺序父34：" + parent);
            Drain();
            var receipts = fixture.Query(
                "SELECT quest_id,experience_amount FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",peer.Id);
            long goldAfter = (long)fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id).Single()[0];
            That(goldAfter >= goldBefore &&
                receipts.Any(row => (long)row[0] == parent && (long)row[1] > 0) &&
                (dialogueChild || receipts.Any(row => (long)row[0] == child && (long)row[1] == 0)) &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,parent)
                    is [[long doneParent]] && doneParent == parent &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,child)
                    is [[long doneChild]] && doneChild == child &&
                fixture.Query("SELECT quest_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id IN ($p1,$p2)",peer.Id,parent,child).Length == 0,
                "父34发放经验（PVF 无金币倍率则金币不增加），末子保持零奖励：" + parent);
            if (parentQuest.DeclaredGoldMultiple is > 0)
                That(goldAfter > goldBefore,"声明金币倍率的顺序父34发放金币：" + parent);
            string completed = State();
            peer.Send(34,QuestRequest(34,checked((ushort)parent),[255,255,1,0,255,255]));
            peer.Expect(1,34,[0,19,0]); Drain();
            That(State() == completed,"重复父34不再次归档或授予奖励：" + parent);
            if (parentQuest.CompleteNpc <= 0)
            {
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            }

            void Drain() => drain(peer,packet =>
            {
                if (packet.Opcode == 31) enabled++;
                if (packet.Opcode == 115) bossChecks++;
            });
            void Kill(int offset)
            {
                ushort key = U16(map,offset+6);
                if (!dead.Add(key)) return;
                peer.Send(39,FormalDungeonTcpTests.Death(key,peer.Id,map[offset+12])); Drain();
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
        using (var peer = Open(fixture,account))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id IN ($p1,$p2) ORDER BY quest_id",peer.Id,parent,child)
                is [[long restoredParent],[long restoredChild]] && restoredParent == parent && restoredChild == child &&
                fixture.Query("SELECT quest_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id IN ($p1,$p2)",peer.Id,parent,child).Length == 0 &&
                fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",peer.Id,parent) is [[1L]],
                "断线重选恢复父子完成与唯一父领奖，不重新接取或发奖：" + parent);
        }
        string State() => fixture.Snapshot("SELECT revision,experience,gold FROM characters WHERE id=$p0",created.Id)+
            fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
            fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
            fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id",created.Id);
    }

    static byte[] GrandineRetirement(ushort key,ushort reporter)
    {
        // 实际61273普通producer的零参与者合同，不复制友方APC/其他模板回报。
        // 统计及helper上下文使用明确合成的0值；reason7在68，不把31解释成CMT参数。
        var body = new byte[74];
        BinaryPrimitives.WriteUInt32LittleEndian(body,key);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),reporter);
        body[30] = 0; body[68] = 7;
        FormalDungeonTcpTests.SealDeath(body);
        return body;
    }
    static byte[] QuestRequest(byte command,ushort quest,ReadOnlySpan<byte> tail)
    {
        var body = new byte[4+tail.Length]; body[0] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),quest); tail.CopyTo(body.AsSpan(4)); return body;
    }
}
