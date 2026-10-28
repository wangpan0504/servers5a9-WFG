using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DimensionEntranceProjection
{
    public static async Task AncientAsync(GameConnectionContext context, Func<bool>? guard = null, bool entranceOnly = true)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase,
            Energy: context.Session.AncientGateEnergy(entranceOnly)));
        if (captured.Energy is not { } energy) return;
        bool Current() => (guard?.Invoke() ?? true) && context.Owner.Execute(() =>
            ReferenceEquals(captured.Selected, context.Session.Selected) && captured.SelectionEpoch == context.Session.SelectionEpoch &&
            captured.Phase == context.Session.Phase);
        await context.Notify(NotiPacketType.ANCIENT_GATE_VALUE, DungeonEnterCountPackets.Ancient(energy), "ancient_gate_energy", Current);
    }
    public static async Task RefreshAsync(GameConnectionContext context, Func<bool>? guard = null, bool entranceOnly = true)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase,
            Info: context.Session.DimensionEntranceInfo(entranceOnly)));
        if (captured.Info is not { } info) return;
        bool Current() => (guard?.Invoke() ?? true) && context.Owner.Execute(() =>
            ReferenceEquals(captured.Selected, context.Session.Selected) && captured.SelectionEpoch == context.Session.SelectionEpoch &&
            captured.Phase == context.Session.Phase);
        await context.Notify(NotiPacketType.DIMENSION_GATE_ENTRANCE_INFO,
            DungeonEnterCountPackets.Dimension(info.BasicUsed, info.ExtraRemaining, info.HiddenUsed), "dimension_entrance_info", Current);
    }
}
