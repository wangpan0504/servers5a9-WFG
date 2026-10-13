using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class BoosterHandler(InventoryBoosterService booster, InventoryProjection projection,
    InventoryAvatarPackageService avatars, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.USE_BOOSTER_ITEM, Handle, nameof(BoosterHandler));
        commands.Register(CmdPacketType.OPEN_CERAPACKAGE, HandleCeraPackage, nameof(BoosterHandler));
    }

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        short sourceSlot = BoosterRequests.ReadSourceSlot(packet.Body);
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        BoosterReceipt? receipt = null;
        InventoryResult result;
        bool avatarSource = false;
        try
        {
            var source = capture.Selected?.Inventory.Find(new(InventoryListType.Main, sourceSlot));
            var definition = avatars.Definition(source?.Core.ItemId ?? 0);
            avatarSource = definition.Recognized;
            if (definition.Recognized)
            {
                var header = AvatarPackageRequests.ReadHeader(packet.Body);
                var category = definition.Categories.FirstOrDefault(row => row.Category == header.Category && row.Subcategory == header.Subcategory);
                if (!definition.Available || category is null || category.Rows.IsEmpty)
                    result = InventoryResult.Reject(19);
                else
                {
                    int minimum = definition.SelectionCount == 0 ? category.Rows.Length : 1;
                    int maximum = definition.SelectionCount == 0 ? category.Rows.Length :
                        Math.Min(definition.SelectionCount, category.Rows.Select(row => row.ItemId).Distinct().Count());
                    var request = AvatarPackageRequests.Read(packet.Body, minimum, maximum);
                    result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
                        context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, capture.Selected) ?
                            avatars.Open(character, request, out receipt, (clock ?? TimeProvider.System).GetUtcNow()) :
                            InventoryResult.Reject(19), 19, requireTown: false);
                }
            }
            else if (source is null && packet.Body.Length > 6 && !BoosterRequests.IsItemSelection(packet.Body) &&
                !AvatarPackageRequests.IsSelection(packet.Body))
                return CommandResult.Unhandled;
            else if (source is null)
                result = InventoryResult.Reject(19);
            else
            {
                // 尚未闭合的其他附加选择段保持未实现，不截断成普通物品选择。
                if (packet.Body.Length > 6 && !BoosterRequests.IsItemSelection(packet.Body))
                    return CommandResult.Unhandled;
                var request = BoosterRequests.Read(packet.Body);
                result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
                    context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, capture.Selected) ?
                        booster.Open(character, request, out receipt) : InventoryResult.Reject(19), 19, requireTown: false);
            }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            result = InventoryResult.Reject(19);
        }
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("booster_open", new { Connection = context.Id, Slot = sourceSlot, Avatar = avatarSource,
            result.Accepted, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.USE_BOOSTER_ITEM, BoosterPackets.Opened(result, receipt),
            result.Accepted ? "booster_opened" : "booster_open_rejected", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> HandleCeraPackage(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
            capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        CeraPackageOpenRequest request;
        try
        {
            request = CeraPackageRequests.Read(packet.Body);
        }
        catch (InvalidDataException)
        {
            log.Write("booster_open", new { Connection = context.Id, CeraPackage = true, Accepted = false, Reason = "malformed", Length = packet.Body.Length });
            await context.Reply(CmdPacketType.OPEN_CERAPACKAGE, CeraPackagePackets.Opened(InventoryResult.Reject(19), null),
                "cera_package_malformed", SameSelection);
            return CommandResult.Handled;
        }

        BoosterReceipt? receipt = null;
        InventoryResult result;
        try
        {
            var source = capture.Selected?.Inventory.Find(new(InventoryListType.Main, request.Slot));
            bool avatar = avatars.Definition(source?.Core.ItemId ?? 0).Recognized;
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(4) :
                context.Session.PlanAndCommitInventory(character => !ReferenceEquals(character, capture.Selected) ? InventoryResult.Reject(19) :
                    avatar ? avatars.OpenCeraPackage(character, request, out receipt, (clock ?? TimeProvider.System).GetUtcNow()) :
                    booster.OpenCeraPackage(character, request, out receipt), 19, requireTown: false);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            result = InventoryResult.Reject(19);
        }
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) : SameSelection();
        log.Write("booster_open", new { Connection = context.Id, Slot = request.Slot, CeraPackage = true,
            result.Accepted, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.OPEN_CERAPACKAGE, CeraPackagePackets.Opened(result, request),
            result.Accepted ? "cera_package_opened" : "cera_package_rejected", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
