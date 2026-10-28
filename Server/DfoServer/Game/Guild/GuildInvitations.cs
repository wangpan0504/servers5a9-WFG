using DfoServer.Game.Session;

namespace DfoServer.Game.Guild;

internal sealed record GuildInvitation(GuildManagementIdentity Inviter, GuildManagementIdentity Target,
    GuildManagementState Guild, long Deadline, int Capacity)
{
    internal ITimer? Timer { get; set; }
    public bool Expired { get; internal set; }
    public void DeliverResult(byte result)
    {
        // 回调已绑定原连接；仅排入有界发送队列，不获取对方账号锁。
        if (Inviter.IsCurrent()) Inviter.Delivery?.Invoke(new(GuildManagementNoticeKind.InvitationResult,
            Invitation: this, Result: result, Name: Target.Name));
    }
}

/// <summary>每个目标连接至多一份邀请；过期后保留墓碑，直到原窗口答复或选角失效。</summary>
internal sealed class GuildInvitations : IDisposable
{
    readonly Dictionary<GameSession, GuildInvitation> pending = [];
    public object SyncRoot { get; } = new();
    public bool HasParticipant(GameSession session)
    {
        lock (SyncRoot) return pending.Values.Any(invite => invite.Target.Session == session ||
            !invite.Expired && invite.Inviter.Session == session);
    }

    public bool TryAdd(GuildInvitation invite, TimeProvider time)
    {
        lock (SyncRoot)
        {
            if (pending.Count >= 2048 || pending.ContainsKey(invite.Target.Session)) return false;
            pending.Add(invite.Target.Session, invite);
            invite.Timer = time.CreateTimer(_ => Expire(invite), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    void Expire(GuildInvitation invite)
    {
        lock (SyncRoot)
        {
            if (!pending.TryGetValue(invite.Target.Session, out var current) || !ReferenceEquals(current, invite)) return;
            invite.Expired = true;
            invite.Timer?.Dispose();
            invite.Timer = null;
        }
        invite.DeliverResult(0);
    }

    public GuildInvitation? Take(GameSession session)
    {
        lock (SyncRoot)
        {
            if (!pending.Remove(session, out var invite)) return null;
            invite.Timer?.Dispose();
            invite.Timer = null;
            return invite;
        }
    }

    public void Remove(GuildInvitation invite)
    {
        lock (SyncRoot)
            if (pending.TryGetValue(invite.Target.Session, out var current) && ReferenceEquals(current, invite)) Take(invite.Target.Session);
    }

    public void Close(GameSession session)
    {
        GuildInvitation? incoming;
        lock (SyncRoot)
        {
            incoming = Take(session);
            foreach (var invite in pending.Values.Where(value => value.Inviter.Session == session))
            {
                invite.Expired = true;
                invite.Timer?.Dispose();
                invite.Timer = null;
            }
        }
        if (incoming is { Expired: false }) incoming.DeliverResult(0);
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            foreach (var invite in pending.Values) invite.Timer?.Dispose();
            pending.Clear();
        }
    }
}
