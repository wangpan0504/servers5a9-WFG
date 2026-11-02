using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class MileageTcpTests
{
    public static async Task RunAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        var clock=new ConsumableUseSessionTests.FakeClock();
        await using var fixture=new TcpTestFixture(options,Path.Combine(root,"mileage"),resources:resources,clock:clock);
        var assets=resources.ItemResources;
        foreach (var (id,amount) in new[] { (10006267,100),(490705399,400),(10088338,30),(10100105,10) })
            That(assets.Items.ActionConsumable(id) is { Label:"[add mileage]",Parameters:[var parsed] } && parsed==amount,
                "不同礼盒面额来自当前PVF动作，而非按物品ID硬编码数量");
        var repo=new CharacterRepository(fixture.Database);
        var owners=new AccountOwners();
        using (var owner=owners.Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var profile=resources.Characters.Get(0);
            var starter=InventoryRewardGrantService.CreateStarter(profile);
            var current=repo.Create(owner,new("MileageOne",resources.Towns.Room,resources.Towns.InitialReturn)
            {
                Level=60,AcceptedStoryLevel=1,Skills=profile.Skills,
                Inventory=starter with { Items=[..starter.Items,Item(65,10006267,2),Item(66,490705399,2),Item(67,10002998,1)] }
            });
            current=InventoryService.Commit(repo,owner,current,new CurrencyService(assets.GoldLimits).Grant(current,CurrencyKind.Mileage,123));
            repo.Create(owner,new("MileageTwo",resources.Towns.Room,resources.Towns.InitialReturn)
                { Level=60,AcceptedStoryLevel=1,Skills=profile.Skills,Inventory=starter });
            var overflow=current with { Inventory=current.Inventory with { AccountWallet=current.Inventory.AccountWallet.Set(CurrencyKind.Mileage,int.MaxValue-99) } };
            That(!new InventoryMileageService(assets.Items,resources.Towns,clock).Plan(overflow,new(InventoryListType.Main,65),true).Accepted &&
                overflow.Inventory.Find(new(InventoryListType.Main,65))!.Core.Count==2,"有成本礼盒超出余额存储范围时整笔拒绝，不饱和丢弃部分面额");
        }
        await fixture.StartAsync();
        using (var peer=Open(fixture))
        {
            CheckInitial(peer,0,123); peer.Barrier();
            long walletRevisionBeforeUse=(long)fixture.Query("SELECT revision FROM account_wallets").Single().Single();
            Use(peer,65); Ack(peer,65); Change(peer,123,223); peer.Barrier();
            That(Count(65)==1 && fixture.Query("SELECT mileage,revision FROM account_wallets") is [[223L,long savedWalletRevision]] &&
                savedWalletRevision==checked(walletRevisionBeforeUse+1),
                "扣礼盒、积分、账号revision、角色和冷却同笔保存");
            string before=State(); Use(peer,65); Rejected(peer,65); peer.Barrier();
            That(State()==before,"同冷却期重复使用拒绝，既不重复扣源也不重复增加积分");
            clock.Advance(TimeSpan.FromSeconds(10));
            Use(peer,65); Ack(peer,65); Change(peer,223,323); peer.Barrier();
            That(Count(65)==0,"最后一个礼盒由ACK515清源，不重复发送14或假造获得消息");
            before=State(); Use(peer,65); Rejected(peer,65); peer.Barrier();
            That(State()==before,"源已耗尽的请求不补发积分");
            Use(peer,66); Ack(peer,66); Change(peer,323,723); peer.Barrier();
            before=State(); Use(peer,67); Rejected(peer,67); peer.Barrier();
            That(State()==before && Count(67)==1,"租赁点道具不能伪造动作72兑换成商城积分");
            fixture.Sql("CREATE TRIGGER mileage_abort AFTER INSERT ON currency_audit_log WHEN NEW.action_name='mileage_item' BEGIN SELECT RAISE(ABORT,'mileage rollback'); END");
            before=State(); Use(peer,66); Rejected(peer,66); peer.Barrier();
            That(State()==before,"积分及源已写入后的审计失败整体回滚，无成功ACK、余额通知或在线快照替换");
            fixture.Sql("DROP TRIGGER mileage_abort");
            Use(peer,66); Ack(peer,66); Change(peer,723,1123); peer.Barrier();
            That(Count(66)==0 && fixture.Query("SELECT currency_kind,balance_after FROM currency_audit_log WHERE action_name='mileage_item' ORDER BY audit_id DESC LIMIT 1")
                is [[19L,1123L]],"实际积分独立审计，不修改点券、代币券或租赁点");
        }
        using (var peer=Open(fixture)) { CheckInitial(peer,1,1123); peer.Barrier(); }
        using (var peer=Open(fixture)) { CheckInitial(peer,0,1123); peer.Barrier(); }
        That(fixture.Query("SELECT count(*) FROM account_currency_balances") is [[0L]],"账号积分只有钱包列一个真源，不复制到其他货币行或角色实体");

        void CheckInitial(TcpPeer peer,ushort index,int expected)
        {
            var entry=peer.Select(index).Single(row=>row is { Kind:0,Opcode:427 });
            Bytes(entry.Body,Build(w=> { w.Write((byte)0);w.Write(expected);w.Write(0); }),"换角及重连427恢复同一账号的真实积分");
        }
        InventoryItem Item(short slot,int id,int count)=>new(0,new(InventoryListType.Main,slot),new(id,GameWorld.Items.ItemKind.Consumable,count));
        void Use(TcpPeer peer,ushort slot)=>peer.Send(515,Build(w=> { w.Write(slot);w.Write((byte)0);w.Write(0u);w.Write(72u);w.Write(0u);w.Write(0u); }));
        void Ack(TcpPeer peer,ushort slot)=>peer.Expect(1,515,Build(w=> { w.Write((byte)1);w.Write(slot);w.Write((byte)0);w.Write(72u); }));
        void Rejected(TcpPeer peer,ushort slot)=>peer.Expect(1,515,Build(w=> { w.Write((byte)0);w.Write((ushort)0);w.Write(slot);w.Write((byte)0);w.Write((byte)72); }));
        void Change(TcpPeer peer,int before,int after)=>peer.Expect(0,537,Build(w=> { w.Write((byte)0);w.Write(before);w.Write(after);w.Write(0); }));
        int Count(short slot)=>fixture.Query("SELECT json_extract(i.item_core,'$.Value') FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
            "WHERE s.list_type=0 AND s.slot_index=$p0",slot) is [[var value]] ? Convert.ToInt32(value) : 0;
        string State()=>JsonSerializer.Serialize(new
        {
            Roles=fixture.Query("SELECT id,revision FROM characters ORDER BY id"),Wallets=fixture.Query("SELECT * FROM account_wallets ORDER BY account"),
            Items=fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"),
            Slots=fixture.Query("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index"),
            States=fixture.Query("SELECT * FROM character_consumable_states ORDER BY character_id,kind,item_id"),
            InventoryAudit=fixture.Query("SELECT * FROM inventory_audit_log ORDER BY audit_id"),
            CurrencyAudit=fixture.Query("SELECT * FROM currency_audit_log ORDER BY audit_id")
        });
    }
}
