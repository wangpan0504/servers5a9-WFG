using DfoServer.Game.Inventory.Models;
using DfoServer.Game.PcRoom;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class PcRoomHandler(PcRoomRewardService rewards, InventoryProjection inventory, ServerLog log,
    TimeProvider? time = null) : IGameCommandModule
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.TENCENT_PC_ROOM_LOGIN_REWARD, (context, packet) => ClaimAsync(context, packet, false), nameof(PcRoomHandler));
        commands.Register(CmdPacketType.TENCENT_PCROOM_RENTAL_ITEM, (context, packet) => ClaimAsync(context, packet, true), nameof(PcRoomHandler));
        commands.Register(CmdPacketType.PC_ROOM_RENT_ITEM, WeaponsAsync, nameof(PcRoomHandler));
    }

    async Task<CommandResult> ClaimAsync(GameConnectionContext context, Packet packet, bool rental)
    {
        PcRoomRequests.Empty(packet.Body);
        var before = context.Session.ReadPcRoomView();
        var result = context.BlocksTownActions ? InventoryResult.Reject(1) : context.Session.ClaimPcRoom(rental, rewards);
        ushort error = result.Accepted ? (ushort)0 : result.Error == 17 ? (ushort)3 : result.Error;
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            before is not null && context.Session.CanPublishPcRoom(before);
        log.Write(result.Accepted ? "pcroom_reward" : "pcroom_reward_rejected",
            new { Connection = context.Id, Rental = rental, result.Accepted, Error = error });
        // 原生ACK回调立即重绘窗口：实际资产与领取位必须先到达。
        await inventory.RefreshAsync(context, result, refreshEquipment: false);
        if (context.PcRoom is { } panel) await panel.RefreshAsync(force: true);
        await context.Reply(rental ? CmdPacketType.TENCENT_PCROOM_RENTAL_ITEM : CmdPacketType.TENCENT_PC_ROOM_LOGIN_REWARD,
            PcRoomPackets.Claim(error), "pcroom_reward_result", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> WeaponsAsync(GameConnectionContext context, Packet packet)
    {
        var indexes = PcRoomRequests.Weapons(packet.Body);
        var before = context.Session.ReadPcRoomView();
        var result = context.BlocksTownActions ? InventoryResult.Reject(1) : context.Session.RentPcRoomWeapons(indexes, rewards);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            before is not null && context.Session.CanPublishPcRoom(before);
        log.Write(result.Accepted ? "pcroom_weapon" : "pcroom_weapon_rejected",
            new { Connection = context.Id, Count = indexes.Length, result.Accepted, result.Error, Reason = result.RejectReason });
        await inventory.RefreshAsync(context, result, refreshEquipment: false);
        if (result.Receipt is { } committed)
            await context.Notify(NotiPacketType.EQUIPMENT_RENTAL_LIST,
                RentalPackets.State(committed.Character.Inventory, clock.GetUtcNow()), "pcroom_weapon_rental_state", Guard);
        await context.Reply(CmdPacketType.PC_ROOM_RENT_ITEM, PcRoomPackets.Weapons(result.Accepted ? (ushort)0 : result.Error),
            "pcroom_weapon_result", Guard);
        return CommandResult.Handled;
    }
}
