using System.Data.Common;
using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// external 的死亡回城为10秒；每个连接仅保留一个任务，断线先取消并等待再释放owner。
internal sealed class DungeonDeathProjection(GameConnectionContext context, TownProjection town, ServerLog log, TimeProvider? time = null) : IAsyncDisposable
{
    public static readonly TimeSpan ReturnDelay = TimeSpan.FromSeconds(10);
    PartyDungeonService? partyDungeons;
    Game.Party.PartyService? parties;
    Func<Task>? revisedBeastPublication;
    internal void ConfigureRevisedBeast(Func<Task> publish) => revisedBeastPublication = publish;
    readonly AsyncLocal<bool> running = new();
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    internal void ConfigurePartyDungeons(PartyDungeonService? dungeons, Game.Party.PartyService? partyService)
    {
        partyDungeons = dungeons;
        parties = partyService;
    }

    public async Task StartAsync()
    {
        if (context.Session.CurrentDungeonDeathReturn() is not { } scheduled) return;
        await StopAsync();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(scheduled, cancellation.Token);
    }

    async Task RunAsync(DungeonDeathReturnReceipt scheduled, CancellationToken stop)
    {
        try
        {
            // 计时经注入的 TimeProvider，回归可按比例缩短等待；生产未注入时使用系统时钟。
            await Task.Delay(context.Session.DungeonDeathDelay, time ?? TimeProvider.System, stop);
            running.Value = true;
            if (context.Session.PartyDungeon is not null)
            {
                using var gates = await DispatchGateScope.EnterAsync(context, stop);
                if (context.Session.PartyDungeon is not { } instance || partyDungeons is null ||
                    context.Session.CurrentDungeonDeathReturn() != scheduled) return;
                using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members, stop);
                if (!ReferenceEquals(context.Session.PartyDungeon, instance) ||
                    context.Session.CurrentDungeonDeathReturn() != scheduled ||
                    instance.Members.Any(m => m.IsCurrent() && ReferenceEquals(m.Actor.Session.PartyDungeon, instance) &&
                        m.Actor.Session.CurrentDungeonDeathReturn() is null)) return;
                foreach (var member in instance.Members.Where(m => m.IsCurrent() &&
                    ReferenceEquals(m.Actor.Session.PartyDungeon, instance)).ToArray())
                {
                    var departure = partyDungeons.Depart(member.Actor.Session, preserveSolo: true);
                    if (departure.Error != 0) return;
                    await PartyDungeonProjection.PublishAsync(departure.DungeonNotices);
                    parties?.Publish(departure.PartyNotices);
                }
                return;
            }
            if (context.Session.CurrentFormalDungeonScene()?.Entry.IsRevisedBeast == true)
            {
                using var gate = await DispatchGateScope.EnterAsync(context, stop);
                if (context.Session.FinishRevisedBeastDeathTimeout(scheduled) is not null)
                {
                    if (revisedBeastPublication is not null) await revisedBeastPublication();
                    return;
                }
            }
            if (context.Session.ReturnFromFormalDungeonOnDeath(scheduled) is not { } receipt) return;
            if (context.LostLand is not null) await context.LostLand.StopAsync();
            await town.ReturnFromFormalDungeonAsync(context, receipt, "formal_death_return_town");
            log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "death_timeout",
                receipt.Character.Id, receipt.Character.Position });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or DbException)
        {
            log.Write("formal_death_return_failed", new { Connection = context.Id, scheduled.InstanceId, Error = error.Message });
        }
    }

    public async Task StopAsync()
    {
        if (running.Value || cancellation is null) return;
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
