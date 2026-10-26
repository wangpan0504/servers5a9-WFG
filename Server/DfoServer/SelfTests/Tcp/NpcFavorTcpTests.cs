using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class NpcFavorTcpTests
{
    const int Letter = 490700963;
    public static async Task RunAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        var items=resources.ItemResources.Items;
        var definition=items.NpcFavorItem(Letter);
        That(definition is { Source.Parameters: [1000] } && definition.Source.Item.MinimumLevel==20 && definition.Targets.Contains(17),
            "真实490700963为20级、每封1000点、包含目标NPC的信件");
        var npcs=new NpcFavorCatalog(resources.Archive);
        That(npcs.Rules is { MinimumLevel: 20, GiftCount: 100, GiftLimit: 5 } rules &&
            rules.Gifts.Count == 6 && rules.Colors.Count == 48 && rules.Extra(17, 10157249) is { Count: 1, Minimum: 100, Maximum: 100 },
            "当前PVF完整普通赠礼、特殊礼物和48色定义可解析");
        That(npcs.Rules!.Extra(17, 10311453) is null, "已删除NPC的迁移礼物不能按普通礼物消费");
        var room = resources.Towns.Room;
        var present = resources.Towns.Areas[(room.Town, room.Area)].Default.Npcs.First(npc => npcs.Get((int)npc.Id) is not null);
        var giftTarget = npcs.Get((int)present.Id)!;
        int giftNpc = giftTarget.OwnerId;
        foreach(int id in definition!.Targets.Where(id=>id!=13))
            That(npcs.Get(id) is { Maximum: > 0 }, $"信件NPC{id}可解析真实owner和上限");
        That(definition.Targets.Contains(13) && npcs.Get(13) is null,"旧诺顿白名单条目没有新版好感度定义，不能伪造点数");
        That(items.NpcFavorItem(490700918) is { Source.Parameters: [10000] },"同动作玫瑰花束保留资源的职业与点数，未硬编码信件编号");
        That(items.NpcFavorItem(490704801) is null && items.NpcFavorItem(490704802) is null,"同名礼包继续走开箱，不误当信件消费");
        await using var fixture=new TcpTestFixture(options,Path.Combine(root,"npc-favor"),resources:resources);
        var repository=new CharacterRepository(fixture.Database);
        using(var owner=new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            foreach(string name in new[]{"FavorTcp","OtherFavor"})
                repository.Create(owner,new(name,resources.Towns.Room,resources.Towns.InitialReturn)
                {
                    Level=20, AcceptedStoryLevel=1, Skills=resources.Characters.Get(0).Skills,
                    Inventory=new([InventoryRewardGrantService.CreateItem(items.Get(Letter),new(InventoryListType.Main,65),
                        InventoryRewardGrantService.Create(items.Get(Letter),3)),
                        InventoryRewardGrantService.CreateItem(items.Get(7542),new(InventoryListType.Main,66),
                            InventoryRewardGrantService.Create(items.Get(7542)))])
                });
            var template = items.Get(10157249);
            var role = repository.Create(owner, new("GiftsTcp", room, resources.Towns.InitialReturn)
            {
                Level=20, AcceptedStoryLevel=1, Skills=resources.Characters.Get(0).Skills,
                Inventory=new([InventoryRewardGrantService.CreateItem(template,new(InventoryListType.Main,177),
                    InventoryRewardGrantService.Create(template,2) with { ExpireTime=0 })])
            });
            var inventory=CurrencyWallet.Set(role.Inventory,CurrencyKind.WhiteCube,500) with
            { NpcFavor=[new(giftNpc,giftTarget.Intimate) { GiftDay=ShopPurchaseCount.Today(fixture.UtcNow),GiftCount=3 }] };
            InventoryService.Commit(repository,owner,role,new(true,0,new(role.Inventory,inventory,"test_favor_seed")));
        }
        await fixture.StartAsync();
        using(var peer=Open(fixture))
        {
            Send(peer);Reject(peer);
            peer.Select();Bytes(peer.NpcFavor,[0],"首次选角发送空好感度");
            string original=State();
            foreach(var body in new[]{Request(npc:999),Request(npc:13),Request(reserved:1),Request(second:1),Request(list:2),Request(slot:66),Request(slot:65535)})
            {
                peer.Send(515,body);Reject(peer,BitConverter.ToUInt16(body),body[2]);
            }
            peer.Barrier();That(State()==original,"伪造NPC、保留参数、容器、源类型和槽位不改变任何资产或revision");
            Send(peer);Success(peer,[new(17,1000)]);
            Send(peer,npc:18);Success(peer,[new(17,1000),new(18,1000)]);
            peer.Barrier();
            That(fixture.Query("SELECT npc_favor FROM characters WHERE name='FavorTcp'") is [[string state]] &&
                JsonSerializer.Deserialize<NpcFavorPoint[]>(state)!.SequenceEqual([new NpcFavorPoint(17,1000),new NpcFavorPoint(18,1000)]),
                "两个NPC各加一次，完整点数与扣信已落库");
        }
        using(var peer=Open(fixture))
        {
            peer.Select();Bytes(peer.NpcFavor,Snapshot([new(17,1000),new(18,1000)]),"重连恢复两个NPC的完整点数");
            Send(peer);Success(peer,[new(17,2000),new(18,1000)]);
            string exhausted=State();
            Send(peer);Reject(peer);peer.Barrier();That(State()==exhausted,"源耗尽后的重复请求不补发好感度");
            peer.Send(7);peer.Expect(1,7,[1]);peer.Expect(0,2);
            // 换角色后必须用空表覆盖前一角色，不能依靠客户端自行清缓存。
            peer.Select(1);Bytes(peer.NpcFavor,[0],"同连接换角不继承前角色好感度");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("rebuild");
        using(var peer=Open(fixture))
        {
            peer.Select();Bytes(peer.NpcFavor,Snapshot([new(17,2000),new(18,1000)]),"重建服务后从真实存档恢复");
            Send(peer);Reject(peer);peer.Barrier();
            That(fixture.Query("PRAGMA integrity_check") is [["ok"]],"好感度与源消费数据库完整");
        }
        using(var peer=Open(fixture))
        {
            peer.Select(2);
            string before=State();
            fixture.Sql("CREATE TRIGGER fail_npc_gift AFTER INSERT ON currency_audit_log WHEN NEW.action_name='npc_favor_gift' BEGIN SELECT RAISE(ABORT,'gift rollback'); END");
            peer.Send(814,GiftRequest(0,giftNpc,36,355));peer.Closed();
            fixture.Sql("DROP TRIGGER fail_npc_gift");
            That(State()==before,"赠礼末段SQL失败整笔回滚并沿统一异常收尾，不冒充业务拒绝或发送成功");
        }
        int giftPoints;
        using(var peer=Open(fixture))
        {
            peer.Send(814,GiftRequest(0,giftNpc,36,355));peer.Expect(1,814,[0,19,0]);
            peer.Select(2);
            peer.Send(814,GiftRequest(0,giftNpc,36,355));
            var ack=peer.Expect(1,814).Body;
            That(ack.Length==13 && ack[0]==1 && ack[1]==0 && BitConverter.ToInt32(ack,2)==giftNpc &&
                BitConverter.ToUInt16(ack,6) is >=200 and <=600 && BitConverter.ToInt32(ack,8)==giftTarget.Intimate+BitConverter.ToUInt16(ack,6) &&
                ack[12]==4,"CMD814晶块赠礼专用成功结构与实际区间、绝对点数、次数一致");
            giftPoints=BitConverter.ToInt32(ack,8);
            var delta=peer.Expect(0,1681).Body;
            That(delta.Length==126 && BitConverter.ToInt16(delta,3)==355 && BitConverter.ToInt32(delta,9)==400,
                "赠礼后无播报差量同步账号钱包的剩余400晶块");
            peer.Barrier();
            peer.Send(814,GiftRequest(2,giftNpc,0,177));
            peer.Expect(1,814,Build(w=>{w.Write((byte)1);w.Write((byte)2);w.Write(giftNpc);w.Write((ushort)100);w.Write(giftPoints+100);w.Write((byte)5);}));
            var cleared=peer.Expect(0,1681).Body;
            That(cleared.Length==126 && BitConverter.ToInt16(cleared,3)==177 && BitConverter.ToInt32(cleared,5)==-1,
                "特殊礼物源先清除旧的同模板数量视图");
            var remaining=peer.Expect(0,1681).Body;
            That(remaining.Length==126 && BitConverter.ToInt16(remaining,3)==177 && BitConverter.ToInt32(remaining,5)==10157249 &&
                BitConverter.ToInt32(remaining,9)==1,"特殊礼物剩余数量按已提交实例同步，不发获得播报");
            peer.Barrier();giftPoints+=100;
            string before=State();
            foreach(var body in new[] { GiftRequest(0,giftNpc,36,355),GiftRequest(2,giftNpc,36,355),
                GiftRequest(0,giftNpc,36,360),GiftRequest(0,int.MaxValue,36,355),new byte[]{1,1,0} })
            {peer.Send(814,body);peer.Expect(1,814,[0,19,0]);}
            peer.Barrier();That(State()==before,"当日满次数、伪造容器、未知NPC及畸形请求不改变资产或revision");
            byte[] dye=Build(w=>{w.Write((byte)1);w.Write(giftNpc);w.Write((byte)2);w.Write((byte)0);w.Write((byte)47);w.Write((byte)3);w.Write((byte)1);});
            peer.Send(814,dye);peer.Expect(1,814,[1,..dye]);peer.Barrier();
            string colored=State();
            peer.Send(814,dye);peer.Expect(1,814,[1,..dye]);peer.Barrier();
            That(State()==colored,"相同染色请求不重复写库或升版本");
        }
        using(var peer=Open(fixture))
        {
            peer.Select(2);
            Bytes(peer.NpcFavor,Build(w=>{w.Write((byte)1);w.Write(giftNpc);w.Write(giftPoints);w.Write((byte)5);w.Write((byte)4);
                w.Write((byte)0);w.Write((byte)47);w.Write((byte)1);w.Write((byte)0);w.Write((byte)2);w.Write((byte)0);w.Write((byte)3);w.Write((byte)1);}),
                "重连746恢复好感、日次数和四通道颜色");
            peer.Send(7);peer.Expect(1,7,[1]);peer.Expect(0,2);peer.Select(1);
            Bytes(peer.NpcFavor,[0],"换角不会继承另一角色的颜色或次数");
        }
        await PartyDungeonTcpTests.RunFavorNpcAsync(options,root,resources);
        string State()=>JsonSerializer.Serialize(fixture.Query("SELECT id,revision,npc_favor FROM characters ORDER BY id"))+
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"))+
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind"));
    }
    static byte[] Request(ushort slot=65,byte list=0,uint npc=17,uint reserved=0,uint second=0)=>Build(w=>
    {w.Write(slot);w.Write(list);w.Write(reserved);w.Write(162u);w.Write(npc);w.Write(second);});
    static byte[] GiftRequest(byte operation,int npc,byte list,ushort slot)=>Build(w=>
    {w.Write(operation);w.Write(npc);w.Write(list);w.Write(slot);});
    static void Send(TcpPeer peer,uint npc=17)=>peer.Send(515,Request(npc:npc));
    static void Reject(TcpPeer peer,ushort slot=65,byte list=0)=>peer.Expect(1,515,Build(w=>
    {w.Write((byte)0);w.Write((ushort)0);w.Write(slot);w.Write(list);w.Write((byte)162);}));
    static void Success(TcpPeer peer,NpcFavorPoint[] rows)
    {
        peer.Expect(1,515,[1,65,0,0,162,0,0,0]);
        peer.Expect(0,746,Snapshot(rows));
        // 下一屏障必须紧跟，证明没有重复NOTI14源更新导致客户端二次扣信。
        peer.Barrier();
    }
    static byte[] Snapshot(NpcFavorPoint[] rows)=>Build(w=>
    {w.Write((byte)rows.Length);foreach(var row in rows){w.Write(row.NpcId);w.Write(row.Points);w.Write((byte)0);w.Write((byte)0);}});
}
