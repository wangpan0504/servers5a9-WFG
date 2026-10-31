using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class ConsumableUseTcpTests
{
    public static async Task RunDisguiseAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int itemId = 10312386;
        var clock = new DailyBoosterTestClock(new(DateTimeOffset.UtcNow.Date, TimeSpan.Zero));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root,"consumable-disguise"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        foreach (string account in new[] { fixture.Options.DevelopmentAccount, "beta" })
        {
            using var owner = new AccountOwners().Acquire(account, () => { });
            var candidate = CharacterCreation.Prepare(account == "beta" ? "OrcWitness" : "OrcPotion",
                resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
            var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
            var learning = SkillPointLedger.Synchronize(0,0,20,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(20) },resources.Learning);
            var items = candidate.Inventory.Items;
            if (account != "beta")
                foreach (short slot in new short[] {65,66,67})
                    items = items.Add(new(0,new(InventoryListType.Main,slot),
                        InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(itemId),3)));
            repository.Create(owner,candidate with { Level=20,InitialLearning=learning,Inventory=candidate.Inventory with { Items=items } });
        }
        await fixture.StartAsync();
        using var source = Open(fixture);
        using var witness = Open(fixture,"beta");
        source.Select(); witness.Select(); source.Barrier(); witness.Barrier();
        source.Send(1426); source.Expect(0,23); source.Expect(0,24); source.Barrier();
        witness.Send(1426); witness.Batch((0,23),(0,24),(0,2),(0,3),(0,23),(0,22)); witness.Barrier();
        source.Batch((0,2),(0,3),(0,23),(0,22)); source.Barrier();

        string before = Assets();
        Send(1,60,itemId); Empty(source); Empty(witness);
        That(Assets() == before,"没有实际44扣药时不能直接用211获得形态");
        Consume(65,100);
        foreach (var invalid in new[] { (Type:(byte)2,Seconds:60,Item:itemId), (Type:(byte)1,Seconds:61,Item:itemId),
            (Type:(byte)1,Seconds:60,Item:63039) })
        {
            Send(invalid.Type,invalid.Seconds,invalid.Item); Empty(source); Empty(witness);
        }
        before = Assets();
        Send(1,60,itemId); Changed(source,1,63039); Changed(witness,1,63039);
        That(Assets() == before,"211只改变当前城镇形态，不重扣物、不写持久资产或续期");
        Send(1,60,itemId); Empty(source); Empty(witness);
        Use(source,65,101,itemId); Refused(source,101,itemId); source.Barrier(); Empty(witness);
        That(Assets() == before,"重复211和已有形态下再用44均不续期、不重扣");
        source.Send(212); Changed(source,0,0); Changed(witness,0,0);
        source.Send(212); Empty(source); Empty(witness);
        Send(1,60,itemId); Empty(source); Empty(witness);

        clock.Advance(TimeSpan.FromSeconds(60));
        Consume(65,102);
        source.Send(212); Empty(source); Empty(witness);
        Send(1,60,itemId); Empty(source); Empty(witness);
        That(Count(65)==1,"212在变身兑现前撤销资格，迟到211不能复活形态，也不返还已消费药剂");

        clock.Advance(TimeSpan.FromSeconds(60));
        Consume(65,103);
        clock.Advance(TimeSpan.FromSeconds(60));
        Send(1,60,itemId); Empty(source); Empty(witness);
        That(Count(65)==0,"超过原使用截止的211拒绝，最后一瓶仍沿44单次消费");

        Consume(66,104); Send(1,60,itemId); Changed(source,1,63039); Changed(witness,1,63039);
        before = Assets();
        clock.Advance(TimeSpan.FromMilliseconds(59999)); Empty(source); Empty(witness);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        // 不向源连接发命令，让真实连接计时任务独立到期，而非用后继请求代替定时器。
        Disguise(source.Expect(0,2).Body,0,0); source.Barrier(); Changed(witness,0,0);
        That(Assets()==before,"60秒等号由独立任务清理本人和同屏形态，不重扣或产生新资产审计");

        Consume(66,105); Send(1,60,itemId); Changed(source,1,63039); Changed(witness,1,63039);
        // 上面双方已通过1426出房，直接走公共区到副本入口，不能再次套用“从房间出发”的辅助流程。
        source.Travel(38,2,1085,249,38,0,kind:5);
        _ = AntonTcpProbe.Barrier(witness,"disguise source changed town");
        source.Send(15,new byte[4]);
        var select = AntonTcpProbe.Barrier(source,"disguise selection boundary");
        That(select.Any(row=>row is { Kind:0,Opcode:27 }) && !select.Any(row=>row is { Kind:0,Opcode:14 }),
            "实际进入选图并清理城镇形态，不伪造库存变化");
        _ = AntonTcpProbe.Barrier(witness,"disguise source left town");
        Send(1,60,itemId); Empty(source); Empty(witness);
        source.Send(7); _ = AntonTcpProbe.Barrier(source,"disguise return selection");
        var restored = source.Select(); Disguise(restored[2].Body,0,0); source.Barrier(); Empty(witness);
        Send(1,60,itemId); Empty(source); Empty(witness);
        That(Count(66)==1,"退出选角/重选不保留形态或旧211资格，不补药、不改原数量");

        // 原生城镇变身不成为普通地下城内的新效果。
        witness.Send(7); _ = AntonTcpProbe.Barrier(witness,"disguise witness return selection"); Empty(source);
        FormalDungeonTcpTests.EntryTown(source);
        _ = FormalDungeonTcpTests.Enter(source); FormalDungeonTcpTests.Load(source);
        before=Assets(); Use(source,67,106,itemId); Refused(source,106,itemId); source.Barrier();
        That(Assets()==before && Count(67)==3,"正式地下城中拒绝城镇兽人药，数量与审计不变");
        using (var replacement = Open(fixture))
        {
            source.Closed(); Disguise(replacement.Select()[2].Body,0,0); replacement.Barrier();
            replacement.Send(211,Build(w=>{w.Write((byte)1);w.Write(60);w.Write(itemId);})); Empty(replacement);
            That(Count(67)==3,"同账号接管后新owner不能兑现旧连接的药剂资格，也不继承旧形态");
            Use(replacement,67,107,itemId); Accepted(replacement,67,107,itemId); replacement.Barrier();
            replacement.Send(211,Build(w=>{w.Write((byte)1);w.Write(60);w.Write(itemId);})); Changed(replacement,1,63039);
            using var next = Open(fixture);
            replacement.Closed(); Disguise(next.Select()[2].Body,0,0); next.Barrier();
            clock.Advance(TimeSpan.FromSeconds(30));
            Use(next,67,108,itemId); Accepted(next,67,108,itemId); next.Barrier();
            next.Send(211,Build(w=>{w.Write((byte)1);w.Write(60);w.Write(itemId);})); Changed(next,1,63039);
            clock.Advance(TimeSpan.FromSeconds(30)); Empty(next);
            That(Count(67)==1,"旧owner的形态到期任务不能清除新owner在不同截止新建的形态或重扣");
            clock.Advance(TimeSpan.FromSeconds(30)); Disguise(next.Expect(0,2).Body,0,0); next.Barrier();
            next.Send(212,[0]); next.Closed();
            That(Count(67)==1,"畸形212关闭原连接，不能回退、返还或重复扣除实际源");
        }

        void Send(byte type,int seconds,int id) => source.Send(211,Build(w=>{w.Write(type);w.Write(seconds);w.Write(id);}));
        void Empty(TcpPeer peer) => That(AntonTcpProbe.Barrier(peer,"disguise rejected or duplicate").Count==0,
            "拒绝和重复的211/212不制造成功ACK或多余形态通知");
        void Changed(TcpPeer peer,byte type,uint form)
        {
            var packets=AntonTcpProbe.Barrier(peer,"disguise appearance update");
            var actor=packets.Single(row=>row is { Kind:0,Opcode:2 } && row.Body[0]==0);
            That(packets.All(row=>row.Kind==0 && row.Opcode is 2 or 3 or 22 or 23),
                "形态同步只使用既有Actor及同屏状态，不回造未知211/212 ACK或扣物通知");
            Disguise(actor.Body,type,form);
        }
        void Consume(ushort slot,uint token)
        {
            long uid=Convert.ToInt64(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=$p1",source.Id,slot)[0][0]);
            var old=ReadCore(uid); long audits=Convert.ToInt64(fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0",source.Id)[0][0]);
            Use(source,slot,token,itemId); Accepted(source,slot,token,itemId); source.Barrier();
            if(old.Count>1) That(ReadCore(uid)==old with { Value=old.Count-1 },"变身药44保留真实源UID及完整Core，仅减一件");
            That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0",source.Id)[0][0])==audits+1,
                "44原子扣药只新增一次库存审计，不重复发送源14");
        }
        ItemCore ReadCore(long uid)=>System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)fixture.Query("SELECT item_core FROM character_inventory_items WHERE item_uid=$p0",uid)[0][0])!;
        int Count(short slot)=>fixture.Query("SELECT i.item_core FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=$p1",source.Id,slot)
            is [var row]?System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)row[0])!.Count:0;
        string Assets()=>fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid")+fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        static void Disguise(byte[] actor,byte type,uint form)=>That(actor.Length>=50 && actor[^50]==type && U32(actor,actor.Length-49)==form,
            "原生Actor副职业后两字段安装正确类型与怪物形态ID，不把药剂ID或后续字段当形态");
    }
}
