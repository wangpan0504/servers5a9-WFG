using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonAltarRewardTests
{
    public static void Run(PvfArchive archive, ItemResources items)
    {
        int before = Count;
        var policy = new DungeonAltarRewardPolicy(DungeonAltarRewardCatalog.Load(archive), DungeonAltarClearItemCatalog.Load(archive),
            items.DungeonAltarItems, DungeonKillExperienceCatalog.Load(archive), items.Items, items.GoldLimits, items.EpicPieces);
        var now = DateTimeOffset.FromUnixTimeSeconds(1792756800);
        var completed = policy.Prepare(11006, 70, 15, 15, 1, _ => 0, now);
        That(completed.Draws.Length == 10 && completed.Draws.All(row => row.Gold > 0 && row.Item is null) &&
            completed.Experience is > 0 and <= 1000000, "实际无尽结算按15回合冻结十项专用奖励与经验");
        var equipment = policy.Prepare(11007, 70, 7, 7, 1,
            bound => bound == int.MaxValue ? 123456789 : bound == 1000 ? 500 : 0, now);
        That(equipment.Draws.Length == 10 && equipment.Draws.All(row => row.Gold == 0) && equipment.Draws.Any(row => row.Item is not null) &&
            equipment.Draws.Where(row => row.Item is not null).All(row => row.Item!.Core.ItemId > 0 && row.Item.Uid == 0),
            "实际极限随机物品分支保留创建核心，空池不会换成其它奖励");
        That(equipment.Draws.Any(row => row.Item?.Core is { Kind:ItemKind.Equipment, Value:123456789 }),
            "实际资源奖励覆盖具有非零实例随机值的装备");
        var partial = policy.Prepare(11006, 70, 5, 15, 2, bound => bound - 1, now);
        That(partial.Draws.Length == 3 && partial.Draws.All(row => row.Gold == 0 && row.Item is null),
            "已完成五回合只取三项，失败抽样保留空位");
        var failed = policy.Prepare(11007, 70, 0, 7, 1, _ => 0, now);
        That(failed.Experience == 0 && failed.Draws is [{ Gold: 0, Item: null }], "零回合失败没有经验或伪造物品");
        foreach (var plan in new[] { completed, equipment, partial, failed })
        {
            var wire = DungeonAltarPackets.ClearRewards(checked((byte)plan.CompletedRounds), checked((byte)plan.MaximumRounds),
                [plan.Draws, [], [], []]);
            That(wire[2] == plan.Draws.Length && wire.Length == 6 + plan.Draws.Length * 8,
                "真实PVF祭坛冻结结果逐槽投影，不删减空牌");
            for (int index = 0; index < plan.Draws.Length; index++)
            {
                var draw = plan.Draws[index];
                uint expectedId = draw.Item is { } item ? checked((uint)item.Core.ItemId) : draw.Gold > 0 ? 0u : uint.MaxValue;
                uint expectedCount = draw.Item is { } granted ? checked((uint)granted.Core.Count) : checked((uint)draw.Gold);
                That(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(3 + index * 8)) == expectedId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(7 + index * 8)) == expectedCount,
                    "真实PVF冻结奖励以空牌、金币或物品实际数量编码，不发送装备随机值");
            }
        }
        Reject<InvalidDataException>(() => policy.Prepare(11006,70,15,7,1,_ => 0,now), "结算不能把无尽回合套入极限上限");
        Reject<InvalidDataException>(() => policy.Prepare(11006,70,15,15,1,bound => bound,now), "随机边界越界不截断成奖励");
        Console.WriteLine($"PASS {Count - before} 实际祭坛奖励资源与冻结策略断言");
    }
}
