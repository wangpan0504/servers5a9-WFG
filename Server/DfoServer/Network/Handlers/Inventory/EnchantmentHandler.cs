using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EnchantmentHandler(InventoryEnchantmentService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.ENCHANT_BY_BEAD, Handle, nameof(EnchantmentHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        EnchantmentRequest request;
        try { request = EnchantmentRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.ENCHANT_BY_BEAD, CargoPackets.Ack(InventoryResult.Reject(InventoryEnchantmentService.Rejection)),
                "enchantment_malformed");
            return CommandResult.Handled;
        }
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        int itemId = capture.Selected?.Inventory.Find(request.Source)?.Core.ItemId ?? 0;
        var definition = context.Activity?.BlocksTownActions == true ? null : service.Definition(itemId);
        var result = definition is null ? InventoryResult.Reject(InventoryEnchantmentService.Rejection) :
            context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, capture.Selected) ?
                service.Plan(character, request, service.Clock.GetUtcNow().ToUnixTimeSeconds(), definition) :
                InventoryResult.Reject(InventoryEnchantmentService.Rejection),
                InventoryEnchantmentService.Rejection, requireTown: false,
                requiredPhase: definition.VillageOnly ? SessionPhase.Town : null);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("enchantment", new { Connection = context.Id, SourceList = request.Source.List, SourceSlot = request.Source.Index,
            TargetList = request.Target.List, TargetSlot = request.Target.Index, result.Accepted, Revision = result.Receipt?.Character.Revision });
        // 成功ACK会立即读取目标附魔并按描述尺寸创建结果窗，必须先安装已提交的核心。
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.ENCHANT_BY_BEAD, EnchantmentPackets.Ack(result, request),
            result.Accepted ? "enchantment_committed" : "enchantment_rejected", Guard);
        return CommandResult.Handled;
    }
}
