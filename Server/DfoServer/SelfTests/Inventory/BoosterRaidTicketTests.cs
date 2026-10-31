using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterRaidTicketTests
{
    internal static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var now = new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.FromHours(8));
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        (int Source, int[] Rewards)[] sources =
        [
            (10162065, [10159649]), (10306761, [10306760]), (10308561, [10308560]),
            (10309055, [10309054]), (10309343, [10309342]), (10309674, [10159649]),
            (10309675, [10306760, 10309054]), (10309960, [10159649]),
            (10309961, [10306760, 10309054]), (10309962, [10308560, 10309342]),
            (10312301, [10159649]), (10312302, [10306760, 10309054]), (10312303, [10308560, 10309342]),
            (10312883, [10308560, 10309342]), (10312884, [10306760, 10309054]),
            (10312931, [10308560, 10309342]), (10312932, [10306760, 10309054]),
            (490013068, [10309054]), (490013090, [10309054])
        ];
        foreach (var (source, rewards) in sources)
        {
            var definition = resources.Boosters.Get(source);
            That(definition.Available, $"团本门票盒{source}保留完整开启定义：{definition.PendingRule}");
            That(definition.Selection is null
                ? definition.Pools is [{ Draws: 1, Rewards.Length: 1 }] &&
                    definition.Pools[0].Rewards[0].ItemId == rewards[0] && definition.Pools[0].Rewards[0].Count == 1
                : definition.Selection.SelectionCount == 1 && definition.Selection.Categories is [{ Category: 0, Subcategory: 0 } category] &&
                    category.Rewards.Select(row => row.ItemId).SequenceEqual(rewards) && category.Rewards.All(row => row.Count == 1),
                $"门票盒{source}随机/自选产物严格对应原文");
            foreach (int ticket in rewards)
            {
                var role = new Character(1, 0, "RaidBox", 0, towns.Room, towns.InitialReturn)
                {
                    Level = 95,
                    Inventory = new([new(1, Bag(65), InventoryRewardGrantService.Create(resources.Items.Get(source), utcNow: now))])
                };
                var request = new BoosterOpenRequest(65, 1, definition.Selection is null ? null : new(0, 0, [ticket]));
                var result = service.Open(role, request, out var receipt, now);
                That(result.Accepted && receipt?.Rewards is [{ Count: 1 } reward] && reward.ItemId == ticket,
                    $"真实门票盒{source}可开启为所选票据{ticket}且不重复扣购买材料");
                var granted = result.Plan!.After.Items.Single(item => item.Core.ItemId == ticket);
                int deadline = resources.Items.Get(ticket).Expiration.Resolve(now);
                That(deadline > now.ToUnixTimeSeconds() && granted.ExpireTime == deadline,
                    "产出票据按自身周删除声明冻结期限，活动外盒周四期限不能覆盖票据期限");
                That(!granted.IsExpired(deadline - 1L) && granted.IsExpired(deadline), "票据截止秒边界不延长有效期");
                That(role.Inventory.Items is [{ Core.ItemId: var original }] && original == source,
                    "开盒规划不提前改变输入库存");
                That(!service.Open(role with { Inventory = result.Plan.After }, request, out _, now).Accepted,
                    "重复使用已消费门票盒不能再次发奖");
                var expired = role with { Inventory = new([role.Inventory.Items[0] with
                    { Core = role.Inventory.Items[0].Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } }]) };
                That(!service.Open(expired, request, out _, now).Accepted, "过期外盒拒绝发票");
                if (definition.Selection is not null)
                {
                    That(!service.Open(role, new(65, 1), out _, now).Accepted &&
                        !service.Open(role, new(65, 1, new(0, 0, [10159649])), out _, now).Accepted &&
                        !service.Open(role, new(65, 1, new(0, 0, rewards)), out _, now).Accepted,
                        "自选门票盒拒绝空选、外来票与多选");
                }
            }
        }
        const string box = "[stackable type]\n`[booster]` 0\n[raid booster item]\n1\n" +
            "[booster info]\n[etc]\n1 10159649 1000 1\n[/etc]\n[/booster info]";
        foreach (string value in new[] { "", "0", "4", "-1", "x", "1 2", "1\n[raid booster item]\n1" })
            That(!BoosterCatalog.Parse(box.Replace("[raid booster item]\n1", "[raid booster item]\n" + value)).Available,
                "团本标记未知值、重复及畸形声明不放行");
        That(!BoosterCatalog.Parse(box.Replace("[raid booster item]\n1\n", "")
            .Replace("[etc]", "[raid booster item]\n1\n[etc]")).Available, "团本标记不能嵌入奖池绕过校验");
        That(!BoosterCatalog.Parse(box + "\n[total weekly usable count]\n1").Available,
            "接通来源标记不绕过尚未实现的使用配额");
        That(resources.Items.Get(10159649).Expiration.Resolve(now) ==
            new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds(),
            "卢克票据真实期限为下一周日六点");
        Console.WriteLine($"PASS {Count - checks} raid ticket booster resource checks");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
