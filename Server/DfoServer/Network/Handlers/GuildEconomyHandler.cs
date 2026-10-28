using System.Data.Common;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildEconomyHandler(GuildEconomyService service, GuildEconomyProjection projection,
    InventoryProjection inventory, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.BUY_GUILD_CONTENTS, BuyAsync, nameof(GuildEconomyHandler));
        commands.Register(CmdPacketType.REQ_GUILD_MILEAGE_HISTORY, HistoryAsync, nameof(GuildEconomyHandler));
    }
    async Task<CommandResult> BuyAsync(GameConnectionContext context, Packet packet)
    {
        var actor = context.Session.CaptureGuildManagement();
        var result = new GuildEconomyCommit(14);
        if (actor is not null && context.Activity?.BlocksTownActions != true &&
            GuildEconomyRequests.TryPurchase(packet.Body, out uint content))
            result = service.Buy(actor, content, context.Session.GuildRequestIdentity(packet.RequestSerial));
        log.Write("guild_content_purchase", new { Connection = context.Id, result.Error, result.Replayed,
            Guild = result.State?.GuildId, Balance = result.State?.PublicFunds });
        await context.Reply(CmdPacketType.BUY_GUILD_CONTENTS, result.Success ? [1] : GuildManagementPackets.Error(result.Error),
            "guild_content_purchase", actor?.IsCurrent);
        service.Publish(result);
        if (result.Success) projection.Post(context);
        return CommandResult.Handled;
    }
    async Task<CommandResult> HistoryAsync(GameConnectionContext context, Packet packet)
    {
        var actor = context.Session.CaptureGuildManagement();
        bool valid = packet.Body.Length == 0 && actor is not null && service.Read(actor) is not null;
        byte[] body = valid ? GuildEconomyPackets.History(service.Store.ReadGuildMileageHistory(actor!.Owner, actor.CharacterId))
            : GuildManagementPackets.Error(34);
        await context.Reply(CmdPacketType.REQ_GUILD_MILEAGE_HISTORY, body, "guild_mileage_history", actor?.IsCurrent);
        return CommandResult.Handled;
    }
    public async Task<CommandResult> UseGiftAsync(GameConnectionContext context, StackableActionUseRequest request, ulong requestSerial)
    {
        bool valid = request.Action == GameWorld.Guilds.GuildMileageGiftDefinition.Action && request.Reserved == 0 &&
            request.FirstArgument == 0 && request.SecondArgument == 0 && request.List == (byte)InventoryListType.Main && request.Slot <= short.MaxValue;
        Game.Session.GuildGiftUseResult use;
        try
        {
            use = context.Session.UseGuildMileageGift(new(InventoryListType.Main, unchecked((short)request.Slot)), service,
                context.Session.GuildRequestIdentity(requestSerial), !valid || context.Activity?.BlocksTownActions == true);
        }
        catch (DbException error)
        {
            log.Write("guild_mileage_gift_commit_failed", new { Connection = context.Id, Error = error.Message });
            return CommandResult.CloseConnection;
        }
        if (use.CloseConnection) return CommandResult.CloseConnection;
        if (use.Commit.Replayed) return CommandResult.Handled;
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            use.Commit.Success ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request, use.Commit.Error),
            "guild_mileage_gift", use.IsCurrent);
        if (use.Inventory is { } result)
        {
            // 515原生消费者自行扣一件，源槽不重复播放“获得剩余量”。
            await inventory.RefreshAsync(context, result, new HashSet<InventorySlot> { new(InventoryListType.Main, (short)request.Slot) },
                refreshEquipment: false);
            service.Publish(use.Commit);
        }
        log.Write("guild_mileage_gift", new { Connection = context.Id, use.Commit.Error, use.Commit.Replayed,
            Guild = use.Commit.State?.GuildId, Balance = use.Commit.State?.PublicFunds });
        return CommandResult.Handled;
    }
}
