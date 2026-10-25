using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class LevelUpByTableTcpTests
{
    public static async Task RunAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        var branch = LevelUpTicketTests.BranchQuests().Select(row=>row with { Id=row.Id+400,
            Definition=row.Id==100 ? row.Definition! with { PrerequisiteGroups=[[900]] } :
                row.Definition! with { RequiredAnswers=[new(500,row.Id==101 ? 0 : 1)] } }).ToArray();
        var quests = new QuestCatalog(LevelUpTicketTests.PlainQuests().Concat(branch)
            .Append(QuestEligibilityTests.Quest(900) with { Grade="[invisible]",IntData=[-1] }).ToDictionary(row=>row.Id));
        quests=TcpSyntheticQuestCatalog.WithHopeMissions(quests,resources.Quests);
        await using var fixture = new TcpTestFixture(options,Path.Combine(root,"levelup-by-table"),resources:resources,questCatalog:quests);
        var repository = new CharacterRepository(fixture.Database,quests);
        var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var created = new List<Character>();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var candidate = CharacterCreation.Prepare("TableJump",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            foreach (var (name,level,id,completed) in new (string,byte,int,int[])[]
                { ("TableJump",27,LevelUpByTableTests.Segmented,[]),("TablePlain",29,LevelUpByTableTests.Plain,[]),
                  ("TableRoll",10,LevelUpByTableTests.Segmented,[]),("TableBranch",10,LevelUpByTableTests.Segmented,[900]) })
                created.Add(repository.Create(owner,candidate with
                {
                    Name=name,Level=level,CompletedQuests=completed,
                    InitialLearning=SkillPointLedger.Synchronize(0,0,level,candidate.InitialLearning with
                        { Experience=growth.GetLevelStartExperience(level)+123,BonusSp=17,BonusTp=5 },resources.Learning),
                    Inventory=candidate.Inventory with { Items=candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,65),
                        new(id,ItemKind.Consumable,1))) }
                }));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(created[0].Slot); peer.Barrier();
            string before = fixture.Snapshot();
            foreach (byte[] request in new[] { Jump(0),Jump(6),Jump(1,reserved:1),Jump(1,second:1),Jump(1,list:1),Jump(1,action:194) })
            {
                peer.Send(515,request);
                peer.Expect(1,515,new byte[] {0,(byte)(request[7]==194 ? 0 : 19),0,65,0,request[2],request[7]});
                peer.Barrier();
                That(fixture.Snapshot()==before,"515未知动作或非法职业选择、容器、保留参数不消费源，连接仍可继续");
            }
            peer.Send(30,[65,0]); peer.Expect(1,30,[0,19,0]); peer.Barrier();
            That(fixture.Snapshot()==before,"jumping道具不能绕过193职业选择改走CMD30");
            peer.Send(515,Jump(1)); peer.Expect(1,515,[1,65,0,0,193,0,0,0]);
            var packets = ReadCompleted(peer);
            Verify(created[0],80,1,packets);
            before = fixture.Snapshot(); peer.Send(515,Jump(1)); peer.Expect(1,515,[0,19,0,65,0,0,193]); peer.Barrier();
            That(fixture.Snapshot()==before,"耗尽源重放不再次升级或返还技能点");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created[1].Slot); peer.Barrier();
            peer.Send(30,[65,0]);
            peer.Expect(1,30,Build(w=>
            {
                w.Write((byte)1);w.Write((ushort)65);w.Write((byte)1);
                w.Write(checked((uint)(growth.GetLevelStartExperience(81)-created[1].Learning.Experience)));
                w.Write(checked((ushort)growth.GetSpDelta(29,81)));w.Write(checked((ushort)growth.GetTpDelta(29,81)));
            }));
            Verify(created[1],81,0,ReadCompleted(peer));
        }
        fixture.Sql($"CREATE TRIGGER table_tcp_abort AFTER INSERT ON character_quest_skip_receipts WHEN NEW.character_id={created[2].Id} BEGIN SELECT RAISE(ABORT,'table tcp rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(created[2].Slot); peer.Barrier();
            string before = fixture.Snapshot(); peer.Send(515,Jump(2)); peer.Closed();
            That(fixture.Snapshot()==before,"193末段SQL失败关闭原连接，无提前成功ACK或部分转职成长");
        }
        fixture.Sql("DROP TRIGGER table_tcp_abort");
        using (var peer = Open(fixture))
        {
            peer.Select(created[2].Slot); peer.Send(515,Jump(2)); peer.Expect(1,515,[1,65,0,0,193,0,0,0]);
            Verify(created[2],80,2,ReadCompleted(peer));
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created[3].Slot,prepared:true); peer.Barrier(); string before = fixture.Snapshot();
            peer.Send(515,Jump(1)); peer.Expect(0,1381,[1,244,1,0,0]); peer.Barrier();
            That(fixture.Snapshot()==before,"193主线分支回答前既不扣物也不转职");
            var answer = Build(w=>{w.Write((byte)1);w.Write(500u);w.Write(1u);});
            peer.Send(1431,answer);peer.Expect(1,1431,[1]);peer.Expect(1,515,[1,65,0,0,193,0,0,0]);
            Verify(created[3],80,1,ReadCompleted(peer));
            peer.Send(1431,answer);peer.Expect(1,1431,[1]);
            That(ReadCompleted(peer).All(packet=>packet.Kind!=1 || packet.Opcode!=515),"重复最终回答只恢复绝对投影，不重复发消费515");
        }
        await fixture.StopAsync(); await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(created[0].Slot,prepared:true);peer.Barrier();
            That(fixture.Query("SELECT level,grow_type,experience FROM characters WHERE id=$p0",created[0].Id) is [[80L,1L,var xp]] &&
                Convert.ToUInt64(xp)==growth.GetLevelStartExperience(80),"服务实例重建恢复已提交等级和职业，不重放道具或经验增量");
        }

        void Verify(Character before,byte level,byte grow,IReadOnlyList<WirePacket> packets)
        {
            var exp = packets.Single(packet=>packet.Kind==0 && packet.Opcode==37).Body;
            That(exp.Length==91 && exp[0]==level && BinaryPrimitives.ReadUInt64LittleEndian(exp.AsSpan(1))==growth.GetLevelStartExperience(level) &&
                packets.All(packet=>packet.Opcode!=14) && packets.Count(packet=>packet.Kind==0 && packet.Opcode==21)==1,
                "ACK后发送真实绝对经验、两树和任务，不重复删除源或发获得effect");
            var actor = packets.Single(packet=>packet.Kind==0 && packet.Opcode==2 && packet.Body[0]==0).Body;
            int nameLength = checked((int)U32(actor,123));
            That(actor[128+nameLength]==grow && actor[129+nameLength]==level &&
                packets.Count(packet=>packet.Kind==0 && packet.Opcode==2 && packet.Body[0]==1)==1,
                "Actor安装已提交的职业和等级，Player另行刷新属性，不能只改数据库职业");
            That(fixture.Query("SELECT level,grow_type FROM characters WHERE id=$p0",before.Id) is [[var actualLevel,var actualGrow]] &&
                Convert.ToByte(actualLevel)==level && Convert.ToByte(actualGrow)==grow &&
                fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=65",before.Id) is [[0L]] &&
                packets.Last().Body[4+100]==1 && packets.Last().Body[4+103]==0,
                "实际保存与成长投影一致，只跳主线，不误完成支线");
        }
        static List<WirePacket> ReadCompleted(TcpPeer peer)
        {
            var packets = new List<WirePacket>();
            while (packets.Count<24)
            {
                var packet=peer.Read();packets.Add(packet);
                if (packet is { Kind:0,Opcode:356 }) { peer.Barrier(); return packets; }
            }
            throw new InvalidDataException("按表升级缺少最终完成表");
        }
    }

    static byte[] Jump(uint grow,uint reserved=0,uint second=0,byte list=0,uint action=LevelUpByTableDefinition.JumpingAction) => Build(w=>
    {
        w.Write((ushort)65);w.Write(list);w.Write(reserved);w.Write(action);w.Write(grow);w.Write(second);
    });
}
