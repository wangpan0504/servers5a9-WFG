using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CreatureGroupTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        var groups = resources.ItemResources.CreatureGroups;
        var color = items.CreatureGrowthItem(2683443)!;
        var ore = items.CreatureGrowthItem(2683441)!;
        That(color is { ViewMode: 3, GroupId: 1 } && color.Types.SequenceEqual(new[] { 1,4 }) &&
            groups.Resolve(color,100330627,2)?.Select(0) == 100330633,
            "真实2683443按第4类同行选择保留型号，而不是套用宠物等级或硬编码目标");
        That(groups.Resolve(ore,100330534,0) is { ConsumeCount: 30 } fixedRule && fixedRule.Select(0) == 100330623 &&
            groups.Resolve(ore,100330625,3) is { ConsumeCount: 50 } selected && selected.Select(0) == 100330638 &&
            groups.Resolve(ore,100330629,0) is { ConsumeCount: 10, TotalWeight: 10000 },
            "真实矿石按类型1、2、3原序匹配，分别使用固定、选择与随机规则及实际成本");
        await using var fixture = new TcpTestFixture(options,Path.Combine(root,"creature-group"),resources:resources,
            clock:new ConsumableUseSessionTests.FakeClock());
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{}))
        {
            var candidate = CharacterCreation.Prepare("PetGroup",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            InventoryItem Pet(short slot,int id) => new(0,new(InventoryListType.Pet,slot),new(id,ItemKind.Creature,0)
                { EnchantCardId=1234,EnchantUpgrade=2,Extension27=88,Extension114=new(1,99) })
                { Creature=new() { Name="keep",Level=1,Experience=0,Satiety=77 } };
            InventoryItem Book(short slot,int id,int count) => new(0,new(InventoryListType.Main,slot),new(id,ItemKind.Consumable,count));
            repository.Create(owner,candidate with { Inventory=candidate.Inventory with { Items=candidate.Inventory.Items
                .Add(Book(65,2683441,1000)).Add(Book(66,2683443,2)).Add(Book(67,2683444,1)).Add(Book(68,2683445,1))
                .Add(Book(69,10003716,100)).Add(Book(70,2683441,29))
                .Add(Pet(8,100330534)).Add(Pet(9,100330627)).Add(Pet(10,100330625)).Add(Pet(11,100330629))
                .Add(Pet(12,100330631)).Add(Pet(13,100330631)).Add(Pet(14,100330804)).Add(Pet(15,100330534))
                .Add(new(0,CreatureRules.EquippedSlot,new(63002,ItemKind.Creature,0)) { Creature=new() { Name="keep" } }) } });
        }
        await fixture.StartAsync();
        var original=Read();
        using (var peer=Open(fixture))
        {
            peer.Select();
            string before=State();
            foreach (var request in new[] { Request(66,9,0),Request(66,9,6),Request(70,15,0),Request(65,40000,0) })
            {
                peer.Send(515,request);
                peer.Expect(1,515,[0,0,0,request[0],request[1],0,107]); peer.Barrier();
            }
            That(State()==before,"错误选项、目标槽或不足30个的源整笔拒绝，不回退低价配方，连接可继续");
            fixture.Sql("CREATE TRIGGER fail_group_convert AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='creature_group_convert' " +
                "BEGIN SELECT RAISE(ABORT,'group rollback'); END");
            peer.Send(515,Request(66,9,2)); peer.Closed();
            That(State()==before,"转换目标及扣源落库之后的审计失败完整回滚，没有先行成功ACK");
            fixture.Sql("DROP TRIGGER fail_group_convert");
        }
        using (var peer=Open(fixture))
        {
            peer.Select();
            Convert(peer,66,9,2,1,100330633);
            string converted=State();
            peer.Send(515,Request(66,9,2)); peer.Expect(1,515,[0,0,0,66,0,0,107]); peer.Barrier();
            That(State()==converted,"重复提交已变成当前属性的选项按原生窗口规则拒绝，不再次扣券");
            Convert(peer,65,8,0,970,100330623);
            Convert(peer,65,10,3,920,100330638);
            int randomResult=Convert(peer,65,11,0,910,null);
            That(randomResult is >=100330628 and <=100330631,"随机规则结果属于原完整权重池，不能由请求指定模板");
            Convert(peer,67,12,0,0,100330656);
            Convert(peer,68,13,0,0,100330661);
            Convert(peer,69,14,0,0,100330947);
            var saved=Read();
            foreach (short slot in new short[] { 8,9,10,11,12,13,14 })
            {
                var old=original.Inventory.Find(new(InventoryListType.Pet,slot))!;
                var current=saved.Inventory.Find(old.Position)!;
                That(current==old with { Core=old.Core with { ItemId=current.Core.ItemId } },
                    "全部规则沿原UID、附魔、扩展、期限和详情原地转换，不重建资产");
            }
            That(saved.Inventory.Find(CreatureRules.EquippedSlot)==original.Inventory.Find(CreatureRules.EquippedSlot) &&
                saved.Inventory.Find(new(InventoryListType.Main,70))==original.Inventory.Find(new(InventoryListType.Main,70)),
                "Pet背包转换不改变穿戴宠物和拒绝请求的原堆");
        }
        string committed=State();
        await fixture.StopAsync(); await fixture.StartAsync("restore");
        using (var peer=Open(fixture))
        {
            peer.Select(); peer.Barrier();
            That(State()==committed,"服务重建及重新选角恢复真实原UID转换和源数量，不重复消费");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length==0,
            "转换组规则及回滚后数据库完整");

        Character Read()
        {
            using var owner=new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{});
            return repository.LoadCharacter(owner,0);
        }
        string State()=>fixture.Snapshot("SELECT * FROM characters ORDER BY id")+
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid")+
            fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid")+
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid")+
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY rowid");
        int Convert(TcpPeer peer,ushort source,short target,uint choice,int remaining,int? expected)
        {
            var previousTarget=Read().Inventory.Find(new(InventoryListType.Pet,target))!;
            peer.Send(515,Request(source,(uint)target,choice));
            peer.Expect(1,515,[1,(byte)source,(byte)(source>>8),0,107,0,0,0]);
            var sourceDelta=peer.Expect(0,14).Body;
            var sourceId=original.Inventory.Find(new(InventoryListType.Main,(short)source))!.Core.ItemId;
            That(sourceDelta.Length==126 && sourceDelta[0]==0 && U16(sourceDelta,1)==1 && U16(sourceDelta,3)==source &&
                U32(sourceDelta,5)==(remaining==0?uint.MaxValue:(uint)sourceId) &&
                (remaining==0 || U32(sourceDelta,9)==remaining),
                "ACK107不在客户端扣源，必须发送真实完整绝对源差量，包括30、50和100个成本");
            int savedResult=Read().Inventory.Find(previousTarget.Position)!.Core.ItemId;
            if (savedResult==previousTarget.Core.ItemId)
            {
                That(expected is null,"仅随机规则可能抽回原模板，源成本仍必须实际消费");
                peer.Barrier();
                return savedResult;
            }
            byte[] empty=new byte[126]; empty[0]=7; empty[1]=1; empty[3]=(byte)target; empty[4]=(byte)(target>>8);
            Array.Fill(empty,(byte)255,5,4);
            peer.Expect(0,14,empty); peer.Expect(0,105);
            var targetDelta=peer.Expect(0,14).Body;
            int result=checked((int)U32(targetDelta,5));
            That(targetDelta.Length==126 && targetDelta[0]==7 && U16(targetDelta,1)==1 && U16(targetDelta,3)==target &&
                result==savedResult && (expected is null || result==expected),
                "源差量后清Pet目标、恢复105及完整核心，不附加106或装备角色刷新");
            peer.Barrier();
            return result;
        }
    }

    static byte[] Request(ushort source,uint target,uint choice)=>Build(writer=>
    { writer.Write(source);writer.Write((byte)0);writer.Write(0u);writer.Write(107u);writer.Write(target);writer.Write(choice); });
}
