using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class MarriageHandler
{
    public async Task<CommandResult> UseItemActionAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        if (request.Action == 93) return await UsePartnerTeleportActionAsync(context, request);
        async Task<CommandResult> Rejected()
        {
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, StackableActionPackets.Rejected(request), "marriage_action_rejected");
            return CommandResult.Handled;
        }
        if (request.Action is not (90 or 92) || request.Reserved != 0 || request.FirstArgument != 0 || request.SecondArgument != 0 ||
            request.List != (byte)InventoryListType.Main || request.Slot > short.MaxValue || context.BlocksTownActions ||
            context.Session.CaptureMarriage() is not { } capture || store.FindMarriage(capture.Character.Id) is not { State: 4 } before ||
            capture.Character.Inventory.Find(new(InventoryListType.Main, (short)request.Slot)) is not { } source)
            return await Rejected();
        try
        {
            var change = request.Action == 90
                ? MarriageWeddingRules.UpgradeRing(before, catalog, capture.Character.Id, checked((byte)(before.RingLevel + 1)), source.Core.ItemId)
                : MarriageWeddingRules.UseLovePointItem(before, catalog, capture.Character.Id, source.Core.ItemId);
            var plan = planner.Plan(capture.Character,
                [new(capture.Character.Id, change.Cost.Item.ItemId, -change.Cost.Item.Count, request.Slot)], timer.GetUtcNow());
            if (plan is null || context.Session.CommitMarriage(capture, plan, before, change.Record) is not { } result) return await Rejected();
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, StackableActionPackets.Used(request), "marriage_action_used");
            await inventory.RefreshAsync(context, result.Actor);
            await PublishRelationshipAsync(context, result.Record);
            return CommandResult.Handled;
        }
        catch (InvalidOperationException) { return await Rejected(); }
    }
}
