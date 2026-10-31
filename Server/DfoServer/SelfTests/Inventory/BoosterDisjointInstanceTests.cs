using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterDisjointInstanceTests
{
    public static void RunCore()
    {
        int checks = Count;
        var ordinary = new ItemCore(27582, ItemKind.Equipment, 50000) { Durability = 40 };
        foreach (bool noSoul in new[] { false, true })
        {
            // 从持久JSON构造，旧实现必须失败于字段丢失，而非依赖尚未存在的属性编译失败。
            var special = Restricted(ordinary, noSoul);
            That(Has(special, "DisjointForbidden") && Has(special, "DestructionSoulForbidden") == noSoul,
                "完整核心恢复礼包实例分解限制，不受同模板普通装备影响");
            special.Validate();
            That(special != ordinary && JsonSerializer.Deserialize<ItemCore>(JsonSerializer.Serialize(special)) == special,
                "实例限制参与核心身份并随JSON保存往返");
            foreach (var core in new[] { special, special with { EpicExchangeOrigin = new(1, 490705251), Extension119 = 1 } })
            {
                byte[] snapshot = AuctionPackets.ItemEntrySnapshot(core);
                That(AuctionPackets.ItemCoreFromEntry(snapshot, ItemKind.Equipment) == core,
                    "拍卖快照同时保留分解限制和既有史诗来源");
                Reject<InvalidDataException>(() => AuctionPackets.ItemCoreFromEntry(snapshot[..^1], ItemKind.Equipment),
                    "截断限制快照拒绝，不能静默还原成可分解装备");
                foreach (int offset in new[] { snapshot.Length - 8, snapshot.Length - 4 })
                {
                    var damaged = snapshot.ToArray();
                    Array.Clear(damaged, offset, 4);
                    Reject<InvalidDataException>(() => AuctionPackets.ItemCoreFromEntry(damaged, ItemKind.Equipment),
                        "损坏的限制trailer拒绝");
                }
            }
            That(Wire(special).SequenceEqual(Wire(ordinary)),
                "服务端限制不占用未闭合的客户端核心位，也不泄漏拍卖trailer");
            Reject<InvalidDataException>(() => (special with { Kind = ItemKind.Consumable }).Validate(),
                "装备实例限制不能附在消耗品子盒上");
        }
        var legacy = AuctionPackets.ItemEntrySnapshot(ordinary);
        var invalid = ordinary with { DestructionSoulForbidden = true };
        Reject<InvalidDataException>(() => invalid.Validate(), "孤立的毁装限制不能进入合法核心");
        Reject<InvalidDataException>(() => AuctionPackets.ItemEntrySnapshot(invalid), "写拍卖快照前拒绝非法限制，不能静默丢字段");
        That(legacy.Length == 123 && AuctionPackets.ItemCoreFromEntry(legacy, ItemKind.Equipment) == ordinary,
            "旧123B普通快照兼容且不反推限制");
        Console.WriteLine($"PASS {Count - checks} disjoint instance core checks");
    }

    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        RunOrdinary(towns, resources);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var catalog = resources.Items;
        var disjoint = new InventoryDisjointService(catalog, resources.Disjoint, resources.GoldLimits, resources.EpicPieces);
        foreach (int id in Enumerable.Range(490002024, 8).Append(490004483))
        {
            var definition = resources.Boosters.Get(id);
            That(definition is { Available: true, Lottery: true, SetsDisjointInstanceType: true, ForbidsDestructionSouls: true } &&
                definition.Pools.Length == 1, "九个英雄罐真实三元奖池与限制声明恢复 " + id);
            long boundary = 0;
            foreach (var row in definition.Pools.Single().Rewards.Where(row => row.Weight > 0))
            {
                var random = new PoolRandom(boundary);
                var service = new InventoryBoosterService(catalog, resources.Boosters, resources.GoldLimits, resources.EpicPieces, random);
                var role = new Character(1, 0, "RestrictedPot", 0, towns.Room, towns.InitialReturn)
                {
                    Level = 95,
                    Inventory = new([new(1, Bag(117), InventoryRewardGrantService.Create(catalog.Get(id), 2, utcNow: now))])
                };
                string before = JsonSerializer.Serialize(role);
                var opened = service.OpenLottery(role, new(0, 117), out var receipt, now);
                That(opened.Accepted && opened.Plan is not null && receipt is not null,
                    "真实CMD27领域入口按每行权重边界开罐 " + id + ":" + row.ItemId);
                var rewards = opened.Plan!.After.Items.Where(item => item.Core.ItemId == row.ItemId).ToArray();
                var expected = InventoryRewardGrantService.Create(catalog.Get(row.ItemId), utcNow: now) with
                    { DisjointForbidden = true, DestructionSoulForbidden = true };
                That(rewards.Length == row.Count && rewards.All(item => item.Core == expected) &&
                    opened.Plan.After.Find(Bag(117)) is { Uid: 1, Core.Count: 1 } &&
                    JsonSerializer.Serialize(role) == before && random.Calls == 1,
                    "奖池数量、完整核心、实例限制与源只扣一次保持 " + id + ":" + row.ItemId);
                foreach (var reward in rewards)
                {
                    var owned = role with { Inventory = opened.Plan.After };
                    var rejected = disjoint.Disjoint(owned, new(reward.Position, -1, 0), out var denied, now);
                    That(!rejected.Accepted && rejected.Plan is null && denied is null,
                        "同一真实奖励实例不可分解且不扣源 " + row.ItemId);
                }
                boundary += row.Weight;
            }
        }
        foreach (int id in Enumerable.Range(490010585, 5))
            That(!resources.Boosters.Get(id).Available,
                "二元legacy防具盒不借三元罐修复擅自开放未知入口 " + id);
        Console.WriteLine($"PASS {Count - checks} disjoint instance resource checks");
    }

    public static void RunOrdinary(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        const string declaration = "[set disjoint instance type]";
        const string script = "[stackable type]\n`[booster]` 0\n" + declaration +
            "\n[booster info]\n[equipment]\n1 400310136 1000 1\n[/equipment]\n[/booster info]";
        That(BoosterCatalog.Parse(script) is { Available: true, SetsDisjointInstanceType: true, ForbidsDestructionSouls: false } &&
            BoosterCatalog.Parse(script.Replace(declaration, declaration + "\n" + declaration)) is
                { Available: true, SetsDisjointInstanceType: true },
            "普通礼包根级空声明及幂等重复完整传递，不新增毁装灵魂规则");
        foreach (string invalid in new[]
        {
            script.Replace(declaration, declaration + "\n1"),
            script.Replace(declaration, declaration + "\n[/set disjoint instance type]"),
            script.Replace(declaration, "[set disjoint instance type"),
            script.Replace(declaration + "\n", "").Replace("[equipment]", declaration + "\n[equipment]")
        }) That(!BoosterCatalog.Parse(invalid).Available, "普通礼包非法非空/闭合冲突/残缺/嵌套声明仍拒绝");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        foreach (int id in new[] { 490014072, 490014073, 490704552, 490704553 })
        {
            var definition = resources.Boosters.Get(id);
            That(definition is { Available: true, Selection: null, Lottery: false, SetsDisjointInstanceType: true,
                ForbidsDestructionSouls: false, Pools.Length: 3 } &&
                definition.Pools.All(pool => pool is { Draws: 1, Rewards: [{ Weight: 1000, Count: 1 }] }),
                "四个普通限制礼包保留三个独立固定装备池和分解声明 " + id);
            int first = definition.Pools[0].Rewards[0].ItemId;
            var sentinel = new InventoryItem(99, Bag(10), InventoryRewardGrantService.Create(resources.Items.Get(first), utcNow: now));
            var source = new InventoryItem(1, Bag(65), InventoryRewardGrantService.Create(resources.Items.Get(id), shopExpiration: true, utcNow: now));
            var actor = new Character(1, 0, "OrdinaryRestricted", 0, towns.Room, towns.InitialReturn)
                { Level = 1, Inventory = new([source, sentinel], Gold: 12345) };
            string before = JsonSerializer.Serialize(actor);
            var result = service.Open(actor, new(65, 1), out var receipt, now);
            That(result.Accepted && result.Plan is not null && receipt is not null && result.Plan.After.Find(Bag(65)) is null &&
                result.Plan.After.Find(Bag(10)) == sentinel && result.Plan.After.Gold == actor.Inventory.Gold &&
                JsonSerializer.Serialize(actor) == before,
                "领取不附加95级穿戴门槛，源只扣一次且原同模板哨兵及其他资产保持 " + id);
            var rewards = result.Plan!.After.Items.Where(item => item.Uid == 0).ToArray();
            That(rewards.Length == 3 && receipt!.Rewards.Length == 3 &&
                rewards.Select(item => item.Core.ItemId).Order().SequenceEqual(definition.Pools.Select(pool => pool.Rewards[0].ItemId).Order()),
                "三个池整套发放，不因礼盒文字出现选择而误减为单件 " + id);
            foreach (var reward in rewards)
            {
                var expected = InventoryRewardGrantService.Create(resources.Items.Get(reward.Core.ItemId), shopExpiration: true, utcNow: now)
                    with { DisjointForbidden = true };
                That(reward.Core == expected && reward.Core.ExpireTime == now.AddDays(30).ToUnixTimeSeconds() &&
                    source.Core.ExpireTime == now.AddDays(id < 490700000 ? 7 : 15).ToUnixTimeSeconds(),
                    "十二件产物完整核心含30天期限和实例限制，不沿用源7/15天或额外禁止毁装灵魂 " + reward.Core.ItemId);
            }
            var full = actor with { Inventory = actor.Inventory with { Items = actor.Inventory.Items.AddRange(
                Enumerable.Range(9, 56).Where(slot => slot != 10).Select(slot =>
                    new InventoryItem(100 + slot, Bag((short)slot), sentinel.Core))) } };
            string fullBefore = JsonSerializer.Serialize(full);
            var denied = service.Open(full, new(65, 1), out var deniedReceipt, now);
            That(!denied.Accepted && denied.Error == 4 && denied.Plan is null && deniedReceipt is null &&
                JsonSerializer.Serialize(full) == fullBefore,
                "三件套满包整笔拒绝，源及全部原资产不变 " + id);
        }
        Console.WriteLine($"PASS {Count - checks} ordinary booster disjoint checks");
    }

    internal static ItemCore Restricted(ItemCore core, bool noSoul = false)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(core))!.AsObject();
        json["DisjointForbidden"] = true;
        json["DestructionSoulForbidden"] = noSoul;
        return JsonSerializer.Deserialize<ItemCore>(json.ToJsonString())!;
    }

    internal static bool Has(ItemCore core, string property) =>
        JsonSerializer.SerializeToElement(core).TryGetProperty(property, out var value) && value.GetBoolean();

    static byte[] Wire(ItemCore core)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        ItemListProtocolWriter.Write(writer, 0, core);
        return stream.ToArray();
    }

    static InventorySlot Bag(short index) => new(InventoryListType.Main, index);

    sealed class PoolRandom(long roll) : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue)
        {
            Calls++;
            if (roll < 0 || roll >= maxValue) throw new InvalidOperationException("测试奖池边界越界");
            return roll;
        }
    }
}
