using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static void VerifyTrainingItems(TcpTestFixture fixture, TcpTestResources resources)
    {
        ConsumableUseTests.RunResources(resources.ItemResources);
        Inventory.SkillMaterialTests.Run();
        ushort slot = checked((ushort)Convert.ToInt32(fixture.Query("SELECT count(*) FROM characters")[0][0]));
        ushort id;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            var candidate = CharacterCreation.Prepare("TrainItems",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
            var learning = SkillPointLedger.Synchronize(0,0,16,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(16) },resources.Learning);
            var character = repository.Create(owner,candidate with { Level = 16,InitialLearning = learning,
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                    .Add(new(0,new(InventoryListType.Main,65),InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1107),1)))
                    .Add(new(0,new(InventoryListType.Main,66),InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1065),3)))
                    .Add(new(0,new(InventoryListType.Main,67),InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1000),6))) } });
            id = character.Id;
            var inventory = new InventoryService(resources.ItemResources.Items,resources.ItemResources.GoldLimits,resources.ItemResources.EpicPieces);
            character = InventoryService.Commit(repository,owner,character,inventory.Grant(character,[new(3037,20),new(3036,10)]));
            InventoryService.Commit(repository,owner,character,new CurrencyService(resources.ItemResources.GoldLimits).Grant(character,CurrencyKind.Cera,200));
        }
        using (var peer = Open(fixture))
        {
            byte[] main = peer.Select(slot)[4].Body;
            peer.Send(15,Convert.FromHexString("88130000")); peer.Expect(1,15,[1]); peer.Expect(0,3);
            peer.Send(16,Convert.FromHexString("881300000000000001FFFF0000000000000000000000000000"));
            peer.Expect(0,27,new byte[42]); peer.Expect(0,28); peer.Expect(0,29); Load(peer); peer.Barrier();
            string before = Assets();
            foreach (var item in new[] { (Slot: (ushort)65,Id: 1107), (Slot: (ushort)66,Id: 1065) })
            {
                Use(peer,item.Slot,item.Id); Accepted(peer,item.Slot,item.Id);
                int offset = Enumerable.Range(0,U16(main,3)).Select(index => 5 + index * 123).Single(p => U16(main,p) == item.Slot);
                peer.Expect(0,1681,[0,1,0,..main.AsSpan(offset,123).ToArray()]); peer.Barrier();
                That(Assets() == before,"修炼场药效成功ACK后1681完整绝对源行静默还原，无获得提示14；最后1件及多件原UID/元数据/数量/revision/审计不变");
            }
            peer.Send(18,Convert.FromHexString("1000000010001A0A080210E60218DD172005200001"));
            peer.Expect(1,18,Convert.FromHexString("010D0000000800120708E602100018021800")); peer.Barrier();
            That(Assets() == before,"修炼场无色删除量0，不写库存、钱包或审计");
            peer.Send(18,Convert.FromHexString("0E00000010001A080802104318062001200001"));
            peer.Expect(1,18,Convert.FromHexString("010C000000080012060843100118021800")); peer.Barrier();
            peer.Send(18,Convert.FromHexString("1000000010001A0A080210E50218DC172005200001"));
            peer.Expect(1,18,Convert.FromHexString("010D0000000800120708E502100518021800")); peer.Barrier();
            That(ItemCount(67) == 5 && fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=6") is [[5L]] &&
                fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=7") is [[20L]],
                "修炼场实体技能材料及蓝色晶体照常扣除，无色20保持");
            before = Assets();
            byte[] key = [(byte)id,(byte)(id >> 8)];
            DieAndRevive(peer,key,0);
            That(Assets() == before,"修炼场零币可复活，保持角色及账号完整资产");
            peer.Send(64,Convert.FromHexString("0000010000FF00A48601000000000000000000"));
            byte[] ack = new byte[24]; ack[0] = 1; ack.AsSpan(2,4).Fill(0xFF);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(6),100004);
            peer.Expect(1,64,ack); peer.Expect(0,14,CoinBalance(1));
            peer.Expect(0,53,[1,100,0,0,0,0,0,0,0,0,0,0,0]); peer.Barrier();
            That(fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=15") is [[100L]] &&
                fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0",id) is [[1L]],
                "修炼场实收CMD64购买仍扣100点券并发1复活币，免消耗不包围付费事务");
            before = Assets(); DieAndRevive(peer,key,1);
            That(Assets() == before,"修炼场有币复活仍保原1币及支付后100点券");
            peer.Send(18,Convert.FromHexString("1A00000010001A0A080210E60218DD1720051A080800104318052001200001"));
            peer.Expect(1,18,Convert.FromHexString("01150000000800120708E6021000180212060843100118001800"));
            byte[] delta = peer.Expect(0,14).Body; peer.Barrier();
            That(U16(delta,3) == 67 && U32(delta,9) == 4 && ItemCount(67) == 4 &&
                fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=7") is [[20L]],
                "修炼场混合无色/丢弃包只免无色，真实丢弃仍删除1件并同步原槽");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters WHERE id=$p0",id) is [[0L]], "修炼场返回不制造虚弱");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(slot); EntryTown(peer); SelectMap(peer);
            peer.Send(16,Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000"));
            peer.Expect(0,28); peer.Expect(0,29); Load(peer); peer.Expect(0,36);
            Use(peer,65,1107); Accepted(peer,65,1107); peer.Barrier();
            Use(peer,66,1065); Accepted(peer,66,1065); peer.Barrier();
            That(ItemCount(65) == 0 && ItemCount(66) == 2,"离开修炼场重登后同两种药成功消费1件，不发送还原14或泄漏豁免");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Expect(0,4,[10]); peer.Barrier();
            That(fixture.Query("SELECT stamina_until>0 FROM characters WHERE id=$p0",id) is [[1L]], "普通副本撤退保存虚弱，与修炼场区分");
        }
        Console.WriteLine("PASS 修炼场仅药剂/无色/复活币免消耗、1681静默还原、其它材料/丢弃/商城实际扣款及场外药剂正常消费TCP");

        string Assets() => fixture.Snapshot("SELECT id,revision,revive_coins FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id");
        int ItemCount(short index) => fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.list_type=0 AND s.slot_index=$p0 AND s.character_id=$p1",index,id)
            is [var row] ? JsonSerializer.Deserialize<ItemCore>((string)row[0])!.Count : 0;
        static void Use(TcpPeer peer,ushort index,int item) => peer.Send(44,Build(w =>
            { w.Write(index); w.Write((byte)0); w.Write(0u); w.Write(item); w.Write(0); }));
        static void Accepted(TcpPeer peer,ushort index,int item) => peer.Expect(1,44,Build(w =>
            { w.Write((byte)1); w.Write(index); w.Write((byte)0); w.Write(0u); w.Write(item); }));
        static void DieAndRevive(TcpPeer peer,byte[] key,int coins)
        {
            peer.Send(40,[0x74,0,4,1,0,0]); peer.Expect(0,32,[key[0],key[1],0,0]);
            peer.Send(41,key); peer.Expect(1,41,[1,key[0],key[1],0]);
            peer.Expect(0,32,[key[0],key[1],1,0]); peer.Expect(0,14,CoinBalance(coins)); peer.Barrier();
        }
    }
}
