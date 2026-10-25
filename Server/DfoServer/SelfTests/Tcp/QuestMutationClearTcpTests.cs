using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestMutationClearTcpTests
{
    // 只消费真实MAP静态演员；39为明确合成输入，不证明75075缺失末动作或客户端演出已验。
    static readonly (DungeonPoint Position,int[] Actors)[] Route =
    [
        (new(4,3),[75067,62013,62013,62013,62013,62013]),
        (new(3,3),[62013,62013,62013,62013,62013,62013]),
        (new(2,3),[62013,62013,62013,62013,109007296]),
        (new(2,2),[62013,62013,62013,62013,62013,62013,62013,62013]),
        (new(1,2),[61464,61464,61464,61464]),
        (new(0,2),[62013,62013,62013,62013,109007296]),
        (new(0,1),[61464,62013,62013,61464,61464,61464,62013,62013]),
        (new(0,0),[75075])
    ];

    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        var quest = resources.Quests.Get(3348);
        That(quest.MinimumLevel == 48 && quest.MaximumLevel == 99 && quest.Prerequisites.SequenceEqual(new[] { 3347 }) &&
            QuestClearEligibility.IsSingleMap(quest) && quest.IntData[0] == 76347 && quest.MainQuest is null &&
            quest.Definition!.Dungeons.Any(row => row.DungeonId == 52) && quest.RewardType == "[item]" &&
            quest.Rewards is [{ Type: 0,Value: 0,Text: null },{ Type: 0,Value: 0,Text: null }] &&
            !quest.Attributes.Contains("not give exp quest"),
            "3348保持真实48级、3347前置、D52清76347及零物品奖励声明，不预写清图条件");
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(52).Mazes.Single(row => row.ConnectsQuest(3348));
        That(maze.Width == 5 && maze.Height == 4 && maze.Starts.Single() == Route[0].Position && maze.Bosses.Single() == Route[^1].Position &&
            maze.MapSpecification!.Select(row => (row.Id,row.Position)).SequenceEqual(
                Route.Select((row,index) => (76340+index,row.Position))) &&
            QuestStoryRouteTests.Path(maze.StaticTopology,maze.Starts.Single(),maze.Bosses.Single()).SequenceEqual(Route.Select(row => row.Position)) &&
            maze.LayeredMaps.Count == 0 && maze.ConditionalMaps.Count == 0 && maze.StoryStages.Count == 0,
            "3348唯一任务迷宫保持真实八房折线路径及76347终点，不借普通或相邻任务布局");
        // 与既有入场回归复用同一真实资源资格；其未清退出矩阵继续由VerifyMutationEntry负责。
        var entrance = MiddleQuestEntryTests.Entrance(resources.Towns,quest,52);
        That(entrance.Completed.Contains(3347) && !entrance.Completed.Contains(3348),"变异生物夹具只准备入口前置，不预完成3348");
        foreach (ushort requested in new ushort[] { 3348,0 }) VerifyCase(requested);

        void VerifyCase(ushort requested)
        {
            string account = "mutation"+requested;
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var candidate = CharacterCreation.Prepare("Mutant"+requested,resources.Characters.Get(0),
                    entrance.Position,entrance.Position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,48,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(48) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                    { Level = 48,AcceptedStoryLevel = 48,CompletedQuests = entrance.Completed,InitialLearning = learning });
            }
            string acceptance = "", dailyAcceptance = "";
            long questSlot = -1;
            byte[] firstDeath = [], lastDeath = [];
            using (var peer = Open(fixture,account))
            {
                peer.Select(0,prepared:true,resumeTown:true,room:false);
                That(peer.Id == created.Id,"变异生物完整通关使用独立账号及实际角色身份");
                var daily = fixture.Query("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id);
                That(daily is [[13033L,1636L,string]],"48级保留原等级地区每日，与3348独立共存");
                dailyAcceptance = (string)daily[0][2];
                peer.Send(31,Request(31,[])); peer.Expect(1,31,[1,20,13,1,0,0,0,0]); drain(peer,null);
                acceptance = (string)fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3348",peer.Id)[0][0];
                questSlot = (long)fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3348",peer.Id)[0][0];
                var map = FormalDungeonTcpTests.Enter(peer,3348,52,information =>
                {
                    That(U32(information,0) == 52 && information[7] == maze.SourceOrdinal && information[8] == 0 && information[9] == 0,
                        "3348及普通0保留任务迷宫与真实Boss坐标");
                },requestQuestId:requested);
                int enabled = 0,bossChecks = 0,experienceNotices = 0,deathCount = 0;
                var allKeys = new HashSet<ushort>();
                var dead = new HashSet<ushort>();
                VerifyMap(0); FormalDungeonTcpTests.Load(peer); Drain();
                That(fixture.Query("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3348",peer.Id)
                    is [[3348L,1L,string original]] && original == acceptance,
                    "完整入场及加载不改变原3348接取或提前归零");
                int friendly = Enumerable.Range(0,map[18]).Select(at => 19+22*at).Single(at => U32(map,at+8) == 75067);
                string before = State();
                peer.Send(39,FormalDungeonTcpTests.Death(U16(map,friendly+6),peer.Id,map[friendly+12])); Drain();
                That(State() == before && experienceNotices == 0 && enabled == 0 && bossChecks == 0,
                    "首房75067友方退场不写成长、清图、奖励或结算");
                var report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
                RejectEarly();
                for (int index = 0; index < Route.Length-1; index++)
                {
                    KillRoom();
                    That(enabled == 0 && bossChecks == 0 && Progress() is [[3348L,1L]] &&
                        fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                        fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                        "前七房实际敌人死亡不能替代76347通关或提前发115/31");
                    var move = new byte[100]; move[0] = checked((byte)Route[index+1].Position.X); move[1] = checked((byte)Route[index+1].Position.Y);
                    peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                    VerifyMap(index+1); FormalDungeonTcpTests.Load(peer); Drain();
                    if (index == 0)
                    {
                        before = State(); peer.Send(39,firstDeath); Drain();
                        That(State() == before && enabled == 0 && bossChecks == 0,"跨房迟到首房39不在下一房重复发奖或推进条件");
                    }
                }
                That(deathCount == 39 && U32(map,14) == 76347,"完整前七房39个敌人死亡后仍保留真实末图Boss");
                RejectEarly();
                int bossOffset = 19;
                lastDeath = FormalDungeonTcpTests.Death(U16(map,bossOffset+6),peer.Id,map[bossOffset+12]);
                Kill(bossOffset);
                That(deathCount == 40 && enabled == 1 && bossChecks == 1 && Progress() is [[3348L,0L]] && fixture.Query(
                    "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                    "75075首次合法死亡才归零3348并许可结算，清图不自动领取普通任务奖励");
                var clearRows = fixture.Query("""
                    SELECT r.acceptance_id,r.quest_slot,r.map_id,r.epoch,g.epoch,g.quest_id,
                      r.instance_id,g.instance_id,r.task_attempt,g.task_attempt,g.monster_id
                    FROM character_quest_clear_receipts r JOIN character_dungeon_growth_receipts g
                    ON g.character_id=r.character_id AND g.event_id=r.growth_event_id
                    WHERE r.character_id=$p0 AND r.quest_id=3348 AND r.dungeon_id=52
                    """,peer.Id);
                That(clearRows is [[string accepted,long slot,76347L,long clearEpoch,long growthEpoch,long instanceQuest,
                        string instance,string growthInstance,long attempt,long growthAttempt,75075L]] &&
                    accepted == acceptance && slot == questSlot && clearEpoch == growthEpoch+1 && instanceQuest == requested &&
                    instance == growthInstance && attempt == growthAttempt && fixture.Query(
                        "SELECT count(*),count(DISTINCT instance_id),count(DISTINCT quest_id) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id)
                        is [[40L,1L,1L]] && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[long currentAttempt]] &&
                    currentAttempt == attempt,
                    "3348清图绑定原Acceptance及槽、当前attempt、75075事件和growth下一epoch，普通0保留原请求身份");
                before = State(); peer.Send(39,lastDeath); Drain();
                That(State() == before && enabled == 1 && bossChecks == 1,"重复75075死亡不重发成长、清图或结算许可");
                peer.Send(46,report); Drain();
                That(fixture.Query("SELECT quest_id,map_id,instance_id,task_attempt,epoch FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                    is [[long settledQuest,76347L,string settledInstance,long settledAttempt,long settledEpoch]] && settledQuest == requested &&
                    settledInstance == (string)clearRows[0][6] && settledAttempt == (long)clearRows[0][8] && settledEpoch == (long)clearRows[0][3],
                    "46以原请求号提交同一76347实例、attempt及清图epoch的唯一结算");
                before = State(); peer.Send(46,report); Drain();
                That(State() == before,"重复46不重抽通关奖励、金币或经验");
                peer.Send(34,Request(34,[255,255,1,0,255,255]));
                var finished = peer.Expect(1,34).Body;
                That(finished.Length >= 4 && finished[0] == 1 && U16(finished,1) == 3348,"真实34交付已持久清图的3348");
                Drain();
                That(Progress().Length == 0 && fixture.Query("""
                    SELECT acceptance_id,instance_id,task_attempt,epoch,condition_type,experience_amount,rewards
                    FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3348
                    """,peer.Id) is [[string completedAcceptance,string completedInstance,long completedAttempt,long completedEpoch,3L,long questExperience,"[]"]] &&
                    completedAcceptance == acceptance && completedInstance == (string)clearRows[0][6] && completedAttempt == (long)clearRows[0][8] &&
                    completedEpoch == (long)clearRows[0][3] && questExperience > 0,
                    "34仅删除原活动槽，清图来源和接取身份保留，按真实零物品声明发任务经验一次");
                before = State();
                peer.Send(34,Request(34,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
                peer.Send(46,report); Drain(); peer.Send(39,lastDeath); Drain();
                That(State() == before,"任务交付后的重复34、迟到46及39不二次写资产或凭据");
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

                void RejectEarly()
                {
                    string unchanged = State();
                    peer.Send(34,Request(34,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
                    peer.Send(46,report); Drain();
                    That(State() == unchanged && enabled == 0 && bossChecks == 0,"未清图的34和46不能绕过75075或签发任务及通关奖励");
                }
                void VerifyMap(int index)
                {
                    That(map.Length >= 19+22*map[18] && map[18] == Route[index].Actors.Length && U32(map,14) == 76340+index &&
                        map[0] == Route[index].Position.X && map[1] == Route[index].Position.Y,
                        $"变异生物MAP合同：expected={76340+index}@{Route[index].Position.X},{Route[index].Position.Y}/actors{Route[index].Actors.Length}; actual={U32(map,14)}@{map[0]},{map[1]}/actors{map[18]}/length{map.Length}");
                    for (int at = 0; at < map[18]; at++)
                    {
                        int offset = 19+22*at, expected = Route[index].Actors[at];
                        ushort key = U16(map,offset+6);
                        That(U32(map,offset+8) == expected && map[offset+21] == (expected is 75067 or 109007296 ? 1 : 0) && key != peer.Id && allKeys.Add(key),
                            "真实模板、友方排除位和全路线唯一key不被删改："+(76340+index));
                        if (expected == 109007296)
                            That(U16(map,offset) == 0 && U32(map,offset+2) == 0 && map[offset+12] == 0 && map[offset+13] == 9 &&
                                map[offset+14] == 0 && map[offset+15] == 0 && map[offset+16] == 255 && U32(map,offset+17) == 0,
                                "76342/76345保留真实special父及三个trap资源，父按type9发布而不冒充可击杀怪物");
                    }
                }
                void KillRoom()
                {
                    foreach (int offset in Enumerable.Range(0,map[18]).Select(at => 19+22*at).Where(at => map[at+21] == 0)) Kill(offset);
                }
                void Kill(int offset)
                {
                    ushort key = U16(map,offset+6);
                    That(dead.Add(key),"完整八房仅首次上报当前敌人key");
                    var body = FormalDungeonTcpTests.Death(key,peer.Id,map[offset+12]);
                    if (firstDeath.Length == 0) firstDeath = body;
                    peer.Send(39,body); Drain(); deathCount++;
                }
                void Drain() => drain(peer,packet =>
                {
                    if (packet.Opcode == 31) { Bytes(packet.Body,[],"变异生物通关许可31为空正文"); enabled++; }
                    if (packet.Opcode == 115) bossChecks++;
                    if (packet.Opcode == 37) experienceNotices++;
                });
            }
            using (var peer = Open(fixture,account))
            {
                peer.Select(0,prepared:true,resumeTown:true,room:false); peer.Barrier();
                That(fixture.Query("SELECT quest_id,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id)
                    is [[13033L,string retainedDaily]] && retainedDaily == dailyAcceptance,
                    "完成与重连仅清理3348，不移除或重建并存地区每日");
                That(peer.Id == created.Id && Progress().Length == 0 && peer.CompletedQuestHistory[4+3348] == 1 &&
                    fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3348",peer.Id) is [[1L]] &&
                    fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=3348",peer.Id) is [[1L]] &&
                    fixture.Query("SELECT quest_id,map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                        is [[long requestQuest,76347L]] && requestQuest == requested,
                    "回城重连恢复3348唯一完成历史、原清图及对应请求号结算，不重接或重发任务奖励");
                string before = State();
                peer.Send(39,firstDeath); drain(peer,null); peer.Send(39,lastDeath); drain(peer,null);
                peer.Send(34,Request(34,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); drain(peer,null);
                That(State() == before,"重连后的旧Run首末39与迟到34不能复活原实例或重复交付");
            }

            object[][] Progress() => fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3348 ORDER BY slot",created.Id);
            string State() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
                fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY combat_sequence",created.Id)+
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id)+
                fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",created.Id)+
                fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",account);
        }
    }

    static byte[] Request(byte command,ReadOnlySpan<byte> tail)
    {
        var body = new byte[4+tail.Length]; body[0] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),3348); tail.CopyTo(body.AsSpan(4)); return body;
    }
}
