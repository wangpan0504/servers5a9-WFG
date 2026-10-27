using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

// CMD271：设计图组与装备自己的 ticket 转换共用事务和权威库存投影。
internal sealed class UpgradeChronicleHandler(InventoryUpgradeChronicleService upgrades, InventoryProjection projection,
    GameWorld.Items.ItemCatalog items) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.UPGRADE_CHRONICLE, Upgrade, nameof(UpgradeChronicleHandler));
        commands.Register(CmdPacketType.REQUEST_ITEM_GROWTYPE_EXPERIENCE, Growth, nameof(UpgradeChronicleHandler));
    }

    async Task<CommandResult> Growth(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 0) return CommandResult.Unhandled;
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch));
        if (capture.Selected is not { } character) return CommandResult.Handled;
        var growing = character.Inventory.Items.Where(item => item.Position.List is InventoryListType.Main or InventoryListType.Equipment &&
            items.EquipmentGrowth.Get(item.Core.ItemId) is not null).ToArray();
        bool Guard() => context.Owner.Execute(() => ReferenceEquals(character, context.Session.Selected) &&
            capture.SelectionEpoch == context.Session.SelectionEpoch);
        foreach (var batch in growing.Chunk(byte.MaxValue))
            await context.Notify(NotiPacketType.GROWTH_ITEM_CHANGE_DATA, EquipmentGrowthPackets.State(batch, false),
                "equipment_growth_query", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> Upgrade(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
            capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        UpgradeChronicleRequest request;
        try { request = UpgradeChronicleRequests.Upgrade(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.UPGRADE_CHRONICLE,
                UpgradeChroniclePackets.Result(InventoryResult.Reject(InventoryUpgradeChronicleService.Rejection)),
                "upgrade_chronicle_malformed", SameSelection);
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? InventoryResult.Reject(InventoryUpgradeChronicleService.Rejection) :
            context.Session.PlanAndCommitInventory(character =>
            {
                try { return upgrades.Upgrade(character, request.Blueprint, request.BlueprintItemId, request.Target, request.TargetItemId,
                    materialPosition: request.Material, materialItemId: request.MaterialItemId); }
                catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or OverflowException)
                { return InventoryResult.Reject(InventoryUpgradeChronicleService.Rejection); }
            }, InventoryUpgradeChronicleService.Rejection);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : SameSelection();
        await context.Reply(CmdPacketType.UPGRADE_CHRONICLE, UpgradeChroniclePackets.Result(result),
            result.Accepted ? "upgrade_chronicle" : "upgrade_chronicle_rejected", Guard);
        // ACK 零消费行；只由既有投影清旧模板、写新模板和材料最终数量，防止双扣。
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
