using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestCinematicSettlementTcpTests
{
    // 复用quests隔离进程。39及33均为明确合成输入，不冒充客户端动画或ACT实发取证。
    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        foreach (var scenario in new[] { (Request:(ushort)3599,FinishFirst:false,AllEnemies:true,Potion:1,Early:false),
            (Request:(ushort)0,FinishFirst:false,AllEnemies:false,Potion:2,Early:true),
            (Request:(ushort)3599,FinishFirst:true,AllEnemies:false,Potion:0,Early:true) })
        {
            var area = resources.Towns.Areas.Values.First(row => row.Level <= 74 && row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 96 &&
                    (option.QuestId == -1 || option.InProgress && option.QuestId == 3599 || !option.InProgress && option.QuestId != 3599)));
            var done = new HashSet<int>(area.Quests) { 3598 };
            if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
            var option = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(row => row.DungeonId == 96 &&
                (row.QuestId == -1 || row.InProgress && row.QuestId == 3599 || !row.InProgress && row.QuestId != 3599));
            if (!option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(3599)).Quests);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
                checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                var candidate = CharacterCreation.Prepare("Movie"+scenario.Request+(scenario.FinishFirst ? "Y" : "N"),resources.Characters.Get(0),
                    position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,74,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(74) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                    { Level=74,AcceptedStoryLevel=74,CompletedQuests=done.ToArray(),InitialLearning=learning,
                        Inventory=scenario.Potion == 0 ? candidate.Inventory : candidate.Inventory with
                        { Items=candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,65),
                            InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(7440),1))) } });
            }
            using (var peer = Open(fixture))
            {
                peer.Select(RoleIndex(peer.Roster,created),prepared:true,resumeTown:true,room:false);
                That(peer.Id == created.Id,"演出TCP选中本条独立角色身份");
                if (scenario.Potion == 1) UsePotion();
                var map = FormalDungeonTcpTests.Enter(peer,3599,96,requestQuestId:scenario.Request);
                int enabled = 0, bossChecks = 0, potionNotices = 0;
                byte[]? clearReward = null;
                Load(76112);
                if (scenario.Potion == 2) UsePotion();
                Move(0); Load(76114);
                That(enabled == 0,"零死亡友方基础路线不凭动画目标生成31");
                var dummy = Enumerable.Range(0,map[18]).Single(at => U32(map,27+22*at) == 62959);
                ushort dummyKey = U16(map,25+22*dummy);
                RejectDummy();
                Move(1); Load(76113);
                var enemies = Enumerable.Range(0,map[18]).Where(at => map[40+22*at] == 0).ToArray();
                That(enemies.Length == 9,"TCP76113下发全部九个敌对演员");
                // 第二、三条只提交一个实际下发对象的合成39，验证其余八敌存活时的导航。
                var reported = scenario.AllEnemies ? enemies : [enemies[^1]];
                foreach (int at in reported)
                {
                    peer.Send(39,ScriptDeath(U16(map,25+22*at),peer.Id));
                    Drain();
                }
                That(enabled == 0,"真实叠层击杀不能提前许可非战斗终点结算");
                Move(1);
                if (scenario.Early)
                {
                    peer.Send(33,[33,0,15,14,0,0]); peer.Expect(1,33,[1,15,14,0,0,0,0]); Drain();
                    That(enabled == 0 && fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3599",peer.Id)
                        is [[0L]] && fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                        "3599返回Loading早到33保存原接取但不提前发布31或结算");
                }
                Load(76114,cached:true);
                That(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id)
                    is [[long deathCount]] && deathCount == reported.Length,
                    "APC演出返回只保留实际39回执，不替其余八敌创建死亡或经验");
                That(enabled == (scenario.Early ? 1 : 0) && fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3599",peer.Id)
                    is [[long scalar]] && scalar == (scenario.Early ? 0 : 1),"返回原演出终点按实际37与已存33共同决定许可");
                RejectDummy();
                byte[] report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
                string before = State();
                if (!scenario.Early)
                {
                    peer.Send(46,report); Drain();
                    That(State() == before && enabled == 0,"TCP提前46不能绕过演出许可或提交奖励");
                }
                peer.Send(33,[33,0,15,14,0x10,0,0]); peer.Expect(1,33,[0,19,0]); Drain();
                That(State() == before && enabled == (scenario.Early ? 1 : 0),"演出错误通道33不持久达成或发布31");
                peer.Send(33,[33,0,15,14,0,0]); peer.Expect(1,33,[1,15,14,0,0,0,0]);
                if (!scenario.Early) { peer.Expect(0,31,[]); enabled++; }
                Drain();
                That(enabled == 1 && bossChecks == 0 && fixture.Query(
                    "SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3599",peer.Id) is [[0L]],
                    "终末33先持久原接取再ACK33及唯一空31，不编造115");
                before = State();
                peer.Send(33,[33,0,15,14,0,0]); peer.Expect(1,33,[1,15,14,0,0,0,0]); Drain();
                That(State() == before && enabled == 1,"成功许可后重复33不改revision或重发31");
                if (scenario.FinishFirst) Finish();
                peer.Send(46,report); Drain();
                That(fixture.Query("SELECT quest_id,map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                    is [[long quest,76114L]] && quest == scenario.Request,
                    "46沿原事务结算真实终点并保留显式或普通0实例请求号");
                before = State(); peer.Send(46,report); Drain();
                That(State() == before,"重复46不重发奖励或提交第二份通关回执");
                uint expectedPotion = scenario.Potion == 0 ? 0 : SelfTestCatalogs.ClearExperience(resources.Archive)
                    .Calculate(SelfTestCatalogs.Dungeons(resources.Archive).Get(96),0,99).TotalExperience;
                That(clearReward is not null && U32(clearReward,21) == expectedPotion &&
                    (scenario.Potion == 0 || potionNotices > 0),
                    "3599图外/图内秘药和无药对照均在真实33许可后收到35，秘药分项按已到账经验保留");
                peer.Send(69); peer.Expect(1,69,[1]);
                peer.Send(70); That(peer.Expect(1,70).Body.Length == 17,"演出终点保留八席翻牌资格");
                peer.Send(71,[0,0]); That(peer.Expect(1,71).Body[0] == 1,"演出终点实际领取免费牌"); Drain();
                That(fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",peer.Id) is [[1L]],
                    "演出终点的秘药不影响免费牌领取落库");
                if (!scenario.FinishFirst) Finish();
                before = State();
                peer.Send(34,[34,0,15,14,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); Drain();
                That(State() == before,"演出任务重复34不重复交付");
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();

                void UsePotion()
                {
                    peer.Send(44,Build(writer => { writer.Write((ushort)65);writer.Write((byte)0);writer.Write(0u);writer.Write(7440);writer.Write(0); }));
                    That(peer.Expect(1,44).Body[0] == 1,"3599图外或首房真实44成功消费秘药");
                    drain(peer,null);
                    That(fixture.Query("SELECT permille FROM character_exp_bonus WHERE character_id=$p0",peer.Id) is [[1000L]],
                        "演出路线秘药效果实际持久化为1000千分比");
                }
                void Drain() => drain(peer,packet =>
                {
                    if (packet.Opcode == 31) { Bytes(packet.Body,[],"演出许可31保持空正文"); enabled++; }
                    if (packet.Opcode == 115) bossChecks++;
                    if (packet.Kind == 0 && packet.Opcode == 35) clearReward = packet.Body;
                    if (packet.Kind == 0 && packet.Opcode == 37 && U32(packet.Body,87) > 0) potionNotices++;
                });
                void Load(uint expected,bool cached = false)
                {
                    if (cached)
                        That(expected == 76114 && map.Length == 16 && map[0] == 1 && map[1] == 0 && map[2] == 2 &&
                            map[13] == 0 && U32(map,9) == 1 && map[14] == 0 && map[15] == 255,
                            "3599返回基础76114使用RestoreBase/Cached，不重发MAP或演员");
                    else That(U32(map,14) == expected,"3599按真实MAP路线下发："+expected);
                    FormalDungeonTcpTests.Load(peer); Drain();
                }
                void Move(byte kind)
                {
                    var move = new byte[100]; move[0] = 1; move[10] = kind;
                    peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                }
                void RejectDummy()
                {
                    string saved = State(); int previousEnabled = enabled;
                    peer.Send(39,ScriptDeath(dummyKey,peer.Id));
                    peer.Expect(0,38,[(byte)dummyKey,(byte)(dummyKey >> 8),0,0,0,0,255,0,0]); Drain();
                    That(State() == saved && enabled == previousEnabled,"62959友方退场不计死亡、发经验或创建结算");
                }
                void Finish()
                {
                    peer.Send(34,[34,0,15,14,255,255,1,0,255,255]);
                    var reply = peer.Expect(1,34).Body;
                    That(reply.Length >= 4 && reply[0] == 1 && U16(reply,1) == 3599,"真实34完成演出任务");
                    Drain();
                }
            }
            using (var peer = Open(fixture))
            {
                peer.Select(RoleIndex(peer.Roster,created),prepared:true); peer.Barrier();
                That(peer.CompletedQuestHistory[4+3599] == 1 && fixture.Query(
                    "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3599",peer.Id) is [[1L]] &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                    "返回城镇重连恢复唯一演出交付及通关凭据");
                string before = State(); peer.Send(39,ScriptDeath(100,peer.Id)); drain(peer,null);
                That(State() == before,"重连迟到39不能复活旧演出实例");
            }
            string State() => fixture.Snapshot()+fixture.Snapshot("SELECT * FROM character_active_main_quests ORDER BY character_id,slot")+
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts ORDER BY character_id,event_id")+
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts ORDER BY character_id,instance_id")+
                fixture.Snapshot("SELECT * FROM character_quest_completion_receipts ORDER BY character_id,quest_id")+
                fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid")+
                fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind");
        }
    }

    static byte[] ScriptDeath(ushort key,ushort reporter)
    {
        var body = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(body,key);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),reporter); body[31] = 0x22;
        FormalDungeonTcpTests.SealDeath(body); return body;
    }

    static ushort RoleIndex(byte[] roster,Character role)
    {
        int at = 15;
        for (ushort ordinal = 0; ordinal < U16(roster,13); ordinal++)
        {
            int width = checked((int)U32(roster,at+2));
            if (U16(roster,at) == role.Slot && DecodeName(roster,at+6,width) == role.Name) return ordinal;
            int equipment = at+21+width;
            at = equipment+AssetWireChecks.ActorEntries(roster,equipment).ConsumedBytes+48;
        }
        throw new InvalidDataException("演出测试角色不在返回名册中："+role.Name);
    }
}
