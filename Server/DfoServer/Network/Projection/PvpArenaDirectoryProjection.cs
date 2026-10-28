using System.Threading.Channels;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 仅维护本连接已发出的目录基线；房间和成员状态仍由正式用例持有。
internal sealed class PvpArenaDirectoryProjection : IAsyncDisposable
{
    readonly GameConnectionContext context;
    readonly PvpArenaService arena;
    readonly ServerLog log;
    readonly Channel<bool> pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    readonly CancellationTokenSource stop;
    readonly Task worker;
    readonly Dictionary<ushort, PvpArenaRoomView> known = [];
    long version;
    long epoch = -1;

    public PvpArenaDirectoryProjection(GameConnectionContext context, PvpArenaService arena, ServerLog log)
    {
        this.context = context;
        this.arena = arena;
        this.log = log;
        stop = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        arena.Changed += Request;
        worker = RunAsync();
    }

    public void Request()
    {
        Interlocked.Increment(ref version);
        pending.Writer.TryWrite(true);
    }

    async Task RunAsync()
    {
        try
        {
            await foreach (bool _ in pending.Reader.ReadAllAsync(stop.Token))
            {
                long requested = Volatile.Read(ref version);
                var session = context.Session;
                if (session.ChannelEntry?.Type != 24 || session.Phase != SessionPhase.Town ||
                    session.CaptureSocialActor() is not { } actor) continue;
                long sendEpoch = context.Sender.Epoch;
                if (epoch != actor.Epoch)
                {
                    known.Clear();
                    epoch = actor.Epoch;
                }
                bool Guard() => actor.IsCurrent() && session.Phase == SessionPhase.Town &&
                    context.Sender.Epoch == sendEpoch && requested == Volatile.Read(ref version) &&
                    !stop.IsCancellationRequested;
                var current = arena.Directory(context.Channel).ToDictionary(room => room.Id);
                foreach (var previous in known.Values.Where(room => !current.ContainsKey(room.Id)).ToArray())
                {
                    bool written = false;
                    var removed = previous with { Members = [], Leader = byte.MaxValue };
                    await context.Notify(NotiPacketType.PVP_ROOM_STATE, PvpArenaPackets.State(removed),
                        "pvp_directory_removed", () => written = Guard());
                    if (written) known.Remove(previous.Id);
                }
                foreach (var room in current.Values)
                {
                    if (known.TryGetValue(room.Id, out var previous) && previous.Instance == room.Instance &&
                        previous.Revision == room.Revision) continue;
                    bool written = false;
                    await context.Notify(NotiPacketType.PVP_ROOM_INFO, PvpArenaPackets.Room(room),
                        "pvp_directory_room", () => written = Guard());
                    if (written) known[room.Id] = room;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            if (!stop.IsCancellationRequested)
            {
                log.Write("pvp_directory_failed", new { Connection = context.Id, Message = error.Message });
                context.Disconnect();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        arena.Changed -= Request;
        stop.Cancel();
        pending.Writer.TryComplete();
        await worker;
        stop.Dispose();
    }
}
