using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterDisguiseTests
{
    public static void Run(TownCatalog towns,ItemResources resources)
    {
        var now = DateTimeOffset.UtcNow;
        var service = new InventoryBoosterService(resources.Items,resources.Boosters,resources.GoldLimits,resources.EpicPieces,new Random(1));
        // 当前完整索引中直接引用变身道具的101个礼盒，覆盖共用开箱路径。
        int[] ids = [
            7492,7493,7553,7554,7555,7556,7557,7574,7692,7726,7727,7728,
            7729,7730,7731,7732,7733,7734,7735,7736,7737,7738,7739,8287,
            2680368,2680386,2680769,2749128,2749146,2749147,2749148,2749149,2749150,2749151,2749152,2749228,
            2749453,2749651,2749683,2749684,10002121,10002555,10003026,10003218,10003906,10004107,10004961,10005894,
            490000089,490004357,10312375,10015271,10096248,10099341,10303034,10305280,10305287,10305954,10305955,10306812,
            10306813,10306814,10307220,10307221,390003005,10307812,10308828,10309613,10309753,10310161,10310229,10310950,
            10311658,10311659,10312665,10312667,490002925,490003010,490003392,490004239,490004240,490005350,490005595,490006394,
            490007506,490008308,490009190,490010638,490010623,490010625,490010629,490011013,490703694,490011840,490011841,490012387,
            490013016,490012974,490013067,490704925,490014129,
        ];
        var failures = new List<string>();
        foreach (int id in ids)
        {
            var template = resources.Items.Get(id);
            var source = new InventoryItem(1,new(InventoryListType.Main,65),InventoryRewardGrantService.Create(template,1,now));
            var role = new Character(1,0,"DisguiseBox",0,towns.Room,towns.InitialReturn)
                { Level = 95, Inventory = new([source]) };
            var opened = service.Open(role,new(65,1),out var receipt,now);
            if (id is 2749651 or 2749683)
            {
                // 旧活动盒按2012/2013日期转换奖励，当前目录明确保留未实现语义，不能忽略转换表开奖。
                That(resources.Boosters.Get(id) is { Available:false,PendingRule:"convert boost item" } &&
                    !opened.Accepted && opened.Plan is null && receipt is null && role.Inventory.Find(source.Position) == source,
                    "UNIMPLEMENTED：日期转换规则未核实，整盒拒绝且来源保持："+id);
                Console.WriteLine($"UNIMPLEMENTED disguise box {id}: convert boost item; rejected without consuming source");
                continue;
            }
            bool valid = opened.Accepted && receipt is { Rewards.Length: > 0 } &&
                opened.Plan!.After.Find(source.Position)?.Core.ItemId != id;
            if (!valid) failures.Add($"{id}: error={opened.Error}, reason={opened.RejectReason}, pending={resources.Boosters.Get(id).PendingRule}");
            else That(true,"变身关联礼盒完整发奖并消费来源："+id);
        }
        That(failures.Count == 0,"变身关联礼盒逐项开箱，所有异常统一报告："+string.Join("; ",failures));
        const int box = 490010625, potion = 490010626;
        var sourceCore = InventoryRewardGrantService.Create(resources.Items.Get(box),2,now);
        var filled = Enumerable.Range(65,56).Select(slot => new InventoryItem(slot,new(InventoryListType.Main,(short)slot),
            slot == 65 ? sourceCore : InventoryRewardGrantService.Create(resources.Items.Get(1106),1000,now))).ToImmutableArray();
        var full = new Character(1,0,"FullDisguiseBox",0,towns.Room,towns.InitialReturn) { Level=95,Inventory=new(filled) };
        That(service.Open(full,new(65,1),out _,now) is { Accepted:false,Plan:null } &&
            full.Inventory.Find(new(InventoryListType.Main,65))!.Core.Count == 2,"满包且保留源栈时整盒拒绝，无部分扣除");
        var last = full with { Inventory = full.Inventory with { Items = filled.SetItem(0,filled[0] with { Core = sourceCore with { Value=1 } }) } };
        var result = service.Open(last,new(65,1),out var granted,now);
        That(result.Accepted && granted!.Rewards is [{ ItemId:potion,Count:1 }] &&
            result.Plan!.After.Find(new(InventoryListType.Main,65)) is { Core.ItemId:potion,Core.Count:1 },
            "引力恶魔礼盒消耗最后一件可复用源槽，满页无需额外空槽");
        var toySource = filled[0] with { Core = InventoryRewardGrantService.Create(resources.Items.Get(10306814),1,now) };
        var toyBox = full with { Inventory = new(filled.SetItem(0,toySource)) };
        That(!service.Open(toyBox,new(65,1),out _,now).Accepted,
            "强者之路礼盒有两项奖励，只有源槽可复用仍不足，必须整盒保留");
        toyBox = toyBox with { Inventory = new(toyBox.Inventory.Items.RemoveAt(55)) };
        var toys = service.Open(toyBox,new(65,1),out var toysReceipt,now);
        That(toys.Accepted && toysReceipt!.Rewards.OrderBy(row => row.ItemId).Select(row => (row.ItemId,row.Count))
            .SequenceEqual(new[] { (10306810,1),(10306815,1) }) &&
            toys.Plan!.After.Items.Count(row => row.Core.ItemId is 10306810 or 10306815) == 2,
            "强者之路礼盒在两格可用时完整发两件COS，不截断奖励或要求第三格");
    }
}
