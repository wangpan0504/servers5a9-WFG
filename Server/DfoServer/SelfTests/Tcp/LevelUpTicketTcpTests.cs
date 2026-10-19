using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class LevelUpTicketTcpTests
{
    public static async Task RunAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        await LevelUpByTableTcpTests.RunAsync(options,root,resources);
        var branch=LevelUpTicketTests.BranchQuests().Select(row=>row with {Id=row.Id+400,
            Definition=row.Id==100 ? row.Definition! with {PrerequisiteGroups=[[900]]} : row.Definition! with
                {RequiredAnswers=[new(500,row.Id==101 ? 0 : 1)]}}).ToArray();
        var marker=QuestEligibilityTests.Quest(900) with {Grade="[invisible]",IntData=[-1]};
        var catalog=new QuestCatalog(LevelUpTicketTests.PlainQuests().Concat(branch).Append(marker).ToDictionary(row=>row.Id));
        var clock=new ConsumableUseSessionTests.FakeClock();
        await using var fixture=new TcpTestFixture(options,Path.Combine(root,"levelup-ticket"),resources:resources,questCatalog:catalog,clock:clock);
        var repository=new CharacterRepository(fixture.Database,catalog);
        var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var created=new List<Character>();
        using(var owner=new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var candidate=CharacterCreation.Prepare("LevelTicket",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var learning=SkillPointLedger.Synchronize(0,0,3,candidate.InitialLearning with
                {Experience=growth.GetLevelStartExperience(3)+123,BonusSp=17,BonusTp=5},resources.Learning);
            foreach(var (name,completed) in new (string,int[])[] {("LevelTicket",[]),("BranchTicket",[900]),("LevelRoll",[]),("EmptyTicket",[100,101])})
                created.Add(repository.Create(owner,candidate with
                {
                    Name=name,Level=3,InitialLearning=learning,CompletedQuests=completed,
                    Inventory=candidate.Inventory with {Items=candidate.Inventory.Items.Add(new(0,new(Game.Inventory.Models.InventoryListType.Main,65),
                        new(LevelUpTicketTests.TicketId,GameWorld.Items.ItemKind.Consumable,1)))
                        .Add(new(0,new(Game.Inventory.Models.InventoryListType.Main,66),new(LevelUpTicketTests.TicketId,GameWorld.Items.ItemKind.Consumable,1)))}
                }));
        }
        var candidate9=CharacterCreation.Prepare("LevelTicket9",resources.Characters.Get(9),resources.Towns.Room,
            resources.Towns.InitialReturn,resources.Learning.Get);
        var state9=SkillPointLedger.Synchronize(9,candidate9.GrowType,3,candidate9.InitialLearning with
            {Experience=growth.GetLevelStartExperience(3),BonusSp=17,BonusTp=5},resources.Learning);
        Character job9;
        using(var owner9=new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
            job9=repository.Create(owner9,candidate9 with
            {
                Level=3,InitialLearning=state9,
                Inventory=candidate9.Inventory with {Items=candidate9.Inventory.Items.Add(new(0,new(Game.Inventory.Models.InventoryListType.Main,65),
                    new(LevelUpTicketTests.TicketId,GameWorld.Items.ItemKind.Consumable,1)))}
            });
        await fixture.StartAsync();
        using(var peer=Open(fixture))
        {
            peer.Send(418,[65,0,0]);peer.Expect(1,418,[0,99,0]);
            peer.Select(created[0].Slot);
            string before=fixture.Snapshot();
            foreach(var bad in new byte[][] {[65,0,1],[255,255,0],[67,0,0]})
            {
                peer.Send(418,bad);peer.Expect(1,418,[0,99,0]);peer.Barrier();
                That(fixture.Snapshot()==before,"非法容器、槽位和空源明确失败，连接及完整状态保持");
            }
            long revisionBefore=Revision(created[0]);
            peer.Send(418,[65,0,0]);ExpectUsed(peer,created[0]);
            var packets=ReadCompleted(peer);
            That(packets.All(packet=>packet.Opcode!=14) && packets.Count(packet=>packet.Opcode==37)==1 &&
                packets.Count(packet=>packet.Opcode==21)==1 && packets.Count(packet=>packet.Opcode==574)==1,
                "418后仅一份完整绝对成长与任务投影，不重复发送源删除和可接列表");
            byte[] experience=packets.Single(packet=>packet.Opcode==37).Body;
            That(experience.Length==91 && experience[0]==4 && BinaryPrimitives.ReadUInt64LittleEndian(experience.AsSpan(1))==growth.GetLevelStartExperience(4) &&
                U16(experience,13)==created[0].Learning.Trees[0].Sp+growth.GetSpDelta(3,4),
                "ACK增量之后用完整37恢复实际绝对经验和两树余额");
            Verify(created[0],packets,revisionBefore);
            before=fixture.Snapshot();peer.Send(418,[65,0,0]);peer.Expect(1,418,[0,99,0]);peer.Barrier();
            That(fixture.Snapshot()==before,"已耗尽源的重复请求不再加经验或写第二条回执");
            peer.Send(418,[66,0,0]);peer.Expect(1,418,[0,99,0]);peer.Barrier();
            That(fixture.Snapshot()==before,"同模板另一源受PVF冷却约束，拒绝不消费或推进主线");
            clock.Advance(TimeSpan.FromSeconds(1));
            peer.Send(418,[66,0,0]);peer.Expect(1,418,Build(w=>
            {
                w.Write((byte)1);w.Write((ushort)66);w.Write((byte)0);
                w.Write(checked((uint)(growth.GetLevelStartExperience(5)-growth.GetLevelStartExperience(4))));
                w.Write(checked((ushort)growth.GetSpDelta(4,5)));w.Write((ushort)0);
            }));
            ReadCompleted(peer);
            That(fixture.Query("SELECT level FROM characters WHERE id=$p0",created[0].Id) is [[5L]],
                "单调时钟到期后第二张正常升一级，不复用旧回执");
        }
        using(var peer=Open(fixture))
        {
            peer.Select(created[1].Slot,prepared:true);
            long revisionBefore=Revision(created[1]);
            string before=fixture.Snapshot();
            peer.Send(418,[65,0,0]);peer.Expect(0,1381,[1,244,1,0,0]);peer.Barrier();
            That(fixture.Snapshot()==before,"真实1381建立mode2前不消费、不写入部分主线");
            var answer=Build(w=>{w.Write((byte)1);w.Write(500u);w.Write(1u);});
            peer.Send(1431,answer);peer.Expect(1,1431,[1]);ExpectUsed(peer,created[1]);
            var packets=ReadCompleted(peer);Verify(created[1],packets,revisionBefore);
            var table=packets.Last().Body;
            That(table[4+500]==2 && table[4+502]==1 && table[4+501]==0,"问题答案和所选分支最后由356恢复");
            before=fixture.Snapshot();
            peer.Send(1431,answer);peer.Expect(1,1431,[1]);
            That(ReadCompleted(peer).All(packet=>packet.Kind!=1 || packet.Opcode!=418) && fixture.Snapshot()==before,
                "重复最终回答允许绝对投影，不再次发送418经验和点数增量");
        }
        fixture.Sql($"CREATE TRIGGER level_tcp_abort AFTER INSERT ON character_quest_skip_receipts WHEN NEW.character_id={created[2].Id} BEGIN SELECT RAISE(ABORT,'level tcp rollback'); END");
        string failedBefore=fixture.Snapshot();
        using(var peer=Open(fixture))
        {
            peer.Select(created[2].Slot);failedBefore=fixture.Snapshot();peer.Send(418,[65,0,0]);peer.Closed();
        }
        That(fixture.Snapshot()==failedBefore,"晚期SQL失败不发送成功ACK，回滚券、等级、技能与任务");
        fixture.Sql("DROP TRIGGER level_tcp_abort");
        using(var peer=Open(fixture))
        {
            peer.Select(created[2].Slot);peer.Send(418,[65,0,0]);ExpectUsed(peer,created[2]);ReadCompleted(peer);
            That(fixture.Query("SELECT level FROM characters WHERE id=$p0",created[2].Id) is [[4L]],"重连后可用原券正常提交");
        }
        using(var peer=Open(fixture))
        {
            peer.Select(created[3].Slot,prepared:true);peer.Send(418,[65,0,0]);ExpectUsed(peer,created[3]);ReadCompleted(peer);
            That(fixture.Query("SELECT level FROM characters WHERE id=$p0",created[3].Id) is [[4L]],"没有可跳过主线仍能正常升级");
        }
        using(var peer=Open(fixture))
        {
            var stored=StoredCombos(fixture,job9.Id);
            peer.Select(job9.Slot,combos:stored);
            peer.Send(418,[65,0,0]);
            That(peer.Expect(1,418).Body is [1,65,..],"job9升级券消费后ACK确认源槽");
            var packets=ReadCompleted(peer);
            // 连招通知首字节与登录初始化一致为0；写1会让客户端重建自动排列。
            var expected=stored.ToArray();
            var combos=packets.Where(packet=>packet is {Kind:0,Opcode:447}).ToArray();
            That(combos.Length==2 && combos.All(packet=>packet.Body.SequenceEqual(expected)),
                "job9升级后同批补发已存连招，并在客户端重建完成后再次回放");
            int info=packets.FindIndex(packet=>packet is {Kind:0,Opcode:19});
            That(info<0 || packets[info+1] is {Kind:0,Opcode:447},"完整技能树回放后紧跟连招通知");
            // 完整NOTI19后客户端重建六列并自动回传默认排列；重建窗口内的508只确认、不覆盖存档。
            peer.Send(508,[0,6,118,0,0,119,0,0,120,0,0,121,0,0,122,0,0,123,0,0,0]);
            peer.Expect(1,508,[1]);
            That(StoredCombos(fixture,job9.Id).SequenceEqual(stored),"重建回传的自动508不覆盖已存连招");
        }
        await fixture.StopAsync();await fixture.StartAsync();
        using(var peer=Open(fixture))
        {
            peer.Select(created[0].Slot,prepared:true);
            That(peer.CompletedQuestHistory[4+100]==1 && peer.CompletedQuestHistory[4+101]==1 &&
                fixture.Query("SELECT level,experience FROM characters WHERE id=$p0",created[0].Id) is [[5L,var xp]] &&
                Convert.ToUInt64(xp)==growth.GetLevelStartExperience(5),"服务实例重建恢复等级经验和跳过完成位，不补发升级");
            peer.Barrier();
        }

        void ExpectUsed(TcpPeer peer,Character before) => peer.Expect(1,418,Build(w=>
        {
            w.Write((byte)1);w.Write((ushort)65);w.Write((byte)0);
            w.Write(checked((uint)(growth.GetLevelStartExperience(4)-before.Learning.Experience)));
            w.Write(checked((ushort)growth.GetSpDelta(3,4)));w.Write((ushort)0);
        }));
        static List<WirePacket> ReadCompleted(TcpPeer peer)
        {
            var packets=new List<WirePacket>();
            while(packets.Count<20)
            {
                var packet=peer.Read();packets.Add(packet);
                if(packet is {Kind:0,Opcode:356}) {peer.Barrier();return packets;}
            }
            throw new InvalidDataException("升级券缺少最终完成任务投影");
        }
        long Revision(Character character)=>Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0",character.Id)[0][0]);
        void Verify(Character before,IReadOnlyList<WirePacket> packets,long revisionBefore)
        {
            var published = packets.ToList();
            int player = published.FindLastIndex(packet => packet is { Kind: 0, Opcode: 2 } && packet.Body[0] == 1);
            int skills = published.FindIndex(packet => packet is { Kind: 0, Opcode: 19 });
            That(player >= 0 && skills > player && packets.Count(packet => packet is { Kind: 0, Opcode: 19 }) == 1,
                "升级券在Player后只重装一次完整技能，避免后续装备刷新覆盖被动状态");
            var table=packets.Last().Body;
            That(table.Length==30004 && table[4+100]==1 && table[4+101]==1 && table[4+102]==0 && table[4+103]==0 &&
                fixture.Query("SELECT level,revision FROM characters WHERE id=$p0",before.Id) is [[4L,var revision]] &&
                Convert.ToInt64(revision)==revisionBefore+1 &&
                fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=65",before.Id) is [[0L]],
                "主线手册截止、支线保留、一次revision和源耗尽与实收投影一致");
        }
    }
}
