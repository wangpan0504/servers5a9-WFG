using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class GuildCargoProjection(GuildCargoService service, InventoryProjection inventory)
{
    public async Task QueryAsync(GameConnectionContext context, bool history)
    {
        var opcode = history ? CmdPacketType.GUILD_CARGO_HISTORY : CmdPacketType.GUILD_CARGO;
        var read = context.Session.ReadGuildCargo(service);
        if (read.Receipt is not { } receipt)
        {
            await context.Reply(opcode, GuildCargoPackets.Failure(read.Error), "guild_cargo_rejected");
            return;
        }
        bool Current() => context.Session.CanPublishGuildCargo(receipt);
        await context.Reply(opcode, history ? GuildCargoPackets.History(receipt.View) : GuildCargoPackets.Snapshot(receipt.View),
            history ? "guild_cargo_history" : "guild_cargo_snapshot", Current);
        // 只有261实际发布后才获得转移资格，查历史不能代替加载仓库。
        if (!history) context.Session.MarkGuildCargoLoaded(receipt);
    }

    public async Task TransferAsync(GameConnectionContext context, GuildCargoRequest request, GuildCargoTransferReceipt result)
    {
        var opcode = request.Move ? CmdPacketType.GUILD_CARGO_MOVE_ITEM :
            request.Deposit ? CmdPacketType.GUILD_CARGO_PUSH_ITEM : CmdPacketType.GUILD_CARGO_POP_ITEM;
        Func<bool>? current = result.Cargo?.IsCurrent;
        await context.Reply(opcode, GuildCargoPackets.Transfer(request, result.Commit), "guild_cargo_transfer", current);
        if (result.Inventory is { } committed) await inventory.RefreshAsync(context, committed);
        else if (!request.Move && result.Cargo is { } refreshed && result.Commit.Character is { } character)
        {
            // 重放只清除等待，再恢复权威数量；不能再次发送会搬动物品对象的成功ACK。
            var slots = character.Inventory.Items.Where(item => item.Position.List == request.List).Select(item => item.Position)
                .Append(new(request.List, request.Deposit ? request.InventorySlot : result.Commit.Destination)).Distinct().ToArray();
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta(character.Inventory, request.List, slots), "guild_cargo_inventory_resync", refreshed.IsCurrent);
        }
        if (result.Cargo is not { } cargo) return;
        bool CargoCurrent() => context.Session.CanPublishGuildCargo(cargo);
        await context.Reply(CmdPacketType.GUILD_CARGO, GuildCargoPackets.Snapshot(cargo.View), "guild_cargo_committed", CargoCurrent);
        context.Session.MarkGuildCargoLoaded(cargo);
    }

    public void Post(GameConnectionContext context, GuildCargoNotice notice)
    {
        var read = context.Session.ReadGuildCargo(service, initialize: false, expectedGuild: notice.GuildId, requireLoaded: true);
        if (read.Receipt is not { } receipt) return;
        bool Current() => context.Session.CanPublishGuildCargo(receipt);
        context.Sender.Post(0, (ushort)NotiPacketType.GUILD_CARGO,
            GuildCargoPackets.Snapshot(receipt.View, success: false), "guild_cargo_peer_committed", Current);
        context.Session.MarkGuildCargoLoaded(receipt);
    }
}
