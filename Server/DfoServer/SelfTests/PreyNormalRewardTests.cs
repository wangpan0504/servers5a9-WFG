using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyNormalRewardTests
{
    internal static void Run(TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var now = new DateTimeOffset(2026,10,31,12,0,0,TimeSpan.FromHours(8));
        var box = InventoryRewardGrantService.Create(items.Items.Get(490011518));
        var source = new Character(1,0,"PreyBox",0,resources.Towns.Room,resources.Towns.InitialReturn)
            { Level=95,Inventory=new([new(1,new(InventoryListType.Main,3),box)]) };
        foreach (var (roll,count) in new[] { (0,8),(4499,8),(4500,9),(7799,9),(7800,10),(9899,10),(9900,20),(9999,20) })
        {
            var service = new InventoryBoosterService(items.Items,items.Boosters,items.GoldLimits,items.EpicPieces,new FixedDraw(roll));
            var result = service.Open(source,new(3,1),out var receipt,now);
            That(result is { Accepted:true,Plan:not null } && receipt?.Rewards is [{ ItemId:10158964 } reward] &&
                reward.Count == count && result.Plan.After.Find(new(InventoryListType.Main,3)) is null &&
                result.Plan.After.Items.Where(row => row.Core.ItemId == 10158964).Sum(row => row.Core.Count) == count &&
                source.Inventory.Items.Single().Core.ItemId == 490011518,
                "每日原宝箱在每个概率分界真实规划扣一箱并给对应数量，规划不提前修改源库存");
        }
    }

    sealed class FixedDraw(long value) : Random
    {
        public override long NextInt64(long maxValue) => Math.Min(value,maxValue-1);
    }
}
