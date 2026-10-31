using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using System.Collections.Concurrent;
using System.Reflection;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterCreatureTests
{
    public static void RunCore()
    {
        var parsed = Parse("1 400990271 10000 1");
        That(parsed.Available && parsed.Pools is [{ Draws: 1, Rewards: [{ ItemId: 400990271, Weight: 10000, Count: 1 }] }],
            "宠物三列随机池保留抽取次数、编号、权重和数量");
        That(parsed.Pools[0].RequiredKind == ItemKind.Creature, "宠物池保留显式真实模板类型要求");
        foreach (string bad in new[] { "", "0 400990271 1 1", "1 400990271 -1 1", "1 400990271 1 0", "1 400990271 1", "1 0 1 1" })
            That(!Parse(bad).Available, "宠物随机池拒绝空、负权重、无效数量及残缺行");
        That(!BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n[avatar]\n1 400990271 1 1\n[/avatar]\n[/booster info]").Available,
            "缺少装扮尾列的畸形奖池行保持拒绝，不把宠物三元行宽套用于装扮");
    }

    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true });
        foreach (var sample in new[] { (Box: 490703983, Pet: 400990271), (Box: 490704027, Pet: 400990276),
            (Box: 490704044, Pet: 400990275), (Box: 490704051, Pet: 400990284) })
        {
            That(resources.Boosters.Get(sample.Box) is { Available: true, Pools: [{ Draws: 1 }] }, "实际三列宠物礼盒可解析 " + sample.Box);
            var source = new InventoryItem(1, new(InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(resources.Items.Get(sample.Box), 2, shopExpiration: true));
            var role = new Character(1, 0, "CreatureBox", 0, towns.Room, towns.InitialReturn) { Job = 15, Level = 95, Inventory = new([source]) };
            var result = service.Open(role, new(65, 1), out var receipt);
            That(result.Accepted && receipt!.Rewards is [{ Count: 1 }] && receipt.Rewards[0].ItemId == sample.Pet &&
                result.Plan!.After.Find(source.Position)!.Core.Count == 1 &&
                result.Plan.After.Items.Single(item => item.Core.ItemId == sample.Pet) is
                    { Position.List: InventoryListType.Pet, Creature: not null, Core.Kind: ItemKind.Creature, Core.Count: 1 } &&
                role.Inventory.Items.Single() == source, "宠物礼盒按真实模板生成专属页完整详情，源只扣一次 " + sample.Box);
            foreach (var denied in new[] { source with { Core = source.Core with { EquipmentLockId = 1 } },
                source with { Core = source.Core with { ExpireTime = 1 } } })
                That(!service.Open(role with { Inventory = new([denied]) }, new(65, 1), out _).Accepted,
                    "宠物池不绕过源锁定和期限限制");
            var petRange = ItemSlotBoundService.Range(ItemKind.Creature, role.Inventory);
            var full = Enumerable.Range(petRange.Start, petRange.End - petRange.Start + 1).Select(slot =>
                InventoryRewardGrantService.CreateItem(resources.Items.Get(sample.Pet), new(InventoryListType.Pet, (short)slot),
                    InventoryRewardGrantService.Create(resources.Items.Get(sample.Pet))));
            var blocked = role with { Inventory = new([source, .. full]) };
            That(!service.Open(blocked, new(65, 1), out _).Accepted && blocked.Inventory.Find(source.Position) == source,
                "宠物页满拒绝整包且保留原源，无普通背包降级");
        }
        TypedCandidatePreflight(towns, resources);
    }

    static void TypedCandidatePreflight(TownCatalog towns, ItemResources resources)
    {
        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resources.Items)!;
        var isolated = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(isolated)!;
        var random = new FirstRandom();
        var service = new InventoryBoosterService(resources.Items, isolated, resources.GoldLimits, resources.EpicPieces, random,
            debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true });
        var source = new InventoryItem(1, new(InventoryListType.Main, 65),
            InventoryRewardGrantService.Create(resources.Items.Get(490703983), 2, shopExpiration: true));
        var role = new Character(1, 0, "TypedPool", 0, towns.Room, towns.InitialReturn) { Job = 15, Level = 95, Inventory = new([source]) };
        foreach (int invalid in new[] { 1106, 1, int.MaxValue, 30 })
        {
            definitions[490703983] = Parse($"1 400990271 100 1 {invalid} 1 1");
            int calls = random.Calls;
            That(!service.Open(role, new(65, 1), out var receipt).Accepted && receipt is null && random.Calls == calls &&
                role.Inventory.Items.Single() == source,
                "宠物池抽取前检查全部正权重候选，错误类型、钱包、缺失模板及契约均整包拒绝 " + invalid);
        }
        definitions[490703983] = Parse("1 400990271 100 1 1106 0 9");
        That(service.Open(role, new(65, 1), out var zeroReceipt).Accepted && zeroReceipt!.Rewards is [{ ItemId: 400990271, Count: 1 }],
            "零权重候选不参与创建及类型预检，不能错误阻塞合法奖励");
        definitions[490703983] = Parse("2 400990271 1 1 400990271 1 2");
        int before = random.Calls;
        var batch = service.Open(role, new(65, 2), out var batchReceipt);
        That(batch.Accepted && random.Calls == before + 4 && batchReceipt!.Rewards is [{ ItemId: 400990271, Count: 4 }] &&
            batch.Plan!.After.Items.Count(item => item.Creature is not null) == 4 && batch.Plan.After.Find(source.Position) is null,
            "宠物池重复奖励行保留独立抽取及源批量倍数，不强行合并奖池概率或截断数量");
        random.Last = true;
        before = random.Calls;
        batch = service.Open(role, new(65, 2), out batchReceipt);
        That(batch.Accepted && random.Calls == before + 4 && batchReceipt!.Rewards is [{ ItemId: 400990271, Count: 8 }] &&
            batch.Plan!.After.Items.Count(item => item.Creature is not null) == 8,
            "同编号第二条奖励行命中时保留数量2及源批量倍数");
    }

    sealed class FirstRandom : Random
    {
        public int Calls { get; private set; }
        public bool Last { get; set; }
        public override long NextInt64(long maxValue) { Calls++; return Last ? maxValue - 1 : 0; }
    }

    static BoosterDefinition Parse(string body) => BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n[creature]\n" + body + "\n[/creature]\n[/booster info]");
}
