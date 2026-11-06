using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionTeleportPotionConsumerTests
{
    static readonly int[] RealSources =
    [
        2600014, 2680728, 2680784, 10005881, 490002897, 490003913, 490004692,
        490005210, 490005509, 490005739, 490006904, 490006908, 490007514,
        490008100, 490009401, 490010436, 490011461
    ];

    public static void RunResources(ItemResources resources)
    {
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        foreach (int id in RealSources)
        {
            var definition = resources.Teleports.Potion(id);
            var template = resources.Items.Get(id);
            That(template.Kind == ItemKind.Consumable && template.StackableType == "[teleport potion] 0" &&
                !template.UseForbidden && template.MinimumLevel == definition?.MinimumLevel,
                "真实瞬移药剂模板与typed目录保持一致 " + id);
            That(definition is { CoolTime: > 0 } && definition.DungeonMaps.Count > 0,
                "真实瞬移药剂声明保留冷却与地下城地图表 " + id);
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                    ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray(),
                OverflowItemId = id,
                OverflowCount = 1
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                "正式loader连接真实瞬移药剂消费者 " + id + ": " + entry.Error);
        }
        foreach (int id in new[] { 10148813, 490000661 })
        {
            That(resources.Teleports.Potion(id) is null,
                "NPC瞬移药剂不借普通瞬移消费者定义 " + id);
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                    ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray(),
                OverflowItemId = id,
                OverflowCount = 1
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Invalid &&
                entry.Error?.Contains("consumer_not_connected", StringComparison.Ordinal) == true,
                "NPC瞬移药剂仍保留未接入状态 " + id + ": " + entry.Error);
        }
    }
}
