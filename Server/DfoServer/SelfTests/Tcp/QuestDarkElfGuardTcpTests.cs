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

// 复用本组服务与真实3265/D154资源。普通39及末层脚本39是合成输入，不代表ACT实发或客户端验收。
internal static class QuestDarkElfGuardTcpTests
{
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain,DungeonCatalog dungeons)
    {
        var quest = resources.Quests.Get(3265);
        int level = Math.Clamp(Math.Max(quest.MinimumLevel,(int)(dungeons.Get(154).MinimumLevel ?? 1)),1,95);
        var area = resources.Towns.Areas.Values.First(row => row.Level <= level && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(Eligible));
        var option = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(Eligible);
        var done = new HashSet<int>(area.Quests) { 3145,4873 };
        done.UnionWith(quest.Prerequisites.Where(id => id is > 0 and < 30000));
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests.First(id => id != 3265));
        if (!option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
        done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(3265)).Quests);
        done.Remove(3265);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        foreach (bool finish in new[] { false,true })
        {
            string account = finish ? "darkelfclear" : "darkelfabort";
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var candidate = CharacterCreation.Prepare(finish ? "GuardClear" : "GuardAbort",resources.Characters.Get(0),
                    position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,(byte)level,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(level) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                    { Level = (byte)level,AcceptedStoryLevel = (uint)level,CompletedQuests = done.ToArray(),InitialLearning = learning });
            }
            string acceptance;
            byte[] lastDeath = [];
            using (var peer = Open(fixture,account))
            {
                peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
                byte[] information = [];
                byte[] map = FormalDungeonTcpTests.Enter(peer,3265,154,row => information = row);
                var definition = dungeons.Get(154);
                var maze = dungeons.ResolveOrdinaryMaze(definition,
                    definition.Mazes.Single(row => row.SourceOrdinal == information[7]));
                var boss = new DungeonPoint(information[8],information[9]);
                var layers = maze.LayeredMaps.Where(row => row.Position == boss).ToArray();
                That(layers.Select(row => row.Id).SequenceEqual([70172,70173,70174]),"3265真实Boss格按70172、70173、70174排序");
                acceptance = (string)fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3265",peer.Id).Single().Single();
                int enabled = 0, bossChecks = 0;
                FormalDungeonTcpTests.Load(peer); Drain();
                var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),boss);
                foreach (var point in path.Skip(1))
                {
                    foreach (int offset in Enemies())
                    {
                        peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12])); Drain();
                    }
                    Move(point,0);
                }
                That(U32(map,14) == 70138 && enabled == 0 && bossChecks == 0,"基础70138不冒充终末清图或开放结算");
                string beforeLayers = State();
                for (int index = 0; index < 2; index++)
                {
                    Move(boss,1);
                    That(U32(map,14) == layers[index].Id && Enemies().Length > 0 &&
                        rooms.Get(layers[index],requirePassiveDefinitions:false).CinematicLayerReturn is
                            { IsOnStartMap:true, ActorId:null, CinematicId:var movie } && movie == 427+index,
                        "真实演出层仍有存活演员，按原ON START MAP合同换下层，不先伪造清怪");
                }
                That(State() == beforeLayers,"70172到70173的导航不写死亡、经验、清图、任务或资产");
                if (finish)
                {
                    Move(boss,1);
                    var enemies = Enemies();
                    That(U32(map,14) == 70174 && enemies.Length == 1 && U32(map,enemies[0]+8) == 70216 && map[enemies[0]+13] == 3,
                        "末层完整下发70216脚本Boss与非阻挡友方演员");
                    That(State() == beforeLayers && enabled == 0 && bossChecks == 0,"演出换层不因DESTROY声明提前签发死亡或完成任务");
                    peer.Send(34,[34,0,193,12,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
                    byte[] report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
                    peer.Send(46,report); Drain();
                    That(State() == beforeLayers,"缺少末层真实死亡时，34与46都不能提前提交");
                    lastDeath = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(lastDeath,U16(map,enemies[0]+6));
                    BinaryPrimitives.WriteUInt16LittleEndian(lastDeath.AsSpan(4),peer.Id);
                    lastDeath[31] = 0x22; FormalDungeonTcpTests.SealDeath(lastDeath);
                    peer.Send(39,lastDeath); Drain();
                    That(enabled == 1 && bossChecks == 1 && fixture.Query(
                        "SELECT acceptance_id,map_id FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=3265",peer.Id)
                        is [[string accepted,70174L]] && accepted == acceptance,"末层合成39只为原接取生成唯一70174清图凭据与结算许可");
                    string cleared = State(); peer.Send(39,lastDeath); Drain();
                    That(State() == cleared && enabled == 1 && bossChecks == 1,"重复末层39不重发许可或奖励");
                    peer.Send(46,report); Drain();
                    That(fixture.Query("SELECT quest_id,map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                        is [[3265L,70174L]],"真实46保持末层清图身份并提交一次结算");
                    string settled = State(); peer.Send(46,report); Drain();
                    That(State() == settled,"重复46不再次发奖");
                    peer.Send(34,[34,0,193,12,255,255,1,0,255,255]);
                    var reply = peer.Expect(1,34).Body; Drain();
                    That(reply.Length >= 4 && reply[0] == 1 && U16(reply,1) == 3265,"清图后手动34交付3265");
                    string completed = State();
                    peer.Send(34,[34,0,193,12,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
                    That(State() == completed,"重复34不再次完成或发奖");
                }
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

                int[] Enemies() => Enumerable.Range(0,map[18]).Select(at => 19+22*at)
                    .Where(at => map[at+21] == 0 && map[at+13] != 9).ToArray();
                void Move(DungeonPoint point,byte kind)
                {
                    var request = new byte[100]; request[0] = checked((byte)point.X); request[1] = checked((byte)point.Y); request[10] = kind;
                    peer.Send(45,request); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                    FormalDungeonTcpTests.Load(peer); Drain();
                }
                void Drain() => drain(peer,packet => { if (packet.Opcode == 31) enabled++; if (packet.Opcode == 115) bossChecks++; });
            }
            using (var peer = Open(fixture,account))
            {
                peer.Select(created.Slot,prepared:true); peer.Barrier();
                That(fixture.Query("SELECT acceptance_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3265",peer.Id)
                    is var active && (finish ? active.Length == 0 && peer.CompletedQuestHistory[4+3265] == 1 :
                        active is [[string original,1L]] && original == acceptance),"回城重连恢复已交付或原接取未完成状态，不因放弃演出授奖");
                That(fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=3265",peer.Id)
                    is [[long clears]] && clears == (finish ? 1 : 0),"放弃演出与完成末层分别保持零份和一份清图凭据");
                if (finish)
                {
                    string saved = State(); peer.Send(39,lastDeath); drain(peer,null);
                    That(State() == saved,"回城重连后的旧末层39不能再次提交");
                }
            }
            string State() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
                fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY combat_sequence",created.Id)+
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
                fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id)+
                fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",created.Id)+
                fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",account);
        }
        bool Eligible(TownDungeonOption row) => row.DungeonId == 154 &&
            (row.QuestId == -1 || row.InProgress && row.QuestId == 3265 || !row.InProgress && row.QuestId != 3265);
    }
}
