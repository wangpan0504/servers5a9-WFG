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
    public static async Task RunRandomDisguiseAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int itemId = 490013028;
        // 独立于产品资源解析的原脚本金值；native 02E51392..02E51546由客户端选索引并发形态ID。
        int[] forms = [58026,1083,107001765,56673,66473,56068,107013152,107013157,107013198];
        var clock = new DailyBoosterTestClock(new(DateTimeOffset.UtcNow.Date, TimeSpan.Zero));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root,"consumable-random-disguise"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        foreach (string account in new[] { fixture.Options.DevelopmentAccount, "beta" })
        {
            using var owner = new AccountOwners().Acquire(account, () => { });
            var candidate = CharacterCreation.Prepare(account == "beta" ? "CosWitness" : "CosPotion",
                resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
            var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
            var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(95) },resources.Learning);
            var items = candidate.Inventory.Items;
            if (account != "beta")
                items = items.Add(new(0,new(InventoryListType.Main,65),
                    InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(itemId),1,clock.GetUtcNow())
                        with { Marker16 = 44, SortLockFlag = 1 }));
            if (account != "beta")
                items = items.Add(new(0,new(InventoryListType.Main,66),
                    InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(10312386),3,clock.GetUtcNow())));
            if (account != "beta")
                foreach (var (slot,id) in new[] { ((short)67,490010626),((short)68,10306815) })
                    items = items.Add(new(0,new(InventoryListType.Main,slot),
                        InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(id),1,clock.GetUtcNow())));
            repository.Create(owner,candidate with { Level=95,InitialLearning=learning,Inventory=candidate.Inventory with { Items=items } });
        }
        await fixture.StartAsync();
        using var source = Open(fixture);
        using var witness = Open(fixture,"beta");
        source.Select(inventoryNotices: [(1, 713)]); witness.Select(); source.Barrier(); witness.Barrier();
        source.Send(1426); source.Expect(0,23); source.Expect(0,24); source.Barrier();
        witness.Send(1426); witness.Batch((0,23),(0,24),(0,2),(0,3),(0,23),(0,22)); witness.Barrier();
        source.Batch((0,2),(0,3),(0,23),(0,22)); source.Barrier();
        string originalSource = Source();
        string originalAssets = Assets();
        foreach (var (id,wire) in new[] { (490010626,"035D02000002F8341D"),(10306815,"035D020000FF449D00") })
        {
            witness.Send(211,Convert.FromHexString(wire)); Empty(witness); Empty(source);
            Send(2,605,id); Empty(source); Empty(witness);
            source.Send(211,Convert.FromHexString(wire)); Changed(source,3,605); Changed(witness,3,605);
            Send(3,605,id); Empty(source); Empty(witness);
            source.Send(212); Changed(source,0,0); Changed(witness,0,0);
            Send(3,605,id); Empty(source); Empty(witness);
            clock.Advance(TimeSpan.FromSeconds(10));
            Send(3,605,id); Changed(source,3,605); Changed(witness,3,605);
            clock.Advance(TimeSpan.FromSeconds(300));
            Disguise(source.Expect(0,2).Body,0,0); source.Barrier(); Changed(witness,0,0);
            That(Assets()==originalAssets,"引力恶魔药水及强者之路COS单体直接211保留源、遵守取消冷却和300秒期限，无44消费或额外审计："+id);
        }
        // 实收右键只有211，不能人工预发44制造资格。
        witness.Send(211,Convert.FromHexString("0261DD00006401351D")); Empty(witness); Empty(source);
        foreach (var invalid in new[] { (Type:(byte)1,Form:forms[0],Item:itemId),
            (Type:(byte)2,Form:300,Item:itemId), (Type:(byte)2,Form:63039,Item:itemId),
            (Type:(byte)2,Form:forms[0],Item:10312386) })
        {
            Send(invalid.Type,invalid.Form,invalid.Item); Empty(source); Empty(witness);
        }
        source.Send(211,Convert.FromHexString("0261DD00006401351D"));
        Changed(source,2,56673); Changed(witness,2,56673);
        That(Source()==originalSource && Assets()==originalAssets,
            "实收直接211由真实持有源取得形态，无44事务、库存差量或审计");
        source.Send(212); Changed(source,0,0); Changed(witness,0,0);
        Send(2,56673,itemId); Empty(source); Empty(witness);
        clock.Advance(TimeSpan.FromSeconds(10));
        for (int i=0;i<forms.Length;i++)
        {
            Send(2,forms[i],itemId); Changed(source,2,checked((uint)forms[i])); Changed(witness,2,checked((uint)forms[i]));
            That(Source()==originalSource && Assets()==originalAssets,
                "直接211免耗保留源UID、数量1及完整Core，不制造44任务使用或库存审计");
            Send(2,forms[(i+1)%forms.Length],itemId); Empty(source); Empty(witness);
            source.Send(212); Changed(source,0,0); Changed(witness,0,0);
            Send(2,forms[i],itemId); Empty(source); Empty(witness);
            clock.Advance(TimeSpan.FromMilliseconds(9999));
            Send(2,forms[i],itemId); Empty(source); Empty(witness);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            That(Source()==originalSource,"取消与同一10秒共享冷却窗口再使用不扣源、不续期或产生新形态");
        }
        Send(2,forms[8],itemId); Changed(source,2,checked((uint)forms[8])); Changed(witness,2,checked((uint)forms[8]));
        clock.Advance(TimeSpan.FromSeconds(299));
        Send(2,forms[0],itemId); Empty(source); Empty(witness);
        clock.Advance(TimeSpan.FromMilliseconds(999)); Empty(source); Empty(witness);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Disguise(source.Expect(0,2).Body,0,0); source.Barrier(); Changed(witness,0,0);
        That(Source()==originalSource && Assets()==originalAssets,
            "300秒从直接211激活开始；活动期间重放不能重置期限或消费源");
        Send(2,forms[0],itemId); Changed(source,2,checked((uint)forms[0])); Changed(witness,2,checked((uint)forms[0]));
        clock.Advance(TimeSpan.FromSeconds(300));
        Disguise(source.Expect(0,2).Body,0,0); source.Barrier(); Changed(witness,0,0);
        That(Source()==originalSource && Assets()==originalAssets,"九候选均按客户端选择校池且免耗，到期后合法新使用另起300秒");

        // 普通单次药仍走44；有效资格不能被另一无限随机源的211夺走。
        Use(source,66,400,10312386); Accepted(source,66,400,10312386); source.Barrier(); Empty(witness);
        string afterOrdinary = Assets();
        Send(2,forms[0],itemId); Empty(source); Empty(witness);
        Send(2,63039,10312386); Changed(source,2,63039); Changed(witness,2,63039);
        That(Assets()==afterOrdinary,"有效普通44资格拒绝直接随机211，且仍可由原单体211兑现");
        source.Send(212); Changed(source,0,0); Changed(witness,0,0);
        clock.Advance(TimeSpan.FromSeconds(60));

        Use(source,66,401,10312386); Accepted(source,66,401,10312386); source.Barrier(); Empty(witness);
        afterOrdinary = Assets();
        clock.Advance(TimeSpan.FromMilliseconds(59999));
        Send(2,forms[0],itemId); Empty(source); Empty(witness);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        // 截止后的第一包就是211；不得先用Barrier或其他命令清过期pending。
        Send(2,forms[0],itemId); Changed(source,2,checked((uint)forms[0])); Changed(witness,2,checked((uint)forms[0]));
        That(Source()==originalSource && Assets()==afterOrdinary,
            "普通44未兑现资格在60000ms等号失效，首个直接211立即取得独立随机源资格且免耗");
        source.Send(212); Changed(source,0,0); Changed(witness,0,0);
        clock.Advance(TimeSpan.FromSeconds(10));

        Use(source,66,402,10312386); Accepted(source,66,402,10312386); source.Barrier(); Empty(witness);
        afterOrdinary = Assets();
        clock.Advance(TimeSpan.FromMilliseconds(60001));
        Send(2,forms[1],itemId); Changed(source,2,checked((uint)forms[1])); Changed(witness,2,checked((uint)forms[1]));
        That(Source()==originalSource && Assets()==afterOrdinary,
            "普通44未兑现后越过期限的第一包直接211也不被残留pending误拒，源及审计保持原样");

        // 新连接不继承临时模型；持有的无限源可重新按真实211取得资格。
        witness.Send(7); _ = AntonTcpProbe.Barrier(witness,"random disguise witness selection");
        source.Expect(0,6,Build(writer=>writer.Write(witness.Id))); source.Barrier();
        string reconnectAssets = Assets();
        using (var replacement = Open(fixture))
        {
            source.Closed(); var restored = replacement.Select(inventoryNotices: [(1,713)]);
            That(restored.Single(row=>row is { Kind:1,Opcode:713 }).Body.AsSpan().SequenceEqual(Convert.FromHexString("01004100")),
                "重连恢复Main65原排序锁，713正文仍为成功及原槽位");
            Disguise(restored[2].Body,0,0); replacement.Barrier();
            replacement.Send(211,Convert.FromHexString("0261DD00006401351D")); Changed(replacement,2,56673);
            replacement.Send(212); Changed(replacement,0,0);
            using var next = Open(fixture);
            replacement.Closed(); var reconnected = next.Select(inventoryNotices: [(1,713)]);
            That(reconnected.Single(row=>row is { Kind:1,Opcode:713 }).Body.AsSpan().SequenceEqual(Convert.FromHexString("01004100")),
                "再次接管继续恢复同一Main65排序锁，不能把正常713当成额外形态包");
            Disguise(reconnected[2].Body,0,0); next.Barrier();
            next.Send(211,Convert.FromHexString("0204DB00006401351D")); Changed(next,2,56068);
            next.Send(212); Changed(next,0,0);
            That(Source()==originalSource && Assets()==reconnectAssets,
                "接管、重连、合法新211和取消使用固定Actor金样，保留无限源完整库存及审计");
        }

        void Send(byte type,int form,int id) => source.Send(211,Build(w=>{w.Write(type);w.Write(form);w.Write(id);}));
        void Empty(TcpPeer peer) => That(AntonTcpProbe.Barrier(peer,"random disguise rejected or idle").Count==0,
            "无效、重放和空闲随机变身不发额外ACK、库存或形态通知");
        void Changed(TcpPeer peer,byte type,uint form)
        {
            var packets=AntonTcpProbe.Barrier(peer,"client selected random disguise");
            var actor=packets.Single(row=>row is { Kind:0,Opcode:2 } && row.Body[0]==0);
            That(packets.All(row=>row.Kind==0 && row.Opcode is 2 or 3 or 22 or 23),
                "随机变身沿既有Actor同步，不新增211/212 ACK或扣物包");
            Disguise(actor.Body,type,form);
        }
        string Source()=>System.Text.Json.JsonSerializer.Serialize(fixture.Query("SELECT s.item_uid,i.item_core FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=65",source.Id));
        string Assets()=>fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid")+fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        static void Disguise(byte[] actor,byte type,uint form)
        {
            // 原生Actor消费者分支及原脚本ID的固定5B金样，独立于产品目录和Builder。
            string expected = (type,form) switch
            {
                (0,0) => "0000000000",
                (3,605) => "035D020000",
                (2,63039) => "023FF60000",
                (2,58026) => "02AAE20000",
                (2,1083) => "023B040000",
                (2,107001765) => "02A5B76006",
                (2,56673) => "0261DD0000",
                (2,66473) => "02A9030100",
                (2,56068) => "0204DB0000",
                (2,107013152) => "0220E46006",
                (2,107013157) => "0225E46006",
                (2,107013198) => "024EE46006",
                _ => throw new InvalidDataException("未登记的原生Actor形态金样")
            };
            That(actor.Length>=50 && actor.AsSpan(actor.Length-50,5).SequenceEqual(Convert.FromHexString(expected)),
                "Actor怪物类型2进入Monster.lst；取消、到期和重连只清零形态，不写入NPC类型1");
        }
    }
}
