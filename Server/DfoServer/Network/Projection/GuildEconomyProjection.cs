using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class GuildEconomyProjection(GuildEconomyService service, Action<Exception>? onError = null,
    GuildNpcService? npcs = null) : IDisposable
{
    readonly object gate = new();
    readonly Dictionary<GameSession, ITimer> timers = new();
    bool disposed;
    public void Post(GameConnectionContext context, bool force = false)
    {
        if (context.Session.PlanGuildBuffProjection(service, force, npcs) is not { } receipt) return;
        if (receipt.Removed.Length > 0)
            context.Sender.Post(0, (ushort)NotiPacketType.CHARACTER_DEL_BUFF,
                GuildEconomyPackets.RemovedBuffs(receipt.Removed), "guild_benefits_removed", receipt.IsCurrent);
        if (receipt.Added.Length > 0)
            context.Sender.Post(0, (ushort)NotiPacketType.CHARACTER_ADD_BUFF,
                GuildEconomyPackets.AddedBuffs(receipt.Added, service.Now), "guild_benefits_added", receipt.IsCurrent);
        lock (gate)
        {
            if (timers.Remove(context.Session, out var previous)) previous.Dispose();
            if (disposed || receipt.Deadline is not { } deadline || !receipt.IsCurrent()) return;
            var due = TimeSpan.FromSeconds(Math.Max(1, deadline - service.Now));
            ITimer? scheduled = null;
            scheduled = service.Clock.CreateTimer(_ =>
            {
                lock (gate)
                {
                    if (disposed || !timers.TryGetValue(context.Session, out var active) || !ReferenceEquals(active, scheduled)) return;
                    timers.Remove(context.Session); active.Dispose();
                }
                try
                {
                    if (context.Session.CaptureGuildManagement(requireTown: false) is not null) Post(context);
                }
                catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
                { onError?.Invoke(error); }
            }, null, due, Timeout.InfiniteTimeSpan);
            timers[context.Session] = scheduled;
        }
    }
    public void Close(GameSession session)
    {
        lock (gate) if (timers.Remove(session, out var timer)) timer.Dispose();
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true; foreach (var timer in timers.Values) timer.Dispose(); timers.Clear();
        }
    }
}
