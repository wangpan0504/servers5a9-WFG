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
