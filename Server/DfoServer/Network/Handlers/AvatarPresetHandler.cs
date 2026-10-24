using System.Data.Common;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

// 三笔预设C2S都没有专用成功/失败ACK：成功用0x063D/0x063F回放提交结果，失败只记日志、不发包。
internal sealed class AvatarPresetHandler(AvatarPresetService presets, AvatarPresetProjection projection,
    InventoryProjection inventory, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CHANGE_AVATAR_PRESET_NAME, RenameAsync, nameof(AvatarPresetHandler));
        commands.Register(CmdPacketType.SELECT_AVATAR_PRESET, SelectAsync, nameof(AvatarPresetHandler));
        commands.Register(CmdPacketType.EXCHANGE_AVATAR_PRESET_POSITION, ExchangeAsync, nameof(AvatarPresetHandler));
    }

    async Task<CommandResult> RenameAsync(GameConnectionContext context, Packet packet)
    {
        AvatarPresetNameRequest request;
        try { request = AvatarPresetRequests.Name(packet.Body); }
        catch (InvalidDataException error)
        {
            log.Write("avatar_preset_name_rejected", new { Connection = context.Id, Length = packet.Body.Length, Reason = error.Message });
            return CommandResult.Handled;
        }
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        if (before.Selected is null || context.BlocksTownActions)
        {
            log.Write("avatar_preset_name_rejected", new { Connection = context.Id, request.Slot, request.Name, Reason = "state" });
            return CommandResult.Handled;
        }
        bool created = before.Selected.Inventory.AvatarPresets.Find(request.Slot) is null;
        InventoryResult result;
        try
        {
            result = context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, before.Selected)
                ? presets.PlanName(character, request.Slot, request.Name) : InventoryResult.Reject(19), 19);
        }
        catch (Exception error) when (error is DbException or InvalidDataException or KeyNotFoundException)
        {
            log.Write("avatar_preset_name_failed", new { Connection = context.Id, request.Slot, Error = error.Message });
            result = InventoryResult.Reject(19);
        }
        log.Write("avatar_preset_name", new { Connection = context.Id, request.Slot, request.Name, created,
            result.Accepted, result.Error, Revision = result.Receipt?.Character.Revision });
        if (result.Receipt is not { } receipt) return CommandResult.Handled;
        // 0x063F只覆盖客户端缓存、不重绘行文字，只发单条会让列表停在旧名字；改名与新建必须回整表，
        // 六行名字才会立刻跟随落库结果。整表会把客户端当前行与名称框重置为槽0（规则见SelectAsync），
        // 名称框因此显示1号橱窗的名字，与第一行内容一致。
        await projection.ListAsync(context, receipt, created ? "avatar_preset_created" : "avatar_preset_renamed");
        return CommandResult.Handled;
    }

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet packet)
    {
        AvatarPresetSlotRequest request;
        try { request = AvatarPresetRequests.Select(packet.Body); }
        catch (InvalidDataException error)
        {
            log.Write("avatar_preset_select_rejected", new { Connection = context.Id, Length = packet.Body.Length, Reason = error.Message });
            return CommandResult.Handled;
        }
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        if (before.Selected is null || context.BlocksTownActions)
        {
            log.Write("avatar_preset_select_rejected", new { Connection = context.Id, request.Slot, Reason = "state" });
            return CommandResult.Handled;
        }
        InventoryResult result;
        try
        {
            result = context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, before.Selected)
                ? presets.PlanSelect(character, request.Slot) : InventoryResult.Reject(19), 19);
        }
        catch (Exception error) when (error is DbException or InvalidDataException or KeyNotFoundException)
        {
            log.Write("avatar_preset_select_failed", new { Connection = context.Id, request.Slot, Error = error.Message });
            result = InventoryResult.Reject(19);
        }
        log.Write("avatar_preset_select", new { Connection = context.Id, request.Slot, result.Accepted, result.Error,
            Revision = result.Receipt?.Character.Revision });
        if (result.Receipt is not { } receipt) return CommandResult.Handled;
        await inventory.RefreshAsync(context, result);
        // 客户端的应用按钮只发0x063A、自身不刷新装扮栏预览，纸娃娃只在整表回放触发的行刷新里
        // 按当前穿戴重建（FUN_028563F0 -> FUN_02854D10）；缺这一帧时左边人物停在旧外观，
        // 玩家必须再点一行才更新。穿戴确有变化才回整表；整表解析器FUN_008407B0会把当前行设成
        // 容量值并投递UI消息0x18(capacity)，行号越界时FUN_0083e940返回槽0，因此选中行与名称框
        // 会被拉回1号橱窗，这是客户端固定行为；纸娃娃仍按刚穿上的当前穿戴重建。
        if (result.Plan is { } plan && plan.ChangedSlots.Any(slot =>
                slot.List is InventoryListType.Avatar or InventoryListType.Equipment))
            await projection.ListAsync(context, receipt, "avatar_preset_selected");
        return CommandResult.Handled;
    }

    async Task<CommandResult> ExchangeAsync(GameConnectionContext context, Packet packet)
    {
        AvatarPresetExchangeRequest request;
        try { request = AvatarPresetRequests.Exchange(packet.Body); }
        catch (InvalidDataException error)
        {
            log.Write("avatar_preset_exchange_rejected", new { Connection = context.Id, Length = packet.Body.Length, Reason = error.Message });
            return CommandResult.Handled;
        }
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        if (before.Selected is null || context.BlocksTownActions)
        {
            log.Write("avatar_preset_exchange_rejected", new { Connection = context.Id, request.From, request.To, Reason = "state" });
            return CommandResult.Handled;
        }
        InventoryResult result;
        try
        {
            result = context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, before.Selected)
                ? presets.PlanExchange(character, request.From, request.To) : InventoryResult.Reject(19), 19);
        }
        catch (Exception error) when (error is DbException or InvalidDataException or KeyNotFoundException)
        {
            log.Write("avatar_preset_exchange_failed", new { Connection = context.Id, request.From, request.To, Error = error.Message });
            result = InventoryResult.Reject(19);
        }
        log.Write("avatar_preset_exchange", new { Connection = context.Id, request.From, request.To, result.Accepted, result.Error });
        if (result.Receipt is not { } receipt) return CommandResult.Handled;
        await projection.ListAsync(context, receipt, "avatar_preset_exchanged");
        return CommandResult.Handled;
    }

    // CMD515 action216：扩展券成功ACK由客户端自行扣一张，随后只回放容量与源槽剩余量。
    internal async Task<CommandResult> UseCapacityAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var source = new InventorySlot(InventoryListType.Main, unchecked((short)request.Slot));
        bool valid = request.List == (byte)InventoryListType.Main && request.Slot <= short.MaxValue &&
            request.Reserved == 0 && request.FirstArgument == 0 && request.SecondArgument == 0;
        var definition = valid ? presets.Definition(before.Selected?.Inventory.Find(source)?.Core.ItemId ?? 0) : null;
        InventoryResult result;
        try
        {
            result = !valid || before.Selected is null || definition is null || context.BlocksTownActions
                ? InventoryResult.Reject(19)
                : context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, before.Selected)
                    ? presets.PlanCapacity(character, source, definition, before.Phase == SessionPhase.Town)
                    : InventoryResult.Reject(19), 19, requireTown: false, requiredPhase: before.Phase);
        }
        catch (Exception error) when (error is DbException or InvalidDataException or KeyNotFoundException)
        {
            log.Write("avatar_preset_capacity_failed", new { Connection = context.Id, request.Slot, Error = error.Message });
            result = InventoryResult.Reject(19);
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, before.Selected) && context.Session.SelectionEpoch == before.SelectionEpoch &&
            context.Session.Phase == before.Phase);
        log.Write("avatar_preset_capacity", new { Connection = context.Id, request.Slot, Item = definition?.Item.Id,
            result.Accepted, result.Error, Before = before.Selected?.Inventory.AvatarPresets.Capacity,
            After = result.Receipt?.Character.Inventory.AvatarPresets.Capacity });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            // 失败必须带非零错误码：0 会让客户端当作无事发生，容量已满时看不到任何提示。
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request, result.Error),
            result.Accepted ? "avatar_preset_capacity_committed" : "avatar_preset_capacity_rejected", Guard);
        if (!result.Accepted || result.Receipt is not { } receipt) return CommandResult.Handled;
        await inventory.RefreshAsync(context, result, new HashSet<InventorySlot> { source }, suppressMainNotice: true);
        await projection.ListAsync(context, receipt, "avatar_preset_capacity");
        return CommandResult.Handled;
    }
}
