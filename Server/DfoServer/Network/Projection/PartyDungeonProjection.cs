using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class PartyDungeonProjection
{
    sealed class Gates(List<SemaphoreSlim> held) : IDisposable
    {
        public void Dispose()
        {
            for (int i = held.Count - 1; i >= 0; i--) held[i].Release();
        }
    }

    public static async Task<IDisposable> AcquirePeersAsync(GameConnectionContext caller, PartyMember[] members, CancellationToken? stop = null)
    {
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var member in members.OrderBy(m => m.Actor.Character.Id))
            {
                if (ReferenceEquals(member.Actor.Session, caller.Session)) continue;
                var gate = member.Actor.Session.CommandGate;
                await gate.WaitAsync(stop ?? caller.Stop);
                held.Add(gate);
            }
            return new Gates(held);
        }
        catch
        {
            new Gates(held).Dispose();
            throw;
        }
    }

    public static async Task PublishAsync(IEnumerable<PartyDungeonNotice> notices)
    {
        var batch = notices.ToArray();
        // 同一连接按事实顺序发送，尤其退场时须先迁移主机再释放加载；不同连接仍并行。
        await Task.WhenAll(batch.GroupBy(n => n.Recipient.Actor.Session).Select(async notices =>
        {
            foreach (var notice in notices) await PublishOneAsync(notice);
        }));
        // 全员31发布完成后再计算共享资格，避免先完成者把仍在发送中的队友误判为不能再挑战。
        await Task.WhenAll(batch.Where(n => n.Kind is PartyDungeonNoticeKind.Death or PartyDungeonNoticeKind.Loaded or PartyDungeonNoticeKind.Move &&
                n.Scene?.Entry.IsCosmo == true && n.Recipient.Actor.Session.FormalDungeonResultFocus() is not null)
            .Select(n => PublishOneAsync(n with { Kind = PartyDungeonNoticeKind.RetryEligibility })));
    }

    // 连接清理从命令门禁外进入，先串行队伍，再等待剩余连接；不得拿实例锁等待账号锁。
    public static async Task DetachAsync(GameConnectionContext context)
    {
        context.Session.PrepareAntonRejoinDisconnect();
        if (context.Session.PartyDungeon is not { } instance) return;
        var partyGate = context.Session.CurrentParty?.Invoke()?.Commands;
        if (partyGate is not null) await partyGate.WaitAsync();
        try
        {
            using var peers = await AcquirePeersAsync(context, instance.Members, CancellationToken.None);
            await PublishAsync(instance.Detach(context.Session));
        }
        finally { partyGate?.Release(); }
    }

    public static Task PublishEventAsync(GameConnectionContext context, PartyDungeonNoticeKind kind, ushort peer, byte state, byte option = 0)
    {
        var instance = context.Session.PartyDungeon;
        if (instance is null) return Task.CompletedTask;
        return PublishAsync(instance.Execute(current => current.Where(m => !ReferenceEquals(m.Actor.Session, context.Session))
            .Select(m => new PartyDungeonNotice(kind, m, current, m.Actor.Session.CurrentFormalDungeonScene(),
                Peer: peer, State: state, Option: option)).ToArray()));
    }

    static async Task PublishOneAsync(PartyDungeonNotice notice)
    {
        if (!notice.Recipient.IsCurrent() || notice.Recipient.Actor.Session.PartyDungeonNoticeArrived is not { } deliver) return;
        try { await deliver(notice); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            notice.Recipient.Actor.Session.Disconnect();
        }
    }
}
