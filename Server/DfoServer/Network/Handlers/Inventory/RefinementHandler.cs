using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Guild;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class RefinementHandler(InventoryRefinementService refinement, InventoryProjection projection,
    IRefinementEffects? effects = null, GuildNpcBenefitService? guildBenefits = null,
    EquipmentUpgradeAnnouncementProjection? announcements = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.UPGRADE_ITEM_SEPARATE, Handle, nameof(RefinementHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = RefinementRequests.Read(packet.Body);
        RefinementReceipt? receipt = null;
        Character? committed = null;
        var result = context.Owner.Execute(() =>
        {
            var outcome = context.BlocksTownActions ? InventoryResult.Reject(4) :
                context.Session.MutateInventory(character => refinement.Refine(character, request, out receipt,
                    guildSuccessPercent: guildBenefits?.SuccessPercent(context.Session.CaptureGuildManagement(),
                        "equip upgrade separate buff", (character.Inventory.Find(request.Position)?.Core.GenuineUpgrade ?? 0) + 1) ?? 0), 4);
            if (outcome.Accepted)
                committed = context.Session.Selected;
            return outcome;
        });
        await context.Reply(CmdPacketType.UPGRADE_ITEM_SEPARATE, RefinementPackets.Ack(result, receipt),
            result.Accepted ? "equipment_refined" : "equipment_refinement_rejected");
        await projection.RefreshAsync(context, result);
        if (result.Accepted && receipt is not null) announcements?.Refinement(context, result, receipt);
        if (result.Accepted && effects is not null && committed is not null && receipt is not null)
        {
            // 只在lease内开始发布/入队，网络等待在外；回调失败不回滚资产或重试本次锻造。
            await context.Owner.Execute(() => effects.PublishAsync(context, committed, receipt));
        }
        return CommandResult.Handled;
    }
}
