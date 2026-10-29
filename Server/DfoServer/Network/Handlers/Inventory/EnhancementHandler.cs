using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EnhancementHandler(InventoryEnhancementService service, InventoryProjection projection, ServerLog log,
    GuildNpcBenefitService? guildBenefits = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.UPGRADE_ITEM, Handle, nameof(EnhancementHandler));
    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
            capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        EnhancementRequest request;
        try { request = EnhancementRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("enhancement", new { Connection = context.Id, Accepted = false, Reason = "malformed", Length = packet.Body.Length });
            await context.Reply(CmdPacketType.UPGRADE_ITEM, CargoPackets.Ack(InventoryResult.Reject(InventoryEnhancementService.Rejection)), "enhancement_malformed", SameSelection);
            return CommandResult.Handled;
        }
        EnhancementReceipt? receipt = null;
        bool resourceInvalid = false;
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(InventoryEnhancementService.Rejection) :
            context.Session.PlanAndCommitInventory(character =>
            {
                try
                {
                    int bonus = guildBenefits?.SuccessPercent(context.Session.CaptureGuildManagement(),
                        request.Mode == EnhancementMode.Amplify ? "equip amplify upgrade buff" : "equip upgrade buff",
                        (character.Inventory.Find(request.Target)?.Core.Upgrade ?? 0) + 1) ?? 0;
                    return service.Plan(character, request, out receipt, guildSuccessPercent: bonus);
                }
                catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or OverflowException)
                {
                    resourceInvalid = true;
                    return InventoryResult.Reject(InventoryEnhancementService.Rejection);
                }
            }, InventoryEnhancementService.Rejection);
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) : SameSelection();
        log.Write("enhancement", new { Connection = context.Id, request.Mode, TargetList = request.Target.List,
            TargetSlot = request.Target.Index, request.MaterialSlot, result.Accepted, result.Error,
            Reason = resourceInvalid ? "resource_invalid" : result.Accepted ? "committed" : "rejected",
            receipt?.OldLevel, receipt?.NewLevel, receipt?.OutcomeCode, receipt?.GoldCost, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.UPGRADE_ITEM, EnhancementPackets.Ack(result, result.Accepted ? receipt : null),
            result.Accepted ? "enhancement_committed" : "enhancement_rejected", Guard);
        HashSet<InventorySlot>? acknowledged = null;
        if (result.Accepted && receipt is { InPlace: true })
        {
            acknowledged = [new(InventoryListType.Main, receipt.MaterialSlot)];
            if (receipt.OutcomeCode != 3 || result.Plan!.After.Find(receipt.Target) is null) acknowledged.Add(receipt.Target);
            if (receipt.TerminalSlot >= 0) acknowledged.Add(new(InventoryListType.Main, receipt.TerminalSlot));
        }
        await projection.RefreshAsync(context, result, acknowledged);
        return CommandResult.Handled;
    }
}
