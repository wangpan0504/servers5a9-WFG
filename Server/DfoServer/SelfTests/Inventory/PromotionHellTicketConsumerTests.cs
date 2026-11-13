using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionHellTicketConsumerTests
{
    internal static IReadOnlySet<int> CurrentHellTicketIds(ItemResources resources)
    {
        var archive = (PvfArchive)typeof(ItemCatalog)
            .GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(resources.Items)!;
        var towns = DfoServer.GameWorld.Towns.TownCatalog.Load(archive);
        return HellPartyTicketConsumers.From(towns, SelfTestCatalogs.Dungeons(archive));
    }

    public static void RunResources(ItemResources resources)
    {
        const int id = HarlemAbyssBonusDropPolicy.InvitationId;
        var hellTicketIds = CurrentHellTicketIds(resources);
        That(hellTicketIds.Contains(id), "当前邀请函连接具体副本的正数收费及可达深渊入口 " + id);
        var template = resources.Items.Get(id);
        That(template.Id == id && template.Kind == ItemKind.Material && !template.UseForbidden &&
            new ItemCore(template.Id, template.Kind, 1).IsStackable &&
            ItemSlotBoundService.List(template.Kind) == InventoryListType.Main,
            "真实深渊邀请函保持可作为入场票的主背包材料 " + id);

        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        var rules = original.Rules with
        {
            Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray()
        };
        var catalog = ServerRuntime.LoadCashShopPromotions(resources,
            JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }), hellTicketIds);
        var entry = catalog.Activities.Single();
        That(entry.Availability == PromotionAvailability.Active,
            "正式loader连接深渊派对入场票消费者 " + id + ": " + entry.Error);
    }
}
