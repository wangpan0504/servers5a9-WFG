using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record HellLegendBuffProjectionReceipt(long UntilSeconds, Func<bool> IsCurrent, Action MarkSent)
{
    public int BuffId { get; init; } = HellLegendEpicDropBuffDefinition.CanonicalBuffId;
    public int RemovedBuffId { get; init; }
}

internal sealed partial class GameSession
{
    long hellLegendProjectionEpoch = -1;
    long hellLegendProjectedUntil;
    int hellLegendProjectedBuff;
    long hellLegendProjectionVersion;

    // 仅记录显示身份；效果及截止仍以已提交的消耗品账本与通用资源表为准。
    public HellLegendBuffProjectionReceipt? PlanHellLegendBuffProjection(bool force = false) => owner.Execute(() =>
    {
        if (Selected is not { } character || consumables is null) return null;
        long epoch = SelectionEpoch;
        if (hellLegendProjectionEpoch != epoch)
        {
            hellLegendProjectionEpoch = epoch;
            hellLegendProjectedUntil = 0;
            hellLegendProjectedBuff = 0;
        }
        long Now() => (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        (int Buff, long Until) Active()
        {
            long now = Now();
            return Selected!.Inventory.ConsumableStates.Where(row => row.Kind == ConsumableStateKind.Effect && row.Active(now))
                .Select(row => (row.UntilMilliseconds, Definition: consumables.HellLegendEpicDropBuffs.GetValueOrDefault(row.ItemId)))
                .Where(row => row.Definition is not null && row.UntilMilliseconds / 1000 > now / 1000)
                .OrderByDescending(row => row.UntilMilliseconds).Select(row => (row.Definition!.BuffId, row.UntilMilliseconds / 1000))
                .FirstOrDefault();
        }
        var desired = Active();
        if (desired == (hellLegendProjectedBuff, hellLegendProjectedUntil) && (!force || desired.Buff == 0)) return null;
        long version = ++hellLegendProjectionVersion;
        var phase = Phase;
        bool Current() => owner.Execute(() => owner.IsCurrent && SelectionEpoch == epoch &&
            Selected?.Id == character.Id && Phase == phase && hellLegendProjectionVersion == version && Active() == desired);
        void MarkSent() => owner.Execute(() =>
        {
            if (Current()) { hellLegendProjectedBuff = desired.Buff; hellLegendProjectedUntil = desired.Until; }
            return true;
        });
        return new HellLegendBuffProjectionReceipt(desired.Until, Current, MarkSent)
        { BuffId = desired.Buff, RemovedBuffId = hellLegendProjectedBuff != desired.Buff ? hellLegendProjectedBuff : 0 };
    });
}
