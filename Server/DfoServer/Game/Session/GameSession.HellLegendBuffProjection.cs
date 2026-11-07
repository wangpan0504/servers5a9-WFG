using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record HellLegendBuffProjectionReceipt(long UntilSeconds, Func<bool> IsCurrent, Action MarkSent);

internal sealed partial class GameSession
{
    long hellLegendProjectionEpoch = -1;
    long hellLegendProjectedUntil;
    long hellLegendProjectionVersion;

    // 仅记录显示身份；掉率和截止时间仍以已提交的消耗品账本为准。
    public HellLegendBuffProjectionReceipt? PlanHellLegendBuffProjection(bool force = false) => owner.Execute(() =>
    {
        if (Selected is not { } character || consumables is null) return null;
        long epoch = SelectionEpoch;
        if (hellLegendProjectionEpoch != epoch)
        {
            hellLegendProjectionEpoch = epoch;
            hellLegendProjectedUntil = 0;
        }
        long Now() => (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        long Until() => Selected!.Inventory.ConsumableStates
            .Where(row => row.Kind == ConsumableStateKind.Effect && row.Active(Now()) &&
                consumables.HellLegendEpicDropBuffs.ContainsKey(row.ItemId))
            .Select(row => row.UntilMilliseconds / 1000).DefaultIfEmpty().Max();
        long until = Until();
        if (until <= Now() / 1000) until = 0;
        if (until == hellLegendProjectedUntil && (!force || until == 0)) return null;
        long version = ++hellLegendProjectionVersion;
        var phase = Phase;
        bool Current() => owner.Execute(() => owner.IsCurrent && SelectionEpoch == epoch &&
            Selected?.Id == character.Id && Phase == phase && hellLegendProjectionVersion == version &&
            (until == 0 ? Until() <= Now() / 1000 : Until() == until && until > Now() / 1000));
        void MarkSent() => owner.Execute(() =>
        {
            if (Current()) hellLegendProjectedUntil = until;
            return true;
        });
        return new HellLegendBuffProjectionReceipt(until, Current, MarkSent);
    });
}
