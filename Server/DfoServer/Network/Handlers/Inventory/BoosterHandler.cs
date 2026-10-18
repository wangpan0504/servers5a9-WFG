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
        commands.Register(CmdPacketType.USE_LOTTERY_ITEM, HandleLottery, nameof(BoosterHandler));
        commands.Register(CmdPacketType.OPEN_CERAPACKAGE, HandleCeraPackage, nameof(BoosterHandler));
    }

    async Task<CommandResult> HandleLottery(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        LotteryReceipt? receipt = null;
        LotteryOpenRequest? request = null;
        InventoryResult result;
        try
        {
            request = BoosterRequests.ReadLottery(packet.Body);
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
                context.Session.PlanAndCommitBooster(character => ReferenceEquals(character, capture.Selected) ?
                    booster.OpenLottery(character, request, out receipt, (clock ?? TimeProvider.System).GetUtcNow()) :
                    InventoryResult.Reject(19), booster.UsePlaces(capture.Selected?.Inventory.Find(new(InventoryListType.Main, request.Slot))?.Core.ItemId ?? 0));
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            result = InventoryResult.Reject(19);
        }
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("lottery_open", new { Connection = context.Id, request?.Mode, request?.Slot,
            SourceItemId = request is null ? null : capture.Selected?.Inventory.Find(new(InventoryListType.Main, request.Slot))?.Core.ItemId,
            result.Accepted, result.Error, Reason = result.Accepted ? "accepted" : "operation_rejected", Reward = receipt,
            Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.USE_LOTTERY_ITEM, LotteryPackets.Opened(result, receipt),
            result.Accepted ? "lottery_opened" : "lottery_rejected", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        short sourceSlot = -1;
        int? itemId = null;
        string? reason = null;
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        BoosterReceipt? receipt = null;
        InventoryResult result;
        bool avatarSource = false;
        try
        {
            sourceSlot = BoosterRequests.ReadSourceSlot(packet.Body);
            var source = capture.Selected?.Inventory.Find(new(InventoryListType.Main, sourceSlot));
            itemId = source?.Core.ItemId;
            var definition = avatars.Definition(source?.Core.ItemId ?? 0);
            avatarSource = definition.Recognized;
            if (definition.Recognized)
            {
                var header = AvatarPackageRequests.ReadHeader(packet.Body);
                var category = definition.Categories.FirstOrDefault(row => row.Category == header.Category && row.Subcategory == header.Subcategory);
                if (!definition.Available || category is null || category.Rows.IsEmpty)
                {
                    reason = "definition_or_category_unavailable";
                    result = InventoryResult.Reject(19);
                }
                else
                {
                    int minimum = definition.SelectionCount == 0 ? category.Rows.Length : 1;
                    int maximum = definition.SelectionCount == 0 ? category.Rows.Length :
                        Math.Min(definition.SelectionCount, category.Rows.Select(row => row.ItemId).Distinct().Count());
                    var request = AvatarPackageRequests.Read(packet.Body, minimum, maximum);
                    result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
                        context.Session.PlanAndCommitBooster(character => ReferenceEquals(character, capture.Selected) ?
                            avatars.Open(character, request, out receipt, (clock ?? TimeProvider.System).GetUtcNow()) :
                            InventoryResult.Reject(19), booster.UsePlaces(itemId ?? 0));
                }
            }
            else if (source is null && packet.Body.Length > 6 && !BoosterRequests.IsItemSelection(packet.Body) &&
                !AvatarPackageRequests.IsSelection(packet.Body))
                return CommandResult.Unhandled;
            else if (source is null)
            {
                reason = "source_missing";
                result = InventoryResult.Reject(19);
            }
            else
            {
                // 尚未闭合的其他附加选择段保持未实现，不截断成普通物品选择。
                if (packet.Body.Length > 6 && !BoosterRequests.IsItemSelection(packet.Body))
                    return CommandResult.Unhandled;
                var request = BoosterRequests.Read(packet.Body);
                result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
                    context.Session.PlanAndCommitBooster(character => ReferenceEquals(character, capture.Selected) ?
                        booster.Open(character, request, out receipt) : InventoryResult.Reject(19), booster.UsePlaces(itemId ?? 0));
            }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            reason = error switch { InvalidDataException => "invalid_request_or_resource", KeyNotFoundException => "resource_missing", _ => "numeric_overflow" };
            result = InventoryResult.Reject(19);
        }
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        int? matchingPools = null, totalPools = null;
        if (!avatarSource && capture.Selected is { } role && itemId is int openedId and > 0)
            (totalPools, matchingPools) = booster.CountMatchingPools(role, openedId);
        log.Write("booster_open", new { Connection = context.Id, Slot = sourceSlot, ItemId = itemId, Avatar = avatarSource,
            Job = capture.Selected?.Job, GrowType = capture.Selected?.GrowType, Level = capture.Selected?.Level,
            MatchingPools = matchingPools, TotalPools = totalPools,
            result.Accepted, result.Error,
            Reason = reason ?? result.RejectReason ?? (result.Accepted ? "accepted" : "operation_rejected"),
            Revision = result.Receipt?.Character.Revision });
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
        int? itemId = null;
        try
        {
            var source = capture.Selected?.Inventory.Find(new(InventoryListType.Main, request.Slot));
            itemId = source?.Core.ItemId;
            bool avatar = avatars.Definition(source?.Core.ItemId ?? 0).Recognized;
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(4) :
                context.Session.PlanAndCommitBooster(character => !ReferenceEquals(character, capture.Selected) ? InventoryResult.Reject(19) :
                    avatar ? avatars.OpenCeraPackage(character, request, out receipt, (clock ?? TimeProvider.System).GetUtcNow()) :
                    booster.OpenCeraPackage(character, request, out receipt), booster.UsePlaces(itemId ?? 0));
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            result = InventoryResult.Reject(19);
        }
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) : SameSelection();
        log.Write("booster_open", new { Connection = context.Id, Slot = request.Slot, ItemId = itemId, CeraPackage = true,
            result.Accepted, result.Error, Reason = result.RejectReason ?? (result.Accepted ? "accepted" : "operation_rejected"),
            Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.OPEN_CERAPACKAGE, CeraPackagePackets.Opened(result, request),
            result.Accepted ? "cera_package_opened" : "cera_package_rejected", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
