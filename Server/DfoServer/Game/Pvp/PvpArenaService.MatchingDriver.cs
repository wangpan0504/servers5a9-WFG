namespace DfoServer.Game.Pvp;

internal sealed partial class PvpArenaService
{
    // 维护驱动不持任一连接门禁等待另一连接；忙连接留在原顺序，下次再配。
    // 返回时仍持新房间Commands，发布双方初始化后由调用方释放。
    public async Task<PvpMatchingRoomEntry?> PrepareNextOrdinaryMatchingAsync(DateTimeOffset now,
        CancellationToken stop = default)
    {
        stop.ThrowIfCancellationRequested();
        SweepMatching(now);
        PvpMatchingTicket[] queued;
        lock (gate)
        {
            PruneMatching();
            queued = matching.SnapshotValues().Where(row => row.Load is null).OrderBy(row => row.Order).ToArray();
        }
        foreach (var first in queued)
        {
            if (!await first.Actor.Session.CommandGate.WaitAsync(0, stop)) continue;
            try
            {
                foreach (var second in queued)
                {
                    if (second.Order <= first.Order || first.Actor.Owner.Account == second.Actor.Owner.Account ||
                        first.Identity.Channel != second.Identity.Channel || !first.Preferences.Compatible(second.Preferences) ||
                        !await second.Actor.Session.CommandGate.WaitAsync(0, stop)) continue;
                    try
                    {
                        var load = PairOrdinary(first, second, now, TimeSpan.FromSeconds(45));
                        if (load is null) continue;
                        bool entered = false;
                        try
                        {
                            var entry = PrepareOrdinaryMatching(load, checked((ushort)Random.Shared.Next(catalog.MapOrder.Length)), now);
                            entered = entry is not null;
                            if (entered) return entry;
                        }
                        finally { if (!entered) CancelMatching(first.Actor.Session, first); }
                    }
                    finally { second.Actor.Session.CommandGate.Release(); }
                }
            }
            finally { first.Actor.Session.CommandGate.Release(); }
        }
        return null;
    }
}
