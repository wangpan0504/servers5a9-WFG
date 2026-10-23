using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestStolenEggTcpTests
{
    // 真实6032资源及正式TCP/SQLite链；39/45为合成输入，不能证明原生演出或dummy自然退场。
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        foreach (bool retireLayer in new[] { false,true })
        foreach (bool ordinary in new[] { false,true })
        {
            var area = resources.Towns.Areas.Values.First(row => row.Level <= 92 && row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100319));
            var done = new HashSet<int>(area.Quests) { 6031 };
            if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(6032)).Quests);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
                checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
            That(!done.Contains(6032),"6032夹具只准备入口及6031前置，不预造本任务完成");
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                var candidate = CharacterCreation.Prepare((ordinary ? "Egg0" : "EggQ")+(retireLayer ? "Retire" : "Live"),resources.Characters.Get(0),
                    position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,92,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(92) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                    { Level=92,AcceptedStoryLevel=92,CompletedQuests=done.ToArray(),InitialLearning=learning });
            }
            byte[] lastDeath = [];
            using (var peer = Open(fixture))
            {
                peer.Select(RoleIndex(peer.Roster,created),prepared:true,resumeTown:true,room:false);
                That(peer.Id == created.Id,"6032 TCP选角对应本条独立角色");
                var map = FormalDungeonTcpTests.Enter(peer,6032,291100319,information =>
                    That(information[8] == 3 && information[9] == 0,"6032 NOTI28保留基础Boss坐标3,0"),
                    requestQuestId:ordinary ? (ushort)0 : (ushort)6032);
                string acceptance = (string)fixture.Query(
                    "SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=6032",peer.Id)[0][0];
                int enabled = 0,bossChecks = 0,deaths = 0,navigationPackets = 0;
                Load(59911);
                foreach (var step in new (byte X,uint Map)[] { (1,59912),(2,59913) })
                {
                    Clear();
                    Move(step.X,0); Load(step.Map);
                }
                Move(2,1); Load(59914);
                VerifyActors(2);
                ushort layerKey = U16(map,25);
                string before = State();
                peer.Send(39,ScriptDeath(ushort.MaxValue,peer.Id)); Drain();
                That(State() == before && enabled == 0,"未知演员key不能形成6032清图");
                if (retireLayer) Clear();
                That(enabled == 0 && bossChecks == 0 && fixture.Query(
                    "SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=6032",peer.Id) is [[0L]],
                    "同ID演出层即使上报可信死亡也不是资源Boss清图房");
                RejectPrematureFinish();
                ReturnBase();
                before = State(); peer.Send(39,ScriptDeath(layerKey,peer.Id)); Drain();
                That(State() == before && enabled == 0,"返回基础房后的演出层迟到39不能提交");
                Move(3,0); Load(59914); VerifyActors(3);
                ushort terminalKey = U16(map,25);
                That(terminalKey != layerKey,"基础Boss房与同ID演出层分配独立演员key");
                before = State(); peer.Send(39,ScriptDeath(layerKey,peer.Id)); Drain();
                That(State() == before && enabled == 0,"演出层旧key不能替代基础Boss房70215死亡");
                lastDeath = ScriptDeath(terminalKey,peer.Id);
                peer.Send(39,lastDeath); Drain(); deaths++;
                That(enabled == 1 && bossChecks == 1 && fixture.Query(
                    "SELECT condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=6032",peer.Id)
                    is [[0L,string saved]] && saved == acceptance && fixture.Query(
                    "SELECT map_id,acceptance_id FROM character_quest_clear_receipts WHERE character_id=$p0 AND quest_id=6032",peer.Id)
                    is [[59914L,string cleared]] && cleared == acceptance && fixture.Query(
                    "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[long count]] && count == deaths,
                    "仅基础Boss房可信合成死亡提交原接取清图、成长及唯一115/31");
                before = State(); peer.Send(39,lastDeath); Drain();
                That(State() == before && enabled == 1,"6032重复末层39不重复成长或结算许可");
                if (!ordinary) Finish();
                peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); Drain();
                That(fixture.Query("SELECT quest_id,map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                    is [[long request,59914L]] && request == (ordinary ? 0 : 6032),
                    "6032实际末层46沿原显式或普通0实例写入唯一结算");
                before = State(); peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); Drain();
                That(State() == before,"6032重复46不重复奖励");
                if (ordinary) Finish();
                before = State();
                peer.Send(34,FinishBody()); peer.Expect(1,34,[0,19,0]); Drain();
                That(State() == before,"6032重复34不重复交付或奖励");
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

                void Load(uint expected,bool cached = false)
                {
                    if (cached)
                        That(expected == 59913 && map.Length == 16 && map[0] == 2 && map[1] == 0 && map[2] == 0 &&
                            map[13] == 0 && map[14] == 0 && map[15] == 255,
                            "6032返回已加载基础59913使用Current/Cached，不重发MAP或演员");
                    else That(U32(map,14) == expected,"6032真实资源路线MAP："+expected);
                    FormalDungeonTcpTests.Load(peer); Drain();
                }
                void VerifyActors(byte x) => That(map[0] == x && map[1] == 0 && map[18] == 4 && U32(map,27) == 70215 &&
                    map[40] == 0 && Enumerable.Range(1,3).All(at => U32(map,27+22*at) == 109034881 && map[40+22*at] == 1),
                    "59914保留真实70215及三个友方资源演员，不要求伪造友方死亡");
                void Move(byte x,byte kind)
                {
                    var body = new byte[100]; body[0]=x; body[10]=kind;
                    peer.Send(45,body); peer.Expect(1,45,[1]); map=peer.Expect(0,29).Body;
                }
                void ReturnBase()
                {
                    string saved = State();
                    Move(2,0);
                    That(map.Length == 16 && map[0] == 2 && map[1] == 0 && map[2] == 2 && map[13] == 0 && map[15] == 255,
                        "真实ACT kind0返回使用operation2/Cached，不重发末层演员");
                    That(State() == saved,"呈现返回不提交死亡、任务或资产变化");
                    FormalDungeonTcpTests.Load(peer); Drain();
                    var ack = new byte[100]; ack[0]=2; ack[10]=1;
                    peer.Send(45,ack); Drain();
                    int seen = navigationPackets;
                    ack[10]=0; peer.Send(45,ack); Drain();
                    That(navigationPackets == seen && State() == saved && enabled == 0 && bossChecks == 0,
                        "重复kind0返回不能重进演出层、推进实例或生成奖励");
                }
                void Clear()
                {
                    foreach (int at in Enumerable.Range(0,map[18]).Where(at => map[40+22*at] == 0))
                    {
                        peer.Send(39,ScriptDeath(U16(map,25+22*at),peer.Id)); Drain(); deaths++;
                    }
                }
                void Drain() => drain(peer,packet =>
                {
                    if (packet.Opcode is 29 or 30 or 3 or 36 or 37 or 45) navigationPackets++;
                    if (packet.Opcode == 31) { Bytes(packet.Body,[],"6032结算许可31为空正文"); enabled++; }
                    if (packet.Opcode == 115) bossChecks++;
                });
                void RejectPrematureFinish()
                {
                    string saved = State();
                    peer.Send(34,FinishBody()); peer.Expect(1,34,[0,19,0]); Drain();
                    peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); Drain();
                    That(State() == saved && enabled == 0,"6032末层可信死亡前34/46均不提前完成或奖励");
                }
                void Finish()
                {
                    peer.Send(34,FinishBody()); var reply=peer.Expect(1,34).Body;
                    That(reply.Length >= 4 && reply[0] == 1 && U16(reply,1) == 6032,"6032真实34交付原接取"); Drain();
                }
            }
            using (var peer = Open(fixture))
            {
                peer.Select(RoleIndex(peer.Roster,created),prepared:true); peer.Barrier();
                That(peer.CompletedQuestHistory[4+6032] == 1 && fixture.Query(
                    "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=6032",peer.Id) is [[1L]] &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                    "6032回城重连恢复完成位和唯一交付、末层结算回执");
                string before=State(); peer.Send(39,lastDeath); drain(peer,null);
                That(State() == before,"6032重连迟到39不能复活旧实例奖励");
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

    static byte[] FinishBody() => [34,0,144,23,255,255,1,0,255,255];

    static byte[] ScriptDeath(ushort key,ushort reporter)
    {
        var body=new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(body,key);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),reporter); body[31]=0x22;
        FormalDungeonTcpTests.SealDeath(body); return body;
    }

    static ushort RoleIndex(byte[] roster,Character role)
    {
        int at=15;
        for (ushort ordinal=0;ordinal<U16(roster,13);ordinal++)
        {
            int width=checked((int)U32(roster,at+2));
            if (U16(roster,at) == role.Slot && DecodeName(roster,at+6,width) == role.Name) return ordinal;
            int equipment=at+21+width;
            at=equipment+AssetWireChecks.ActorEntries(roster,equipment).ConsumedBytes+48;
        }
        throw new InvalidDataException("6032测试角色不在返回名册中："+role.Name);
    }
}
