using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class PromotionAvatarCompoundConsumerTests
{
    static readonly int[] RealSources = [21, 899];

    public static void RunResources(ItemResources resources)
    {
        RunBoundaries(resources);
        foreach (int id in RealSources)
        {
            var template = resources.Items.Get(id);
            That(resources.AvatarCompounds.ForMaterial(id).Any() && template.Id == id && !template.UseForbidden &&
                new ItemCore(template.Id, template.Kind, 1).IsStackable &&
                ItemSlotBoundService.List(template.Kind) == InventoryListType.Main,
                "真实装扮合成材料保持完整typed输入定义 " + id);
        }

        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        foreach (int id in RealSources)
        {
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                    ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray()
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                "正式loader连接真实装扮合成材料消费者 " + id + ": " + entry.Error);
        }
    }
}
