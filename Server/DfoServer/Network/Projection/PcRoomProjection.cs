using DfoServer.Game.PcRoom;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 这里只缓存已发面板；领取事实始终来自与物品同事务的账号/角色日账本。
internal sealed class PcRoomProjection(GameConnectionContext context, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    long epoch = -1;
    int day = -1;
    bool active;
    public async Task RefreshAsync(bool force = false)
    {
        var now = clock.GetUtcNow();
        var selected = context.Owner.Execute(() => (context.Session.Selected, context.Session.Ready,
            context.Session.SelectionEpoch, context.Session.PremiumPcRoom));
        if (selected.Selected is null || !force && !selected.Ready) return;
        if (!force && epoch == selected.SelectionEpoch && day == PcRoomDay.At(now) && active == selected.PremiumPcRoom) return;
        if (context.Session.ReadPcRoomView() is not { } view) return;
        if (!view.Active && (epoch != view.Epoch || !active))
        {
            epoch = view.Epoch;
            day = view.Day;
            active = false;
            return;
        }
        bool Guard() => context.Session.CanPublishPcRoom(view);
        await context.Notify(NotiPacketType.PCROOM_LOGIN_REWARD,
            PcRoomPackets.Login(!view.Active || view.Claims.LoginClaimed), "pcroom_login_state", Guard);
        await context.Notify(NotiPacketType.PCROOM_RENTAL_ITEM,
            PcRoomPackets.Rental(!view.Active || view.Claims.RentalClaimed), "pcroom_rental_state", Guard);
        if (context.Session.FatigueAccelerationState() is { } acceleration)
            await context.Notify(NotiPacketType.USER_FATIGUE_ACCELERATION_STATE,
                FatigueAccelerationPackets.State(acceleration.Active), "pcroom_fatigue_acceleration_state",
                () => Guard() && context.Session.CanPublishFatigueAcceleration(acceleration, requireCurrentState: true));
        if (!Guard()) return;
        epoch = view.Epoch;
        day = view.Day;
        active = view.Active;
    }
}
