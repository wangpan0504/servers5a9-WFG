using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class AmplificationHandler(InventoryAmplificationService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.PURIFY_ITEM, Handle, nameof(AmplificationHandler));
        commands.Register(CmdPacketType.INVEST_ITEM_AMPLIFY_OPTION, Handle, nameof(AmplificationHandler));
    }

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        AmplificationReceipt? receipt = null;
        AmplificationRequest? request = null;
        InventoryResult result;
        string reason;
        try
        {
            request = packet.Opcode == (ushort)CmdPacketType.PURIFY_ITEM ? AmplificationRequests.Purify(packet.Body) :
                AmplificationRequests.Invest(packet.Body);
        }
        catch (InvalidDataException)
        {
            request = null;
        }
        bool invalidResource = false;
        result = request is null || context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(InventoryAmplificationService.Rejection) :
            context.Session.PlanAndCommitInventory(character =>
            {
                try { return service.Plan(character, request, out receipt); }
                catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException or ArgumentException)
                {
                    invalidResource = true;
                    return InventoryResult.Reject(InventoryAmplificationService.Rejection);
                }
            }, InventoryAmplificationService.Rejection,
                effectItem: request.MaterialSlot >= 0 ? new(InventoryListType.Main, request.MaterialSlot) : null,
                useTarget: packet.Opcode == (ushort)CmdPacketType.INVEST_ITEM_AMPLIFY_OPTION
                    ? new(InventoryListType.Main, request.TargetSlot) : null);
        // 只在锁外规划内转换资源错误；数据库提交异常沿原链路关闭连接，不伪装为业务失败。
        reason = request is null ? "malformed" : invalidResource ? "resource_invalid" : result.Accepted ? "committed" : "rejected";
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("amplification_option", new { Connection = context.Id, Opcode = packet.Opcode, request?.Operation,
            AppliedOperation = receipt?.Operation, request?.TargetSlot, request?.MaterialSlot, result.Accepted, result.Error,
            Reason = reason, Revision = result.Receipt?.Character.Revision });
        await context.Reply((CmdPacketType)packet.Opcode, AmplificationPackets.Ack(result, receipt), "amplification_" + reason, Guard);
        if (result.Accepted && receipt is not null)
        {
            // 原生成功回执原位改源数量及目标，205结果窗继续持有该对象，不能用NOTI14重建它。
            var acknowledged = new HashSet<InventorySlot> { new(InventoryListType.Main, receipt.MaterialSlot) };
            if (!receipt.RefreshTarget) acknowledged.Add(new(InventoryListType.Main, receipt.TargetSlot));
            await projection.RefreshAsync(context, result, acknowledged, refreshEquipment: false, suppressMainNotice: true);
        }
        return CommandResult.Handled;
    }
}
