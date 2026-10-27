using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class NpcFavorTests
{
    static readonly ItemTemplate Template = new(490700963, ItemKind.Consumable, 0, 20, 0, 1000, 0, 2, 1,
        "[account]", ushort.MaxValue, 0, "测试信件", "[waste] 1");
    static readonly InventorySlot Slot = new(InventoryListType.Main, 65);
    static readonly TimeProvider Clock = new ConsumableUseSessionTests.FakeClock();

    public static void RunCore()
    {
        int before = Count;
        var strings = new List<string>();
        PvfToken Text(byte type, string text) { strings.Add(text); return new(type, strings.Count - 1); }
        PvfToken[] tokens = [Text(3,"[stackable type]"), Text(6,"[waste]"), new(0,1),
            Text(3,"[action type]"), Text(6,"[use favor point]"), new(0,1000), Text(3,"[/action type]"),
            Text(3,"[npc select category]"), new(0,17), new(0,18), Text(3,"[/npc select category]")];
        NpcFavorItemDefinition? Parse(PvfToken[] input) => NpcFavorItemDefinition.Parse(Template.Id,"letter.stk",input,
            index=>strings[index],Template);
        var definition = Parse(tokens)!;
        That(definition is { Source.Parameters: [1000] } && definition.Targets.SetEquals([17,18]), "完整动作与NPC白名单按资源读取");
        foreach (var bad in new PvfToken[][]
        {
            [..tokens, ..tokens[7..]], [..tokens[..8], new(6,0), tokens[^1]], [..tokens[..8], new(0,17), new(0,17), tokens[^1]],
            [..tokens[..8], tokens[^1]], [..tokens, Text(3,"[unknown effect]"), new(0,1)],
            [..tokens[..5], new(0,0), ..tokens[6..]]
        }) That(Parse(bad) is null, "重复、错误类型、重复NPC、空白名单、未知效果及零点数不能放行");
        That(Parse(tokens[..^1]) is null, "NPC白名单漏闭合不能按部分清单放行");
        var npcs = new NpcFavorCatalog(id=>id is 17 or 18 ? new(17,40000) : null);
        var service = new InventoryNpcFavorService(id=>id==Template.Id ? definition : null,npcs,time:Clock);
        var item = new InventoryItem(9, Slot, new(Template.Id, ItemKind.Consumable, 2) { SortLockFlag=1 });
        var role = new Character(1,0,"Favor",0,new(1,0,10,10),null) { Level=20, Inventory=new([item]) };
        var result = service.Plan(role,Slot,18,true);
        That(result.Accepted && result.Plan!.After.NpcFavor.SequenceEqual([new NpcFavorPoint(17,1000)]) &&
            result.Plan.After.Find(Slot) is { Uid:9, Core.Count:1, Core.SortLockFlag:1 } && role.Inventory.NpcFavor.IsEmpty,
            "共享NPC写原owner，扣一封并保留源UID、排序锁，规划不修改输入");
        var near = role with { Inventory = role.Inventory with { NpcFavor=[new(17,39500)] } };
        That(service.Plan(near,Slot,17,true).Plan!.After.NpcFavor[0].Points==40000,"临界按NPC最大点数封顶");
        var gifted = near with { Inventory = near.Inventory with { NpcFavor = [new(17,39500)
            { GiftDay=ShopPurchaseCount.Today(Clock.GetUtcNow()),GiftCount=5,Colors=47 }] } };
        That(service.Plan(gifted,Slot,17,true).Plan!.After.NpcFavor[0] is { Points:40000,GiftCount:5,Colors:47 },
            "信件不占普通赠礼次数，且保留已有NPC染色");
        foreach (var bad in new[] { role with { Level=19 }, role with { Job=16 },
            role with { Inventory=role.Inventory with { NpcFavor=[new(17,40000)] } },
            role with { Inventory=role.Inventory with { Items=[item with { Core=item.Core with { EquipmentLockId=1 } }] } },
            role with { Inventory=role.Inventory with { Items=[item with { Core=item.Core with { ExpireTime=(int)Clock.GetUtcNow().ToUnixTimeSeconds() } }] } },
            role with { Inventory=InventorySnapshot.Empty } })
            That(!service.Plan(bad,Slot,17,true).Accepted,"等级、职业、满点、锁、到期及空源拒绝且不扣信");
        That(!service.Plan(role,Slot,999,true).Accepted && !service.Plan(role,new(InventoryListType.PersonalCargo,0),17,true).Accepted,
            "越权NPC与错误容器拒绝");
        Bytes(NpcFavorPackets.Snapshot([new(17,1000)]),[1,17,0,0,0,232,3,0,0,0,0],"746独立预期：绝对点数与两个空计数字节");
        Bytes(NpcFavorPackets.Snapshot([]),[0],"空表显式清除旧角色缓存");
        Reject<InvalidDataException>(()=>NpcFavorPackets.Snapshot([new(17,1),new(17,2)]),"重复NPC存档拒绝");
        Repository(service,item);
        NpcFavorOperationTests.RunCore();
        Console.WriteLine($"PASS {Count-before} NPC好感度资源、规划、协议与事务断言");
    }

    static void Repository(InventoryNpcFavorService service, InventoryItem item)
    {
        string root=Path.Combine(Path.GetTempPath(),"s5a9-npc-favor-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"game.db");
            var repository=new CharacterRepository(path);
            var owners=new AccountOwners();
            using var owner=owners.Acquire("favor",()=>{});
            var place=new DfoServer.GameWorld.Towns.Location(1,0,10,10);
            var role=repository.Create(owner,new("Favor",place,place) { Level=20, Inventory=new([item with { Uid=0 }]) });
            using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();
            using var sql=db.CreateCommand();
            // 真实v101升级保留原角色与完整库存，默认没有历史好感度。
            DatabaseSchemaTests.RewindRaidLifecycle(db);
            sql.CommandText="ALTER TABLE characters DROP COLUMN npc_favor; PRAGMA user_version=101";sql.ExecuteNonQuery();
            repository=new CharacterRepository(path);
            var loaded=repository.LoadCharacter(owner,0);
            That(JsonSerializer.Serialize(loaded)==JsonSerializer.Serialize(role),"v101升级默认空好感且保留角色和完整实例");
            var plan=service.Plan(loaded,Slot,17,true);
            sql.CommandText="CREATE TRIGGER fail_favor AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='npc_favor_item' BEGIN SELECT RAISE(ABORT,'favor rollback'); END";
            sql.ExecuteNonQuery();
            Reject<SqliteException>(()=>InventoryService.Commit(repository,owner,loaded,plan),"末段真实SQL失败");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner,0))==JsonSerializer.Serialize(loaded),"SQL失败一起回滚好感、源UID/Core/数量及revision");
            sql.CommandText="DROP TRIGGER fail_favor";sql.ExecuteNonQuery();
            var committed=InventoryService.Commit(repository,owner,loaded,plan);
            Reject<InvalidOperationException>(()=>InventoryService.Commit(repository,owner,loaded,plan),"旧revision计划不能重扣或重加");
            var reopened=new CharacterRepository(path).LoadCharacter(owner,0);
            That(reopened.Inventory.NpcFavor.SequenceEqual([new NpcFavorPoint(17,1000)]) && reopened.Inventory.Find(Slot)?.Core.Count==1 &&
                reopened.Revision==committed.Revision,"重开库同时恢复好感与剩余信件");
            var pending=service.Plan(reopened,Slot,17,true);
            using var replacement=owners.Acquire("favor",()=>{});
            Reject<InvalidOperationException>(()=>InventoryService.Commit(repository,owner,reopened,pending),"旧generation不能提交");
            That(repository.LoadCharacter(replacement,0).Inventory.NpcFavor[0].Points==1000,"接管不产生旧代际资产变化");
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
