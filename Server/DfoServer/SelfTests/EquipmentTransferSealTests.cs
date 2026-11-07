using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentTransferSealTests
{
    internal const string ResealText = """
        [grade]
        1 85 4 86 95 5
        [/grade]
        [rarity weight]
        1 2 1 2 8 8 6
        [/rarity weight]
        [extended rarity weight]
        0 0 1 1 1 1 1
        [/extended rarity weight]
        [reseal soul index]
        0 10100115 1 10100116 2 10099773 3 10099774 4 10158124 5 3311 6 10099775
        [/reseal soul index]
        [soul cost weight]
        0 0 100 10 0 1 2
        [/soul cost weight]
        [soul reseal constant]
        0 0 1.10 1.10 0 0 1.11
        [/soul reseal constant]
        [reseal count limit]
        1 85 7 86 95 4
        [/reseal count limit]
        """;

    public static void RunCore()
    {
        var clock = new ConsumableUseSessionTests.FakeClock();
        var equipment = new ItemTemplate(101, ItemKind.Equipment, 12, 85, 10, 1, 0, 3, 1, "[sealing]", ushort.MaxValue, 0, "equip");
        var wax = equipment with { Id = 14, Kind = ItemKind.Consumable, EquipmentSlot = 0, MinimumLevel = 1,
            ResealConsumable = new(1, 99, false, [0,0,1,1,0,0,1]) };
        var items = new ItemCatalog(null!, new Dictionary<int,string>(), new Dictionary<int,string>(), 1000);
        items.PrefillTemplates(new[] { equipment, wax, wax with { Id = 102, ResealConsumable = wax.ResealConsumable! with { OneShot = true } } }
            .Select(item => new KeyValuePair<int,ItemTemplate>(item.Id, item)));
        var catalog = new EquipmentResealCatalog(ResealText);
        var pieces = new EpicPieceCatalog("[piece list]\n101 1001\n[/piece list]");
        That(catalog.WaxCost(equipment, 3) == 11 && catalog.SoulCost(equipment, 3) is { ItemId: 10099774, Count: 106 },
            "封装费用按当前次数计算，灵魂费用float32幂次向零截断");
        That(catalog.SoulCost(equipment with { MinimumLevel = 90 }, 4) is null && catalog.SoulCost(equipment with { Rarity = 4 }, 0) is null,
            "高等级封装次数上限和不支持的灵魂类型不变成免费封装");
        var target = new InventoryItem(1, Bag(9), new(equipment.Id, ItemKind.Equipment, 12345)
            { Attr = 3 * 32 + 17, EnchantCardId = 3175, Rune = 9, Extension97 = 0x12345678, EmancipateLevel = 0 });
        var source = new InventoryItem(2, Bag(82), new(14, ItemKind.Consumable, 20));
        var character = new Character(1, 0, "Rules", 0, new(1,0,1,1), null) { Level = 95, Inventory = new([target, source]) };
        var reseal = new EquipmentResealService(items, catalog, pieces, clock);
        var result = reseal.Plan(character, new(9, equipment.Id, 82, 0, false));
        That(result.Accepted && result.Plan!.After.Find(Bag(9))!.Core == target.Core with { Attr = 4 * 32 + 17, SealFlag = 1, Rune = 0 } &&
            result.Plan.After.Find(Bag(82))!.Core.Count == 9, "普通蜜蜡清除特效，按实际费用扣指定堆叠并保留打造");
        foreach (var invalid in new[] { target with { Core = target.Core with { EquipmentLockId = 1 } },
            target with { Core = target.Core with { SealFlag = 1 } }, target with { Core = target.Core with { Attr = 7 * 32 + 17 } },
            target with { Core = target.Core with { ExpireTime = 1 } }, target with { Core = target.Core with { IsRental = true } } })
            That(!reseal.Plan(character with { Inventory = new([invalid,source]) }, new(9,equipment.Id,82,0,false)).Accepted,
                "封装拒绝锁定、已封装、次数上限、过期和租赁实例");
        var maxed = target with { Core = target.Core with { Attr = 7 * 32 + 17 } };
        var once = reseal.Plan(character with { Inventory = new([maxed, source with { Core = source.Core with { ItemId = 102, Value = 1 } }]) },
            new(9,equipment.Id,82,0,false));
        That(once.Accepted && once.Plan!.After.Find(Bag(9))!.Core == maxed.Core with { SealFlag = 1, Rune = 0 } && once.Plan.After.Find(Bag(82)) is null,
            "一次性蜜蜡可封装已满次数装备且不增加次数，同时清除特效");

        var transport = new EquipmentTransportCatalog("""
            [stone type]
            0
            [cargo transport stone grade]
            1 95 3
            [/cargo transport stone grade]
            [cargo transport stone rarity weight]
            3 2
            [/cargo transport stone rarity weight]
            [cargo transport stone enable equip type]
            `[weapon]`
            [/cargo transport stone enable equip type]
            [/stone type]
            """);
        var stone = wax with { Id = 201, ResealConsumable = null };
        var action = new ActionConsumableDefinition(stone, "[cargo transport stone]", [0], 1000, -201, 0, 0, ["[seria room]"]);
        var service = new EquipmentTransportService(id => id == 201 ? stone : equipment, _ => action, transport, pieces, null!, clock);
        var sender = character with { Inventory = new([target, source with { Core = new(201, ItemKind.Consumable, 10) }]) { AccountCargo = new(Capacity: 8) } };
        var request = new CreatureTransportRequest(82,9,0,uint.MaxValue);
        That(service.Plan(sender,null,request,"[seria room]",out var plan) == 0 && plan!.Inventory.After.Find(Bag(82))!.Core.Count == 4 &&
            plan.Transferred.Uid == target.Uid && plan.Transferred.Core == target.Core && plan.Transferred.Position == new InventorySlot(InventoryListType.AccountCargo,0),
            "普通跨界按等级乘稀有度扣多枚石头，移入真实账号仓库并保留完整UID/核心");
        Bytes(CreatureTransportPackets.Ack(request, new(true,0,plan!.Inventory) { Receipt = new(sender with { Inventory = plan.Inventory.After },1,1) }),
            [1,82,0,9,0,4,0,0,0,0], "跨界ACK第四u16为仓库槽而非固定参数");
        That(service.Plan(sender,null,request,"[village]",out _) == 0 &&
            service.Plan(sender with { Inventory = sender.Inventory with { AccountCargo = new() } },null,request,"[seria room]",out _) == 19,
            "跨界规划不另按地点字符串拒绝，统一Session门禁负责场景；账号仓库容量不足仍整单拒绝");
        var adjusted = sender with { Inventory = sender.Inventory with
            { Items = sender.Inventory.Items.Replace(target,target with { Core=target.Core with { EmancipateLevel=11 } }) } };
        That(service.Plan(adjusted,null,request,"[seria room]",out var stoneAdjusted)==0 &&
            stoneAdjusted!.Inventory.After.Find(Bag(82))!.Core.Count==4,
            "普通石头按模板等级收费，不套NPC专用实例等级修正");
        var rule = transport.Rules[0] with { Included = [equipment.Id], Excluded = [equipment.Id],
            ExcludedProperties = ["growthType", "randomoption", "periodEquipment"] };
        That(rule.StoneCost(equipment)==6 && rule.StoneCost(equipment with { Id=999 })==0 &&
            rule.StoneCost(equipment with { GrowthEquipment=true })==0 &&
            rule.StoneCost(equipment with { RandomOptionEquipment=true })==0 &&
            rule.StoneCost(equipment with { PeriodEquipment=true })==0 && rule.StoneCost(equipment,11)==0,
            "非空白名单替代黑名单，部位、修正等级及三项资源排除仍生效");
        var npc = rule with { Included=[], Excluded=[], Souls=[3,10099774,5,3311],
            RarityWeights=ImmutableDictionary<int,int>.Empty.Add(3,2).Add(5,2).Add(4,2),
            SoulWeights=[3,10,20,30,40,5,99,99,99,99,4,10,20,30,40],
            CrystalWeights=[3316,3,4,5,6,7,5,9,9,9,9,4,4,5,6,7],
            PieceWeights=[2,2,2,2], ParticularCosts=[3,85,2000,2], ChronicleCost=250 };
        That(npc.NpcCosts(equipment,pieces) is { } npcCosts && npcCosts.SequenceEqual(new ItemMaterialCost[]
            { new(10099774,60),new(3316,24),new(2000,2) }) &&
            npc.NpcCosts(equipment with { Rarity=5 },pieces) is { } chronicle && chronicle[0]==new ItemMaterialCost(3311,250),
            "NPC四列材料费用及勇者250灵魂覆盖值来自独立金样");
        var medalCatalog = new EquipmentTransportCatalog("""
            [stone type]
            9
            [cargo transport stone grade]
            1 95 1
            [/cargo transport stone grade]
            [cargo transport stone rarity weight]
            3 1
            [/cargo transport stone rarity weight]
            [cargo transport stone enable equip type]
            `[flag]`
            [/cargo transport stone enable equip type]
            [/stone type]
            """);
        var medal = equipment with { Kind=ItemKind.GuildMedal,EquipmentSlot=32,Jobs=1 };
        var medalService = new EquipmentTransportService(id=>id==201?stone:medal,
            _=>action with { Label="[flag transport stone]",Parameters=[9] },medalCatalog,pieces,null!,clock);
        var medalSender = sender with { Inventory=sender.Inventory with { Items=sender.Inventory.Items.Replace(target,
            target with { Position=new(InventoryListType.GuildMedal,0),Core=target.Core with { Kind=ItemKind.GuildMedal } }) } };
        var receiver = CharacterRosterEntry.FromCharacter(character with { Id=2,Job=1 });
        That(medalService.Plan(medalSender,receiver,new(82,0,100,1),"[seria room]",out _)==7 &&
            medalService.Plan(medalSender,receiver with { Job=0 },new(82,0,100,1),"[seria room]",out _)==0,
            "勋章收件职业沿模板职业位过滤，不被伪造名册索引绕过");
        Console.WriteLine("PASS 装备跨界及封装核心费用、完整实例和拒绝边界");
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
