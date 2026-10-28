using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Game.Guild;

internal static class GuildRoleRules
{
    public const uint PermissionMask = (1u << 19) - 1;
    public const uint NpcManagementPermission = 1u << 18;
    public static byte NormalizeGrade(ushort id, ushort master, byte grade) => id == master ? (byte)1 :
        grade is >= 2 and <= 5 ? grade : (byte)4;
    public static uint DefaultPermissions(byte grade) => grade switch { 1 => PermissionMask, 2 => 0x4410, _ => 0 };
    public static string DefaultName(byte grade) => grade switch
        { 1 => "会长", 2 => "副会长", 3 => "优秀会员", 4 => "普通会员", _ => "" };
    public static bool CanKick(byte actor, byte target, uint permissions) =>
        actor is >= 1 and <= 4 && target is >= 2 and <= 4 && actor < target && (permissions & (1u << 10)) != 0;
    public static long DepartureDeadline(long now)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(8));
        var next = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
        return (local < next ? next : next.AddDays(1)).ToUnixTimeSeconds();
    }
}

internal sealed record GuildRole(byte Grade, uint Permissions, string Name);
internal sealed record GuildManagementTarget(ushort Id, string Account, string Name);
internal sealed record GuildManagementIdentity(GameSession Session, AccountOwners.Lease Owner, ushort CharacterId,
    string Name, long Epoch, ChannelIdentity Channel, Func<bool> IsCurrent)
{
    public Action<GuildManagementNotice>? Delivery { get; init; }
}
internal sealed record GuildManagementState(GuildMembership Guild, ushort MasterId, byte Grade, uint Permissions,
    long Revision, string Promotion, uint Tags, byte JoinMode, GuildRole[] Roles, GuildManagementTarget[] Members)
{
    public byte Webpage { get; init; }
    public ChannelIdentity? RecommendedChannel { get; init; }
}
internal enum GuildManagementAction { Leave, Disband, Notice, Memo }
internal sealed record GuildManagementChange(GuildManagementAction Action, GuildManagementTarget? Target, string Text = "");
internal sealed record GuildManagementCommit(ushort Error, uint GuildId = 0, long Revision = 0,
    GuildManagementTarget? Removed = null, string GuildName = "", bool Voluntary = false,
    GuildManagementTarget[]? Members = null)
{
    public bool Success => Error == 0;
    public bool NpcsChanged { get; init; }
}

internal interface IGuildManagementStore
{
    GuildManagementState? ReadGuildManagement(AccountOwners.Lease owner, ushort characterId);
    GuildManagementTarget? FindGuildManagementTarget(string name);
    GuildManagementCommit ChangeGuildManagement(GuildManagementIdentity actor, GuildManagementChange change, long now);
    GuildManagementCommit JoinGuildInvitation(GuildManagementIdentity inviter, GuildManagementIdentity target,
        uint expectedGuild, long deadline, int capacity, Func<long> now);
}

internal enum GuildManagementNoticeKind { Refresh, Invite, InvitationResult, AccountRefresh }
internal sealed record GuildManagementNotice(GuildManagementNoticeKind Kind, uint GuildId = 0,
    GuildInvitation? Invitation = null, byte Result = 0, string Name = "")
{
    public bool NpcsChanged { get; init; }
}

internal sealed class GuildManagementService(IGuildManagementStore store, OnlineSessionDirectory online,
    GuildInvitations invitations, GuildPermitRegistry permits, int memberLimit = 300, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;
    public IGuildManagementStore Store => store;
    public GuildInvitations Invitations => invitations;
    public int MemberLimit => memberLimit;
    int Capacity(GuildManagementIdentity actor) => store is IGuildEconomyStore economy
        ? economy.ReadGuildMemberLimit(actor.Owner, actor.CharacterId) : memberLimit;
    long Now => time.GetUtcNow().ToUnixTimeSeconds();
    long NowMillis => time.GetUtcNow().ToUnixTimeMilliseconds();

    public GuildManagementIdentity? Capture(GameSession session) => session.CaptureGuildManagement();

    public ushort Offer(GuildManagementIdentity actor, byte region, string name)
    {
        if (!actor.IsCurrent()) return 3;
        var state = store.ReadGuildManagement(actor.Owner, actor.CharacterId);
        if (state is null) return 34;
        if ((state.Permissions & 2) == 0) return 36;
        var target = store.FindGuildManagementTarget(name);
        if (target is null || target.Account == actor.Owner.Account) return 3;
        var peer = online.Find(target.Account)?.CaptureGuildManagement();
        if (peer is null || peer.CharacterId != target.Id || peer.Name != name || !peer.IsCurrent() ||
            peer.Channel != actor.Channel || region != peer.Channel.ServerIndex) return 3;
        if (store.ReadGuildManagement(peer.Owner, peer.CharacterId) is not null) return 39;
        int limit = Capacity(actor);
        if (state.Members.Length >= limit) return 38;
        var invite = new GuildInvitation(actor, peer, state, NowMillis + 15_000, limit);
        lock (invitations.SyncRoot)
        {
            if (permits.HasParticipant(actor.CharacterId, Now) || permits.HasParticipant(peer.CharacterId, Now)) return 103;
            if (!invitations.TryAdd(invite, time)) return 103;
        }
        if (!peer.Session.TryDeliverGuildManagement(peer.CharacterId,
                new(GuildManagementNoticeKind.Invite, state.Guild.Id, invite)))
        {
            invitations.Remove(invite);
            return 3;
        }
        return 0;
    }

    public GuildManagementCommit Answer(GuildManagementIdentity target, bool accept)
    {
        var invite = invitations.Take(target.Session);
        if (invite is null) return new(103);
        if (invite.Target.CharacterId != target.CharacterId || invite.Target.Epoch != target.Epoch ||
            invite.Expired || NowMillis >= invite.Deadline || !invite.Inviter.IsCurrent() || !target.IsCurrent())
        {
            if (!invite.Expired) invite.DeliverResult(0);
            return new(103);
        }
        if (!accept) { invite.DeliverResult(0); return new(0); }
        var result = store.JoinGuildInvitation(invite.Inviter, target, invite.Guild.Guild.Id, invite.Deadline,
            invite.Capacity, () => NowMillis);
        invite.DeliverResult(result.Success ? (byte)1 : (byte)0);
        return result;
    }

    public GuildManagementCommit Change(GuildManagementIdentity actor, GuildManagementChange change) =>
        store.ChangeGuildManagement(actor, change, Now);

    public GuildManagementTarget? Target(string name) => store.FindGuildManagementTarget(name);

    public void RefreshAccount(GuildManagementTarget target) =>
        online.Find(target.Account)?.TryDeliverGuildManagement(target.Id, new(GuildManagementNoticeKind.AccountRefresh));

    public void Publish(GuildManagementCommit commit)
    {
        if (!commit.Success) return;
        foreach (var member in commit.Members ?? [])
            online.Find(member.Account)?.TryDeliverGuildManagement(member.Id,
                new(GuildManagementNoticeKind.Refresh, commit.GuildId) { NpcsChanged = commit.NpcsChanged });
        if (commit.Removed is { } removed)
        {
            if (online.Find(removed.Account) is not { } session) return;
            invitations.Close(session);
            session.TryDeliverGuildManagement(removed.Id, new(GuildManagementNoticeKind.Refresh, commit.GuildId));
        }
    }
}
