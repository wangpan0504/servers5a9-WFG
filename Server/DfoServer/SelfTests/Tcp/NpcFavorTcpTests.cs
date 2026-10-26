using System.Text.Json;
using DfoServer.Game.Accounts;
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
        string State()=>JsonSerializer.Serialize(fixture.Query("SELECT id,revision,npc_favor FROM characters ORDER BY id"))+
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"));
    }
    static byte[] Request(ushort slot=65,byte list=0,uint npc=17,uint reserved=0,uint second=0)=>Build(w=>
    {w.Write(slot);w.Write(list);w.Write(reserved);w.Write(162u);w.Write(npc);w.Write(second);});
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
