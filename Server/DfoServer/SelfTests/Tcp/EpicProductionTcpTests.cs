using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EpicProductionTcpTests
{
    public static async Task RunAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        await using var f=new TcpTestFixture(source,Path.Combine(root,"epic-production"),resources:resources);
        var items=resources.ItemResources;
        var production=new EpicProductionCatalog(resources.Archive);
        int weapon=production.Weapons["[swordman]"][0];
        var repo=new CharacterRepository(f.Database);var owners=new AccountOwners();
        Character created;
        using(var owner=owners.Acquire(f.Options.DevelopmentAccount,()=>{}))
        {
            var candidate=CharacterCreation.Prepare("EpicMake",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
            var learning=SkillPointLedger.Synchronize(0,0,86,candidate.InitialLearning with
                { Experience=growth.GetLevelStartExperience(86) },resources.Learning);
            created=repo.Create(owner,candidate with { Level=86,InitialLearning=learning,AcceptedStoryLevel=1 });
            var grant=InventoryRewardGrantService.TryGrantBatch(created,[new(3137,51),new(3141,25),new(3012,751),new(10163130,3),new(10163135,253),new(10158124,30),new(10163199,1),new(3168,150),new(10099774,570)],
                items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
            That(grant.Accepted,"制作TCP材料夹具合法");created=repo.CommitInventory(owner,created,grant.Plan!);
        }
        f.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,10023),($p0,10025)",created.Id);
        File.WriteAllText(Path.Combine(f.Options.DataDirectory,"epic-production-compatibility.json"),"{\"GreatSuccessPercent\":0}");
        await f.StartAsync();
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);That(BinaryPrimitives.ReadInt32LittleEndian(peer.EpicProductionState.AsSpan(8))==-1,"初始无制作目标使用客户端-1哨兵");
            // 本版普通配方允许神器灵魂；它位于账号钱包虚拟槽363，不是实体背包行。
            peer.Send(1420,Build(w=>{w.Write(10163130);w.Write((ushort)1);w.Write(10099774);w.Write(363u);w.Write(500);}));
            Mutation(peer,1420,body=>Bytes(body,[1,1,0,0,0,2,0,0,0,0],"500个神器灵魂按稀有度积分制作普通催化剂"));
            State(peer,-1,0,0,0);
            using(var verify=owners.Acquire(f.Options.DevelopmentAccount,()=>{}))
                That(CurrencyWallet.Get(repo.List(verify).Single().Inventory,CurrencyKind.UniqueSoul)==70,
                    "普通制作真实扣除账号钱包500个灵魂");
            short ordinary=Slot(3012);
            peer.Send(1420,Build(w=>{w.Write(10163130);w.Write((ushort)1);w.Write(3012);w.Write((uint)ordinary);w.Write(750);}));
            Mutation(peer,1420,body=>Bytes(body,[1,1,0,0,0,2,0,0,0,0],"750个普通材料制作一份且不多扣整堆"));
            State(peer,-1,0,0,0);
            using(var verify=owners.Acquire(f.Options.DevelopmentAccount,()=>{}))
                That(repo.List(verify).Single().Inventory.Items.Single(item=>item.Core.ItemId==3012).Core.Count==1,
                    "普通制作实体材料落库剩余一件");
            peer.Send(1417,Build(w=>w.Write(weapon)));
            peer.Expect(1,1417,Build(w=>{w.Write((byte)1);w.Write(weapon);w.Write(0);}));
            State(peer,weapon,0,0,0);
            foreach(ushort id in new ushort[]{10024,10026})
            {
                peer.Send(31,Build(w=>{w.Write((ushort)31);w.Write(id);}));
                var accepted=peer.Expect(1,31).Body;
                That(accepted[0]==1 && U16(accepted,1)==id && U32(accepted,3)==1,"两条实际加工任务均按未完成目标接取");
                QuestLifecycleTcpTests.DrainNotifications(peer);
            }
            peer.Send(33,[33,0,40,39,0,0]);
            peer.Expect(1,33,[0,19,0]);
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=10024",created.Id) is [[1L]],
                "拒绝客户端归零且保留未完成加工目标");
            QuestLifecycleTcpTests.DrainNotifications(peer);
            short slot=Slot(3137);
            short leather=Slot(3141);
            peer.Send(1420,Build(w=>{w.Write(10163131);w.Write((ushort)2);w.Write(3137);w.Write((uint)slot);w.Write(26);w.Write(3141);w.Write((uint)leather);w.Write(25);}));
            Mutation(peer,1420,body=>Bytes(body,[1,1,0,0,0,1,1,0,0,0],"催化剂回包一份产量和一份材料余量"));
            State(peer,weapon,0,0,1);
            using(var verify=owners.Acquire(f.Options.DevelopmentAccount,()=>{}))
                That(repo.List(verify).Single().Inventory.Items.Single(item=>item.Core.ItemId==3137).Core.Count==25 &&
                    repo.List(verify).Single().Inventory.Items.All(item=>item.Core.ItemId!=3141),"中级制作混合两种材料按请求数量落库扣除");
            var rows=new[]{10163130,10163131}.Select(id=>(id,Slot(id))).ToArray();
            peer.Send(1419,Build(w=>{w.Write((ushort)rows.Length);foreach(var (id,index) in rows){w.Write(id);w.Write((uint)index);}}));
            Mutation(peer,1419,body=>Bytes(body,[1,0xb8,0x0b,0,0,1,0],"关闭兼容大成功后加工精确增加3000点"));
            State(peer,weapon,3000,1,1);
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id IN(10024,10026) ORDER BY quest_id",created.Id) is [[0L],[0L]],
                "成功事务同时推进两条实际已接取任务而非只更新客户端");
            foreach(ushort id in new ushort[]{10024,10026})
            {
                peer.Send(34,Build(w=>{w.Write((ushort)34);w.Write(id);w.Write(ushort.MaxValue);w.Write((ushort)1);w.Write(ushort.MaxValue);}));
                var result=peer.Expect(1,34).Body;That(result[0]==1,"加工任务按标准领奖入口完成");
                QuestLifecycleTcpTests.DrainNotifications(peer);
            }
            int alternate=production.Weapons["[swordman]"][1];
            short carbon=Slot(10163135);
            var change=Build(w=>{w.Write(alternate);w.Write((ushort)2);w.Write(10158124);w.Write(365u);w.Write(10163135);w.Write((uint)carbon);});
            peer.Send(1418,change);
            Mutation(peer,1418,body=>Bytes(body,Build(w=>{w.Write((byte)1);w.Write(alternate);}),"普通配方换型成功只回新武器"));
            State(peer,alternate,3000,1,1);
            peer.Send(1418,change);peer.Expect(1,1418,[0,3,0]);peer.Barrier();
            short ticket=Slot(10163199);
            peer.Send(1418,Build(w=>{w.Write(weapon);w.Write((ushort)1);w.Write(10163199);w.Write((uint)ticket);}));
            Mutation(peer,1418,body=>Bytes(body,Build(w=>{w.Write((byte)1);w.Write(weapon);}),"特殊配方单券换回且不重复扣灵魂"));
            State(peer,weapon,3000,1,1);
            peer.Send(1421,[]);peer.Expect(1,1421,[0,3,0]);peer.Barrier();
        }
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);var state=peer.EpicProductionState;
            That(U32(state,8)==weapon && U32(state,12)==3000 && state[17]==1 && U32(state,20)==1,
                "重连恢复制作目标、进度、周次数与材料余量");
            peer.Send(1417,Build(w=>w.Write(weapon)));peer.Expect(1,1417,[0,3,0]);peer.Barrier();
        }
        // 服务端持久状态边界夹具，不以SQL替代真实加工与成品网络流程。
        f.Sql("UPDATE character_epic_production SET charge=$p0,weekly_uses=0 WHERE character_id=$p1",production.MaximumCharge-500,created.Id);
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);
            short catalyst=Slot(10163130);
            peer.Send(1419,Build(w=>{w.Write((ushort)1);w.Write(10163130);w.Write((uint)catalyst);}));
            Mutation(peer,1419,body=>Bytes(body,Build(w=>{w.Write((byte)1);w.Write(production.MaximumCharge);w.Write((byte)1);w.Write((byte)0);}),
                "末次加工使用最大绝对进度且不伪造大成功"));
            peer.Expect(1,1417,Build(w=>{w.Write((byte)1);w.Write(weapon);w.Write(production.MaximumCharge);}));
            State(peer,-1,0,1,1);
            using var verificationOwner=owners.Acquire(f.Options.DevelopmentAccount,()=>{});
            That(repo.List(verificationOwner).Single().Inventory.Items.Count(item=>item.Core.ItemId==weapon && item.Uid>0)==1,
                "完整成品链只生成一个真实装备实例");
            short finished=Slot(weapon);
            foreach(var (group,option) in new (byte,byte)[]{(0,1),(1,11)})
            {
                peer.Send(1421,Build(w=>{w.Write((ushort)finished);w.Write(group);w.Write(option);}));
                bool updated=false;
                Mutation(peer,1421,body=>
                {
                    Bytes(body,[1],"属性附加先发布装备/材料变化，再回无额外载荷成功ACK");
                    That(updated,"成功ACK前真实NOTI14已携带对应装备的完整属性字段");
                },body=>
                {
                    for(int at=3;at<body.Length;at+=123)
                        if(U16(body,at)==finished)
                        {
                            That(U32(body,at+114)==(group==0?1:0x0B01),"wire114保存两组选择且不覆盖先前组");
                            updated=true;
                        }
                });
                State(peer,-1,0,1,1);
            }
            peer.Send(1421,Build(w=>{w.Write((ushort)finished);w.Write((byte)1);w.Write((byte)11);}));
            peer.Expect(1,1421,[0,3,0]);peer.Barrier();
            using var abilityVerificationOwner=owners.Acquire(f.Options.DevelopmentAccount,()=>{});
            That(repo.List(abilityVerificationOwner).Single().Inventory.Items.Single(i=>i.Core.ItemId==weapon).Core.Extension114.Value==0x0B01,
                "两组属性真实持久化于同一装备，重复选项拒绝不重扣材料");
        }
        using(var peer=Open(f))
        {
            peer.Select(prepared:true);
            using var verify=owners.Acquire(f.Options.DevelopmentAccount,()=>{});
            That(repo.List(verify).Single().Inventory.Items.Single(i=>i.Core.ItemId==weapon).Core.Extension114.Value==0x0B01,
                "断线重连后附加两组属性保持");
            peer.Barrier();
        }
        That(f.Query("PRAGMA integrity_check") is [["ok"]] && f.Query("PRAGMA foreign_key_check").Length==0,"制作TCP完整性与外键一致");
        short Slot(int id)
        {
            using var lease=owners.Acquire(f.Options.DevelopmentAccount,()=>{});
            return repo.List(lease).Single().Inventory.Items.Single(item=>item.Core.ItemId==id).Position.Index;
        }
    }
    static void State(TcpPeer peer,int weapon,int charge,byte used,int remainder)
    {
        var body=peer.Expect(0,1358).Body;
        That(body.Length==32 && U32(body,8)==unchecked((uint)weapon) && U32(body,12)==charge && body[17]==used && U32(body,20)==remainder,
            "提交后完整状态通知与真实持久状态匹配");peer.Barrier();
    }
    static void Mutation(TcpPeer peer,ushort opcode,Action<byte[]> check,Action<byte[]>? inventory=null)
    {
        int changes=0;
        for(int i=0;i<32;i++)
        {
            var packet=peer.Read();
            if(packet.Kind==1 && packet.Opcode==opcode){check(packet.Body);That(changes>0,"成功制作先发布库存变化");return;}
            // 任务权威变化有两种形态：NOTI33与延迟应答SET_QUEST_TRIGGER(kind1/33，QuestProjection.PublishConditionsAsync)。
            // 制作事务提交后背包投影先补发加工任务进度再回ACK；该顺序在成组前就存在，基线提交独立复现，不是本分支引入。
            if(packet.Kind==1 && packet.Opcode==33)
            {
                That(packet.Body.Length>=3 && U16(packet.Body,1) is 10024 or 10026,
                    "任务权威应答只能携带本次实际推进的加工任务");
                continue;
            }
            That(packet.Kind==0 && packet.Opcode is 14 or 21 or 32 or 33 or 356 or 574,"制作之前只能发布库存/任务权威变化");
            if(packet.Opcode==14){That(packet.Body.Length>=3 && packet.Body.Length==3+U16(packet.Body,1)*123,"库存差量精确行宽");inventory?.Invoke(packet.Body);changes++;}
        }
        throw new InvalidDataException("制作回包超过有界通知数");
    }
}
