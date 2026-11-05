using System.Numerics;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Game.Guild;

internal enum GuildGovernanceAction { Settings, RolePermissions, RoleName, MemberGrade, RecommendedChannel, MasterTransfer }
internal sealed record GuildSettingsChange(uint Mask, string Promotion, bool Webpage, uint Tags, byte JoinMode);
internal sealed record GuildGovernanceChange(GuildGovernanceAction Action, byte Grade = 0, uint Permissions = 0,
    string Text = "", GuildSettingsChange? Settings = null, GuildManagementTarget? Target = null, byte Region = 0,
    ChannelEntry? RecommendedChannel = null);
internal sealed record GuildBoardPost(uint PostId, ushort AuthorCharacterId, string AuthorName, byte Grade, string Content, uint CreatedAtUnix);
internal sealed record GuildBoardPage(uint GuildId, byte Page, ushort TotalCount, IReadOnlyList<GuildBoardPost> Posts);
internal sealed record GuildBoardWriteCommit(ushort Error, GuildBoardPost? Post = null, GuildBoardPage? Page = null)
{
    public bool Success => Error == 0;
}

internal interface IGuildGovernanceStore
{
    GuildManagementCommit ChangeGuildGovernance(GuildManagementIdentity actor, GuildGovernanceChange change, long now);
}

internal static class GuildGovernanceRules
{
    public const uint JoinMode = 1u << 1;
    public const uint Promotion = 1u << 5;
    public const uint Tags = 1u << 17;
    public const uint SettingsMask = JoinMode | Promotion | Tags;

    public static ushort Validate(GuildManagementState state, ushort actor, GuildGovernanceChange change)
    {
        bool master = actor == state.MasterId && state.Grade == 1;
        switch (change.Action)
        {
            case GuildGovernanceAction.MasterTransfer:
                if (!master) return 36;
                return change.Target is not { } successor || successor.Id == actor ||
                    !state.Members.Contains(successor) ? (ushort)86 : (ushort)0;
            case GuildGovernanceAction.RecommendedChannel:
                if (!master) return 19;
                return change.RecommendedChannel is not { } channel || !AllowsRecommendedChannel(channel.Type)
                    ? (ushort)7 : (ushort)0;
            case GuildGovernanceAction.RolePermissions:
                if (!master) return 36;
                return change.Grade is < 2 or > 5 || (change.Permissions & ~GuildRoleRules.PermissionMask) != 0 ? (ushort)48 : (ushort)0;
            case GuildGovernanceAction.RoleName:
                if (!master) return 36;
                return change.Grade is < 1 or > 5 || !ValidText(change.Text, 23, 20, false) ? (ushort)159 : (ushort)0;
            case GuildGovernanceAction.Settings:
                if (change.Settings is not { } selected || selected.Mask == 0 || (selected.Mask & ~SettingsMask) != 0) return 7;
                uint permissions = state.Permissions | (master ? Tags : 0);
                if ((permissions & selected.Mask) != selected.Mask) return 7;
                if ((selected.Mask & JoinMode) != 0 && selected.JoinMode > 1) return 7;
                if ((selected.Mask & Tags) != 0 && ((selected.Tags & ~0x7fu) != 0 || BitOperations.PopCount(selected.Tags) > 3)) return 7;
                if ((selected.Mask & Promotion) != 0 &&
                    (!ValidText(selected.Promotion, 255, 80, true) ||
                        state.Promotion.Length != 0 && string.IsNullOrWhiteSpace(selected.Promotion))) return 108;
                return 0;
            case GuildGovernanceAction.MemberGrade:
                if ((state.Permissions & (1u << 11)) == 0) return 83;
                if (change.Target is not { } target || !state.Members.Contains(target)) return 3;
                if (target.Id == actor) return 85;
                if (target.Id == state.MasterId) return 87;
                return change.Grade is < 2 or > 4 ? (ushort)88 : (ushort)0;
            default:
                return 23;
        }
    }

    // 0059A5D0发送空正文，02350BB0的频道类型门禁与普通频道22兼容。
    public static bool AllowsRecommendedChannel(int type) => type is not
        (4 or 5 or 7 or 8 or 9 or 10 or 13 or 14 or 20 or 23 or 24 or 25 or 31 or 32 or
         34 or 35 or 36 or 39 or 40 or 41 or 42 or 45 or 50 or 53 or 59 or 60 or 62 or 63);

    static bool ValidText(string text, int bytes, int utf16, bool multiline)
    {
        if (text.Length > utf16 || !multiline && string.IsNullOrWhiteSpace(text)) return false;
        if (text.Any(value => char.IsControl(value) && !(multiline && value is '\r' or '\n'))) return false;
        try { return CharacterName.Encoding.GetByteCount(text) <= bytes; }
        catch (EncoderFallbackException) { return false; }
    }

    public static bool ValidBoardText(string text) => ValidText(text, 255, 120, true) && !string.IsNullOrWhiteSpace(text);
}

internal sealed class GuildGovernanceService(IGuildGovernanceStore store, GuildManagementService management,
    TimeProvider? clock = null, ChannelCatalog? channels = null)
{
    public const int BoardPageSize = 10;
    public const int MaxBoardPostsPerGuild = 200;
    readonly object boardSync = new();
    readonly Dictionary<uint, List<GuildBoardPost>> boards = [];
    uint nextBoardPostId = 1;

    public GuildManagementIdentity? Capture(GameSession session) => management.Capture(session);
    public GuildManagementTarget? Target(string name) => management.Target(name);
    public GuildManagementCommit Change(GuildManagementIdentity actor, GuildGovernanceChange change)
    {
        if (change.Action == GuildGovernanceAction.RecommendedChannel)
        {
            // 只从当前会话绑定身份及已公布目录取值，不采信客户端正文。
            var definition = channels?.Channels.SingleOrDefault(row => row.Number == actor.Channel.Number);
            change = change with { RecommendedChannel = definition is not null &&
                channels!.ServerIndex == actor.Channel.ServerIndex
                ? new(actor.Channel, definition.Type, definition.Group) : null };
        }
        return store.ChangeGuildGovernance(actor, change, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
    }
    public void Publish(GuildManagementCommit commit) => management.Publish(commit);

    public GuildBoardPage? OpenBoard(GuildManagementIdentity actor, byte page)
    {
        if (!actor.IsCurrent()) return null;
        var state = management.Store.ReadGuildManagement(actor.Owner, actor.CharacterId);
        if (state is null) return null;
        lock (boardSync) return ReadBoardPageLocked(state.Guild.Id, page);
    }

    public GuildBoardWriteCommit WriteBoard(GuildManagementIdentity actor, string content)
    {
        if (!actor.IsCurrent()) return new(3);
        if (!GuildGovernanceRules.ValidBoardText(content)) return new(108);
        var state = management.Store.ReadGuildManagement(actor.Owner, actor.CharacterId);
        if (state is null) return new(34);
        uint now = (uint)Math.Max(0, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
        lock (boardSync)
        {
            if (!boards.TryGetValue(state.Guild.Id, out var list))
                boards[state.Guild.Id] = list = [];
            var post = new GuildBoardPost(nextBoardPostId++, actor.CharacterId, actor.Name, state.Grade, content, now);
            list.Insert(0, post);
            if (list.Count > MaxBoardPostsPerGuild)
                list.RemoveRange(MaxBoardPostsPerGuild, list.Count - MaxBoardPostsPerGuild);
            return new(0, post, ReadBoardPageLocked(state.Guild.Id, 0));
        }
    }

    public GuildBoardWriteCommit DeleteBoard(GuildManagementIdentity actor, uint postId)
    {
        if (!actor.IsCurrent()) return new(3);
        var state = management.Store.ReadGuildManagement(actor.Owner, actor.CharacterId);
        if (state is null) return new(34);
        lock (boardSync)
        {
            if (!boards.TryGetValue(state.Guild.Id, out var list)) return new(7);
            int index = list.FindIndex(post => post.PostId == postId);
            if (index < 0) return new(7);
            var post = list[index];
            bool master = actor.CharacterId == state.MasterId && state.Grade == 1;
            bool canManage = master || state.Grade == 2 || (state.Permissions & (1u << 11)) != 0;
            if (post.AuthorCharacterId != actor.CharacterId && !canManage) return new(36);
            list.RemoveAt(index);
            return new(0, post, ReadBoardPageLocked(state.Guild.Id, 0));
        }
    }

    GuildBoardPage ReadBoardPageLocked(uint guildId, byte page)
    {
        if (!boards.TryGetValue(guildId, out var list) || list.Count == 0)
            return new(guildId, page, 0, []);
        int skip = page * BoardPageSize;
        var slice = skip >= list.Count ? [] : list.Skip(skip).Take(BoardPageSize).ToArray();
        return new(guildId, page, (ushort)Math.Min(ushort.MaxValue, list.Count), slice);
    }
}
