using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Game.TagEvildom;

// 编队保存角色稳定身份，不保存客户端选角列表索引；普通多人 Party 不参与此状态。
internal readonly record struct TagEvildomTeam(ushort First, ushort Second)
{
    public bool Empty => First == 0 && Second == 0;
    public bool Contains(ushort character) => character != 0 && (First == character || Second == character);
}

internal readonly record struct TagEvildomMemberRequest(byte Server, int RosterIndex, uint Auxiliary);

internal sealed record TagEvildomProjection(TagEvildomState State, byte Server,
    CharacterRosterSnapshot Roster, long Generation, long Epoch, ushort CharacterId);
internal sealed record TagEvildomCharacters(TagEvildomProjection Projection, Character Primary, Character Standby, long Version);

internal sealed record TagEvildomConfiguration(long Revision, int SelectedTeam, ImmutableArray<TagEvildomTeam> Teams)
{
    public static readonly TagEvildomConfiguration Empty = new(0, 0, [default, default, default]);

    public void Validate()
    {
        if (Revision < 0 || SelectedTeam is < 0 or >= 3 || Teams.IsDefault || Teams.Length != 3)
            throw new InvalidDataException("再战魔界编队形状无效");
        var members = new HashSet<ushort>();
        foreach (var team in Teams)
        {
            if (team.Empty) continue;
            if (team.First == 0 || team.Second == 0 || !members.Add(team.First) || !members.Add(team.Second))
                throw new InvalidDataException("再战魔界要求完整双角色编队且角色不可重复");
        }
    }

    public bool SameAssignments(TagEvildomConfiguration other) => SelectedTeam == other.SelectedTeam && Teams.SequenceEqual(other.Teams);

    // 保护名单必须由真实通关状态提供；请求、客户端计数和编队保存均不能创建或清除保护。
    public TagEvildomConfiguration Reset(IReadOnlySet<ushort> protectedMembers)
    {
        Validate();
        return this with
        {
            Teams = Teams.Select(team => protectedMembers.Contains(team.First) || protectedMembers.Contains(team.Second)
                ? team : default).ToImmutableArray()
        };
    }

    internal static string ValidateChange(TagEvildomConfiguration before, TagEvildomConfiguration after,
        IReadOnlyList<CharacterRosterEntry> roster, IReadOnlySet<ushort> protectedMembers)
    {
        before.Validate();
        after.Validate();
        if (before.Revision != after.Revision) return "configuration-revision-changed";
        var characters = roster.ToDictionary(row => row.Id);
        for (int index = 0; index < 3; index++)
        {
            var previous = before.Teams[index];
            var next = after.Teams[index];
            if ((protectedMembers.Contains(previous.First) || protectedMembers.Contains(previous.Second)) && next != previous)
                return "protected-team-changed";
            foreach (ushort member in new[] { next.First, next.Second })
            {
                if (member == 0) continue;
                if (!characters.TryGetValue(member, out var character)) return "character-not-in-account";
                if (character.Level < 95 || character.IsPvpPrivate) return "character-not-eligible";
                if (protectedMembers.Contains(member) && !previous.Contains(member)) return "protected-character-reassigned";
            }
        }
        return "ready";
    }
}

// 编队配置与通关账本分开；此合同不接收或修改客户端提供的清图次数、保护名单或资产。
internal interface ITagEvildomConfigurationStore
{
    TagEvildomConfiguration ReadTagEvildomConfiguration(AccountOwners.Lease owner);
    TagEvildomState ReadTagEvildomState(AccountOwners.Lease owner, TagEvildomPeriod period);
    bool MatchesTagEvildomCharacters(AccountOwners.Lease owner, TagEvildomCharacters expected);
    TagEvildomConfiguration CommitTagEvildomConfiguration(AccountOwners.Lease owner,
        TagEvildomConfiguration expected, TagEvildomConfiguration desired, TagEvildomPeriod? period = null);
}

// 本服日界沿 UTC+8 06:00；再战魔界周界为周一，不能复用其它模块的周四周界。
internal readonly record struct TagEvildomPeriod(int Day, int Week)
{
    public static TagEvildomPeriod At(DateTimeOffset now)
    {
        var day = now.UtcDateTime.AddHours(2).Date;
        var monday = day.AddDays(-((int)day.DayOfWeek + 6) % 7);
        static int Key(DateTime value) => value.Year * 10000 + value.Month * 100 + value.Day;
        return new(Key(day), Key(monday));
    }
}

internal sealed record TagEvildomState(TagEvildomConfiguration Configuration, int UsedThisWeek,
    ImmutableHashSet<ushort> ProtectedMembers);
