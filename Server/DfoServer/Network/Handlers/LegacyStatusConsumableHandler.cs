using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class LegacyStatusConsumableHandler(LegacyStatusConsumableService service, InventoryProjection inventory, ServerLog log)
{
    public async Task<CommandResult?> TryUseAsync(GameConnectionContext context, InventorySlot slot)
    {
        if (!context.Owner.Execute(() => LegacyStatusConsumableDefinition.IsNativeItem(context.Session.Selected?.Inventory.Find(slot)?.Core.ItemId ?? 0)))
            return null;
        var result = context.Session.UseLegacyStatusConsumable(slot, service, context.Activity?.BlocksTownActions == true);
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        var guard = result.Guard ?? throw new InvalidOperationException("旧状态道具缺少原场景身份");
        bool Guard() => context.Session.CanPublishConsumable(guard);
        log.Write("consumable_use", new { Connection = context.Id, Opcode = (ushort)CmdPacketType.INCREASE_STATUS,
            Slot = slot.Index, Item = result.LegacyStatus?.Source.Core.ItemId ?? guard.Character?.Inventory.Find(slot)?.Core.ItemId,
            Accepted = result.Outcome == ConsumableUseOutcome.Committed, result.Reason,
            Character = guard.Character?.Id, Revision = guard.Character?.Revision, result.SourceUid });
        if (result.LegacyStatus is not { } plan || result.Inventory?.Receipt is not { } receipt)
        {
            await context.Reply(CmdPacketType.INCREASE_STATUS, ExperienceConsumablePackets.Rejected(), "legacy_status_rejected", Guard);
            return CommandResult.Handled;
        }
        // 币券的原生消费回执不增加本地钱包，随后同步同次提交的真实余额。
        await context.Reply(CmdPacketType.INCREASE_STATUS, ExperienceConsumablePackets.Legacy(plan), "legacy_status_used", Guard);
        await inventory.RefreshAsync(context, result.Inventory, new HashSet<InventorySlot> { slot }, refreshEquipment: false);
        return CommandResult.Handled;
    }
}
