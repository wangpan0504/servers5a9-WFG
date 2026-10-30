using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Network.Parsers;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class SkinStorageHandler(InventorySkinStorageService skins, InventoryProjection inventory,
    SkinStorageProjection projection, ServerLog log, OnlineSessionDirectory online) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.MAKE_SKIN, Make, nameof(SkinStorageHandler));
        commands.Register(CmdPacketType.SELECT_SKIN, Select, nameof(SkinStorageHandler));
        commands.Register(CmdPacketType.SEND_EMOTICON, SendEmoticon, nameof(SkinStorageHandler), UserStateRequirement.WorldReady);
    }

    Task<CommandResult> SendEmoticon(GameConnectionContext context, Packet packet)
    {
        int skinId;
        try { skinId = SkinStorageRequests.Emoticon(packet.Body); }
        catch (InvalidDataException) { return Task.FromResult(CommandResult.Handled); }
        var notice = context.Session.CaptureEmoticon(skins, skinId);
        if (notice is not null) online.DeliverEmoticon(notice);
        // 未发现专用ACK消费者；播放由NOTI1535驱动，本人也通过同一投递路径收到。
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> Make(GameConnectionContext context, Packet packet)
    {
        MakeWeaponSkinRequest request;
        try { request = SkinStorageRequests.Make(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.MAKE_SKIN, CargoPackets.Ack(InventoryResult.Reject(3)), "weapon_skin_make_malformed");
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? InventoryResult.Reject(3) :
            context.Session.PlanAndCommitInventory(character => skins.Make(character, request), 3);
        var receipt = result.Receipt;
        log.Write("weapon_skin_make", new { Connection = context.Id, request.Type, request.Recipe, request.Source,
            result.Accepted, result.Error, result.RejectReason, Revision = receipt?.Character.Revision });
        // 本版成功回调只启动制作表现，不自行扣源或创建产物；先同步提交后的完整实例。
        await inventory.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.MAKE_SKIN, CargoPackets.Ack(result),
            result.Accepted ? "weapon_skin_made" : "weapon_skin_make_rejected",
            receipt is null ? null : () => context.Session.CanPublishInventory(receipt));
        return CommandResult.Handled;
    }

    async Task<CommandResult> Select(GameConnectionContext context, Packet packet)
    {
        SelectSkinRequest request;
        try { request = SkinStorageRequests.Select(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.SELECT_SKIN, CargoPackets.Ack(InventoryResult.Reject(19)), "weapon_skin_select_malformed");
            return CommandResult.Handled;
        }
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => skins.Select(character, request), 19, requireTown: false);
        var receipt = result.Receipt;
        log.Write("skin_storage_select", new { Connection = context.Id, request.Type, request.SkinId,
            result.Accepted, result.Error, Revision = receipt?.Character.Revision });
        await context.Reply(CmdPacketType.SELECT_SKIN, SkinStoragePackets.SelectedAck(result, request),
            result.Accepted ? "weapon_skin_selected" : "weapon_skin_select_rejected",
            receipt is null ? null : () => context.Session.CanPublishInventory(receipt));
        await inventory.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    public async Task<CommandResult> UseAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool valid = request.Action == InventorySkinStorageService.AddAction && request.Reserved == 0 &&
            request.FirstArgument == 0 && request.SecondArgument == 0 && request.List == 0 && request.Slot <= short.MaxValue;
        var position = new InventorySlot(InventoryListType.Main, valid ? (short)request.Slot : (short)-1);
        var result = !valid || context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(3) :
            context.Session.PlanAndCommitInventory(character => skins.Add(character, position,
                before.Phase == Game.Session.SessionPhase.Town), 3, requireTown: false);
        var receipt = result.Receipt;
        var keys = result.Plan is { } plan ? skins.Keys(plan.Before.Find(position)) : [];
        bool Guard() => receipt is not null ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, before.Selected) && context.Session.SelectionEpoch == before.SelectionEpoch &&
            context.Session.Phase == before.Phase);
        log.Write("skin_storage_add", new { Connection = context.Id, request.Slot, request.List, request.Action,
            Skins = keys, result.Accepted, result.Error, result.RejectReason, Revision = receipt?.Character.Revision });
        // 028C0AE0先自行扣源，28C0930回调不额外读包；不能复用ACK44或再发一次源差量。
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request, result.Error),
            result.Accepted ? "skin_storage_token_used" : "skin_storage_token_rejected", Guard);
        await inventory.RefreshAsync(context, result, acknowledgedSlots: new HashSet<InventorySlot> { position });
        if (receipt is not null && !keys.IsEmpty) await projection.AddedAsync(context, receipt, keys);
        return CommandResult.Handled;
    }
}
