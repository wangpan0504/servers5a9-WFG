using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestMirrorPrisonTcpTests
{
    // 真实3444资源和正式TCP处理链；39/45是独立编码的合成输入，不代表客户端演出已验收。
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        foreach (bool ordinary in new[] { false,true })
        {
            var area = resources.Towns.Areas.Values.First(row => row.Level <= 53 && row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 148 &&
                    (option.QuestId == -1 || option.InProgress && option.QuestId == 3444 || !option.InProgress && option.QuestId != 3444)));
            var done = new HashSet<int>(area.Quests) { 3443 };
            if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
            var option = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(row => row.DungeonId == 148 &&
                (row.QuestId == -1 || row.InProgress && row.QuestId == 3444 || !row.InProgress && row.QuestId != 3444));
            if (!option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(3444)).Quests);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
                checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                var candidate = CharacterCreation.Prepare(ordinary ? "PrisonZero" : "PrisonQuest",resources.Characters.Get(0),
                    position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,53,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(53) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                    { Level=53,AcceptedStoryLevel=53,CompletedQuests=done.ToArray(),InitialLearning=learning });
            }
            byte[] lastDeath = [];
            using (var peer = Open(fixture))
            {
                peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
                That(peer.Id == created.Id,"3444选角绑定本条独立角色");
                var map = FormalDungeonTcpTests.Enter(peer,3444,148,information =>
                    That(information[8] == 3 && information[9] == 2,"3444保留基础Boss坐标3,2"),
                    requestQuestId:ordinary ? (ushort)0 : (ushort)3444);
                string acceptance = (string)fixture.Query(
                    "SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3444",peer.Id)[0][0];
                int enabled = 0,bossChecks = 0,deaths = 0;
                Load(53647);
                foreach (var step in new (byte X,byte Y,uint Map)[] { (1,0,53648),(2,0,53649),(3,0,53650),
                    (4,0,53651),(4,1,53652),(4,2,53653),(3,2,53654) })
                {
                    if (U32(map,14) == 53649)
                        That(map[18] == 4 && U32(map,27+44) == 107003986 && map[40+44] == 1,
                            "53649保留team0友方演员，不要求伪造死亡");
                    if (U32(map,14) == 53653)
                        That(map[18] == 1 && U32(map,27) == 107003991 && map[40] == 0,
                            "53653保留真实普莱斯演员与原战斗资格，不以dialog推导见面目标");
                    foreach (int at in Enumerable.Range(0,map[18]).Where(at => map[40+22*at] == 0)) Kill(at);
                    Move(step.X,step.Y,0); Load(step.Map);
                }
                That(map[18] == 4 && Enumerable.Range(0,4).Count(at => U32(map,27+22*at) == 107004988) == 1,
                    "基础53654保留真实Boss和三名普通演员");
                int boss = Enumerable.Range(0,4).Single(at => U32(map,27+22*at) == 107004988);
                ushort bossKey = U16(map,25+22*boss);
                string before = State();
                Move(3,2,1); Load(53662);
                That(map[18] == 4 && Enumerable.Range(0,4).All(at => map[40+22*at] == 0) &&
                    Enumerable.Range(0,4).Select(at => U32(map,27+22*at)).Order().SequenceEqual(new uint[] { 65018,107003997,107003997,107003997 }),
                    "演出层保留四名原演员，不借用友方退场或假死亡");
                ushort layerKey = U16(map,25);
                Move(3,2,1);
                That(map.Length == 16 && map[0] == 3 && map[1] == 2 && map[2] == 2 && map[13] == 0 &&
                    U32(map,9) == 1 && map[14] == 0 && map[15] == 255,"未清末层返回使用RestoreBase/Cached，不重发演员");
                FormalDungeonTcpTests.Load(peer); Drain();
                That(State() == before && enabled == 0 && bossChecks == 0,"演出导航不写成长、任务进度、清图或结算");
                peer.Send(34,[34,0,116,13,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
                peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); Drain();
                peer.Send(39,ScriptDeath(layerKey,peer.Id)); Drain();
                That(State() == before && enabled == 0,"恢复基础Boss前的34/46及旧层死亡均不能提前成功");
                lastDeath = ScriptDeath(bossKey,peer.Id);
                peer.Send(39,lastDeath); Drain(); deaths++;
                That(enabled == 1 && bossChecks == 1 && fixture.Query(
                    "SELECT condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3444",peer.Id)
                    is [[0L,string saved]] && saved == acceptance && fixture.Query(
                    "SELECT quest_id,map_id,acceptance_id FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=3444",peer.Id)
                    is [[3444L,53654L,string clearAcceptance]] && clearAcceptance == acceptance && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[long count]] && count == deaths,
                    "真实基础Boss死亡后才提交原接取53654清图、115及唯一31，不计四名演出演员和三名Boss收尾演员");
                before = State(); peer.Send(39,lastDeath); Drain();
                That(State() == before && enabled == 1,"重复Boss死亡不重发进度或经验");
                if (!ordinary) Finish();
                peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); Drain();
                That(fixture.Query("SELECT quest_id,map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                    is [[long request,53654L]] && request == (ordinary ? 0 : 3444),"46沿原任务或普通0实例结算基础Boss终点");
                before = State(); peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); Drain();
                That(State() == before,"重复46不重复结算奖励");
                if (ordinary) Finish();
                before = State(); peer.Send(34,[34,0,116,13,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
                That(State() == before,"重复34不重复交付");
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

                void Load(uint expected) { That(U32(map,14) == expected,"3444完整资源路线MAP："+expected); FormalDungeonTcpTests.Load(peer); Drain(); }
                void Move(byte x,byte y,byte kind) { var body = new byte[100]; body[0]=x; body[1]=y; body[10]=kind;
                    peer.Send(45,body); peer.Expect(1,45,[1]); map=peer.Expect(0,29).Body; }
                void Kill(int at) { peer.Send(39,ScriptDeath(U16(map,25+22*at),peer.Id)); Drain(); deaths++; }
                void Drain() => drain(peer,packet => { if (packet.Opcode == 31) { Bytes(packet.Body,[],"结算许可31为空正文"); enabled++; }
                    if (packet.Opcode == 115) bossChecks++; });
                void Finish() { peer.Send(34,[34,0,116,13,255,255,1,0,255,255]); var reply=peer.Expect(1,34).Body;
                    That(reply.Length >= 4 && reply[0] == 1 && U16(reply,1) == 3444,"真实34交付3444"); Drain(); }
            }
            using (var peer = Open(fixture))
            {
                peer.Select(created.Slot,prepared:true); peer.Barrier();
                That(peer.CompletedQuestHistory[4+3444] == 1 && fixture.Query(
                    "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3444",peer.Id) is [[1L]] &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                    "重连恢复3444唯一交付及通关回执");
                string before=State(); peer.Send(39,lastDeath); drain(peer,null); That(State() == before,"重连迟到39不能重放旧Run奖励");
            }
            string State() => fixture.Snapshot()+fixture.Snapshot("SELECT * FROM character_active_main_quests ORDER BY character_id,slot")+
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts ORDER BY character_id,event_id")+
                fixture.Snapshot("SELECT * FROM character_quest_clear_receipts ORDER BY character_id,quest_id")+
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts ORDER BY character_id,instance_id")+
                fixture.Snapshot("SELECT * FROM character_quest_completion_receipts ORDER BY character_id,quest_id")+
                fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid")+
                fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind");
        }
    }

    static byte[] ScriptDeath(ushort key,ushort reporter)
    {
        var body=new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(body,key);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),reporter); body[31]=0x22;
        FormalDungeonTcpTests.SealDeath(body); return body;
    }
}
