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
    GuildNpcBenefitService? guildBenefits = null, EquipmentUpgradeAnnouncementProjection? announcements = null) : IGameCommandModule
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
        InventorySlot? effectSource = null;
        try
        {
            if (capture.Selected is { } sourceCharacter && service.IsEffectSource(sourceCharacter, request))
                effectSource = new(InventoryListType.Main, request.MaterialSlot);
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or OverflowException)
        { resourceInvalid = true; }
        var result = resourceInvalid || context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(InventoryEnhancementService.Rejection) :
            context.Session.PlanAndCommitInventory(character =>
            {
                if (!ReferenceEquals(character, capture.Selected)) return InventoryResult.Reject(InventoryEnhancementService.Rejection);
                try
                {
                    int bonus = guildBenefits?.SuccessPercent(context.Session.CaptureGuildManagement(),
                        request.Mode == EnhancementMode.Amplify ? "equip amplify upgrade buff" : "equip upgrade buff",
                        (character.Inventory.Find(request.Target)?.Core.Upgrade ?? 0) + 1) ?? 0;
                    return service.Plan(character, request, out receipt, guildSuccessPercent: bonus,
                        canUseSource: id => context.Session.CanUseStackableItem(character, id, automatic: true));
                }
                catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or OverflowException)
                {
                    resourceInvalid = true;
                    return InventoryResult.Reject(InventoryEnhancementService.Rejection);
                }
            }, InventoryEnhancementService.Rejection,
                effectItem: effectSource);
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) : SameSelection();
        log.Write("enhancement", new { Connection = context.Id, request.Mode, TargetList = request.Target.List,
            TargetSlot = request.Target.Index, request.MaterialSlot, result.Accepted, result.Error,
            Reason = resourceInvalid ? "resource_invalid" : result.Accepted ? "committed" : "rejected",
            receipt?.OldLevel, receipt?.NewLevel, receipt?.OutcomeCode, receipt?.GoldCost, Revision = result.Receipt?.Character.Revision,
            BaseSuccessPercent = result.Accepted ? receipt?.BaseSuccessPercent : null,
            SuccessPercent = result.Accepted ? receipt?.SuccessPercent : null });
        await context.Reply(CmdPacketType.UPGRADE_ITEM, EnhancementPackets.Ack(result, result.Accepted ? receipt : null),
            result.Accepted ? "enhancement_committed" : "enhancement_rejected", Guard);
        HashSet<InventorySlot>? acknowledged = null;
        if (result.Accepted && receipt is { InPlace: true })
        {
            acknowledged = [new(InventoryListType.Main, receipt.MaterialSlot)];
            if (receipt.OutcomeCode != 3 || result.Plan!.After.Find(receipt.Target) is null) acknowledged.Add(receipt.Target);
            if (receipt.TerminalSlot >= 0) acknowledged.Add(new(InventoryListType.Main, receipt.TerminalSlot));
        }
        try
        {
            await projection.RefreshAsync(context, result, acknowledged);
            if (result.Accepted && receipt is not null) announcements?.Enhancement(context, result, receipt);
            if (result.Accepted && result.Plan?.EquipmentEnhancement is { } fact && Guard())
                await DfoServer.Network.Projection.BattlePassMissionProjection.RefreshAsync(context, fact.Clock.GetUtcNow());
        }
        catch (System.Data.Common.DbException error) when (result.Accepted && result.Plan?.EquipmentEnhancement is not null)
        {
            // 库存和任务已提交，发布读取失败不能让外层重新抽签并重放扣费。
            throw new IOException("Committed equipment enhancement publication failed", error);
        }
        return CommandResult.Handled;
    }
}
