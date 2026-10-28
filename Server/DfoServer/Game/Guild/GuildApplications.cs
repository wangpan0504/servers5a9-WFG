using System.Numerics;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Guild;

internal sealed record GuildSearchFilter(byte Mode, string Query = "", uint Tags = 0, byte Page = 0)
{
    public bool IsValid => Mode switch
    {
        0 or 1 => Query.Length <= 14 && !Query.Any(char.IsControl) && Page == 0,
        2 => (Tags & ~0x7fu) == 0 && BitOperations.PopCount(Tags) <= 3,
        _ => false
    };
}

internal sealed record GuildDirectoryEntry(uint Id, string Name, string MasterName, byte Level,
    ushort Members, byte JoinMode, uint Tags, string Promotion)
{
    public uint Mark { get; init; }
    public byte Tier { get; init; }
    public uint CreatedAtUnix { get; init; }
    public uint MemberLimit { get; init; } = 300;
    public uint AttendanceCount { get; init; }
    public uint Ranking { get; init; }
}
internal sealed record GuildSearchPage(uint Total, byte Page, GuildDirectoryEntry[] Guilds);
internal sealed record GuildAccountEntry(string Name, GuildDirectoryEntry? Guild, bool Pending, byte Region);
internal sealed record GuildAccountDirectory(GuildDirectoryEntry[] Guilds, GuildAccountEntry[] Members,
    GuildAccountEntry[] Applications);
internal sealed record GuildApplicationRow(ushort CharacterId, string Account, string Name, byte Job,
    byte GrowType, byte Level, string Message, uint CreatedAtUnix, byte Region);
internal sealed record GuildApplicationList(ushort Error, GuildApplicationRow[] Rows);
internal sealed record GuildApplicationCommit(ushort Error, GuildDirectoryEntry? Guild = null,
    bool Joined = false, long Revision = 0, GuildManagementTarget[]? Members = null)
{
    public bool Success => Error == 0;
    public GuildManagementCommit Management => new(Error, Guild?.Id ?? 0, Revision,
        GuildName: Guild?.Name ?? "", Members: Members);
}

internal interface IGuildApplicationsStore
{
    GuildSearchPage SearchGuilds(GuildSearchFilter filter);
    GuildDirectoryEntry? FindApplicationGuild(string name);
    GuildAccountDirectory ReadAccountGuildDirectory(AccountOwners.Lease owner, ushort selectedId, byte region);
    GuildApplicationList ReadGuildApplications(GuildManagementIdentity actor);
    GuildApplicationCommit ApplyToGuild(GuildManagementIdentity actor, string name, string message,
        byte region, Func<long> now, int memberLimit);
    GuildApplicationCommit CancelGuildApplication(GuildManagementIdentity actor, uint guildId);
    GuildApplicationCommit DecideGuildApplication(GuildManagementIdentity actor, ushort targetId, byte region,
        bool approve, Func<long> now, int memberLimit);
}

internal sealed class GuildApplications(IGuildApplicationsStore store, GuildManagementService management,
    TimeProvider? clock = null, GuildRankingService? ranking = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;
    public GuildSearchPage Search(GuildSearchFilter filter) => filter.IsValid
        ? store.SearchGuilds(filter) : throw new InvalidDataException("Invalid guild search");
    public GuildDirectoryEntry? Find(string name)
    {
        var found = store.FindApplicationGuild(name);
        return found is null ? null : found with { Ranking = ranking?.OfficialRank(found.Id) ?? 0 };
    }
    public GuildAccountDirectory Account(GuildManagementIdentity actor) =>
        store.ReadAccountGuildDirectory(actor.Owner, actor.CharacterId, actor.Channel.ServerIndex);
    public GuildApplicationList List(GuildManagementIdentity actor) => store.ReadGuildApplications(actor);
    public GuildApplicationCommit Apply(GuildManagementIdentity actor, string name, string message) =>
        ValidMessage(message) ? store.ApplyToGuild(actor, name, message.Trim(), actor.Channel.ServerIndex,
            () => time.GetUtcNow().ToUnixTimeSeconds(), management.MemberLimit) : new(23);
    public GuildApplicationCommit Cancel(GuildManagementIdentity actor, uint guildId) =>
        store.CancelGuildApplication(actor, guildId);
    public GuildApplicationCommit Decide(GuildManagementIdentity actor, ushort target, byte region, bool approve) =>
        store.DecideGuildApplication(actor, target, region, approve,
            () => time.GetUtcNow().ToUnixTimeSeconds(), management.MemberLimit);
    public void Publish(GuildApplicationCommit commit)
    {
        if (!commit.Success || commit.Members is null) return;
        if (commit.Joined) management.Publish(commit.Management);
        else foreach (var target in commit.Members) management.RefreshAccount(target);
    }
    public static bool ValidMessage(string message)
    {
        try { return message.Length <= 80 && !message.Contains('\0') && CharacterName.Encoding.GetByteCount(message) <= 160; }
        catch (System.Text.EncoderFallbackException) { return false; }
    }
}
