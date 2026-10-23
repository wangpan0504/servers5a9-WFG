using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class CompoundHandler(InventoryRecipeCraftService crafts, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.COMPOUND_ITEM, Compound, nameof(CompoundHandler));

    async Task<CommandResult> Compound(GameConnectionContext context, Packet packet)
    {
        var request = CompoundRequests.Read(packet.Body);
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        RecipeCraftReceipt? recipe = null;
        InventoryResult result;
        try
        {
            result = context.BlocksTownActions ? InventoryResult.Reject(InventoryRecipeCraftService.Rejection) :
                context.Session.CraftRecipe(request.Raw, request.Mode, request.Count, crafts, out recipe);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or NotSupportedException or OverflowException)
        {
            log.Write("compound_item_resource_rejected", new { Connection = context.Id, request.Raw, request.Mode, Error = error.Message });
            result = InventoryResult.Reject(InventoryRecipeCraftService.Rejection) with { RejectReason = "invalid-resource-or-count" };
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            context.Session.SelectionEpoch == captured.SelectionEpoch && context.Session.Phase == captured.Phase &&
            context.Session.Selected?.Id == captured.Selected?.Id);
        var body = CompoundPackets.Result(result, recipe);
        log.Write("compound_item", new
        {
            Connection = context.Id, request.Raw, request.Mode, request.Count, Body = Convert.ToHexString(packet.Body),
            result.Accepted, result.Error, result.RejectReason, RecipeId = recipe?.RecipeId,
            Inputs = recipe?.Consumed.Length, Products = recipe?.Products.Length, recipe?.MakingGain,
            Revision = result.Receipt?.Character.Revision, ReplyBytes = body.Length
        });
        await context.Reply(CmdPacketType.COMPOUND_ITEM, body,
            result.Accepted ? "compound_item" : "compound_item_rejected", Guard);
        if (result.Accepted && recipe is not null)
        {
            var acknowledged = recipe.Consumed.Select(row => row.Position).Concat(recipe.Products.Select(row => row.Position)).ToHashSet();
            await projection.RefreshAsync(context, result, acknowledged, refreshEquipment: false);
            if (recipe.MakingGain is { } gain)
                await context.Notify(NotiPacketType.GAIN_ITEM_MAKING_SKILL_POINT, CompoundPackets.MakingGain(gain), "item_making_skill_gain", Guard);
        }
        return CommandResult.Handled;
    }
}
