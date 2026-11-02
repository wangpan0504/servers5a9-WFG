using System.Collections.Immutable;

namespace DfoServer.Game.Pvp;

internal sealed record PvpArenaMember(Guid Token, string Account, ushort CharacterId, byte Seat,
    byte Team, bool Spectator, bool Ready)
{
    public byte? Job { get; init; }
    public ImmutableArray<DfoServer.Game.Characters.ActiveMainQuest> QuestBindings { get; init; } = [];
}
internal sealed record PvpArenaRoomView(Guid Instance, ushort Id, ushort Channel, string Title,
    PvpArenaMode Mode, ushort Map, byte Leader, ImmutableArray<PvpArenaMember> Members,
    ImmutableArray<byte> ClosedSeats, Guid? MatchId, PvpArenaMatchPhase? MatchPhase, long Revision)
{
    public byte TitlePreset { get; init; }
    public bool IsTotal { get; init; }
}

// Commands 的身份在房间存活期间固定。调用方先取本门禁，再取连接门禁和账号租约。
internal sealed class PvpArenaRoom
{
    readonly Dictionary<Guid, PvpArenaMember> members = [];
    readonly HashSet<byte> closed = [];
    readonly HashSet<Guid> returned = [];
    public SemaphoreSlim Commands { get; } = new(1, 1);
    public Guid Instance { get; } = Guid.NewGuid();
    public ushort Id { get; }
    public ushort Channel { get; }
    public string Title { get; }
    public byte TitlePreset { get; }
    readonly string password;
    public bool RequiresPassword => password.Length != 0;
    public PvpArenaMode Mode { get; private set; }
    public ushort Map { get; private set; }
    public Guid Leader { get; private set; }
    public long Revision { get; private set; }
    public PvpArenaMatch? Match { get; private set; }
    public PvpMatchingLoad? MatchingLoad { get; }
    public bool IsMatching => MatchingLoad is not null;
    public bool IsTotal => MatchingLoad?.Members[0].IsTotal == true;
    public bool IsEmpty => members.Count == 0;
    public bool IsPlaying => Match?.Phase is PvpArenaMatchPhase.Loading or PvpArenaMatchPhase.Fighting or PvpArenaMatchPhase.Selecting;

    public PvpArenaRoom(ushort id, ushort channel, string title, PvpArenaMode mode, ushort map,
        byte titlePreset = 0, string password = "", PvpMatchingLoad? matchingLoad = null)
    {
        if (id is 0 or ushort.MaxValue || titlePreset > 16 ||
            titlePreset == 0 && string.IsNullOrWhiteSpace(title) || !Enum.IsDefined(mode))
            throw new ArgumentException("决斗房间编号、标题或模式无效");
        Id = id;
        Channel = channel;
        Title = title;
        TitlePreset = titlePreset;
        this.password = password;
        MatchingLoad = matchingLoad;
        Mode = mode;
        Map = map;
    }

    public PvpArenaMember? Join(string account, ushort character, byte team, bool spectator = false, byte? job = null,
        ImmutableArray<DfoServer.Game.Characters.ActiveMainQuest> questBindings = default)
    {
        if (Match is not null && !spectator || character is 0 or ushort.MaxValue || string.IsNullOrWhiteSpace(account) || team > 1 ||
            members.Count >= 8 || members.Values.Any(row => row.Account == account || row.CharacterId == character)) return null;
        int seat = Enumerable.Range(0, 8).FirstOrDefault(index => !closed.Contains((byte)index) &&
            members.Values.All(row => row.Seat != index), -1);
        if (seat < 0) return null;
        var member = new PvpArenaMember(Guid.NewGuid(), account, character, (byte)seat, team, spectator, false)
            { Job = job, QuestBindings = questBindings.IsDefault ? [] : questBindings };
        members.Add(member.Token, member);
        if (Leader == Guid.Empty) Leader = member.Token;
        Revision++;
        return member;
    }

    public bool Move(Guid token, byte seat, byte team, bool spectator)
    {
        if (Match is not null || seat > 7 || team > 1 || closed.Contains(seat) || !members.TryGetValue(token, out var member) ||
            members.Values.Any(row => row.Token != token && row.Seat == seat)) return false;
        if (member.Seat == seat && member.Team == team && member.Spectator == spectator) return false;
        members[token] = member with { Seat = seat, Team = team, Spectator = spectator, Ready = false };
        Revision++;
        return true;
    }

    public bool SetSeatClosed(Guid actor, byte seat, bool value)
    {
        if (Match is not null || actor != Leader || seat > 7 || members.Values.Any(row => row.Seat == seat)) return false;
        if (value ? !closed.Add(seat) : !closed.Remove(seat)) return false;
        Revision++;
        return true;
    }

    public bool SetReady(Guid token, bool value)
    {
        if (Match is not null || !members.TryGetValue(token, out var member) || member.Spectator || member.Ready == value) return false;
        members[token] = member with { Ready = value };
        Revision++;
        return true;
    }

    public bool Configure(Guid actor, PvpArenaMode mode, ushort map)
    {
        if (Match is not null || actor != Leader || !Enum.IsDefined(mode) || mode == Mode && map == Map) return false;
        Mode = mode;
        Map = map;
        foreach (var row in members.Values.ToArray()) members[row.Token] = row with { Ready = false };
        Revision++;
        return true;
    }

    public PvpArenaMatch? Start(Guid actor, DateTimeOffset now, TimeSpan loadTimeout, TimeSpan duration,
        IReadOnlyDictionary<ushort, PvpRatingSnapshot>? ratings = null)
    {
        if (actor != Leader || Match is not null || members.Values.Any(row => !row.Spectator && row.Token != Leader && !row.Ready)) return null;
        var fighters = members.Values.Where(row => !row.Spectator).OrderBy(row => row.Seat).ToArray();
        if (fighters.Length < 2 || Mode != PvpArenaMode.Individual && fighters.Select(row => row.Team).Distinct().Count() != 2) return null;
        if (IsMatching && (ratings is null || fighters.Any(row => !ratings.ContainsKey(row.CharacterId)))) return null;
        Match = new(Mode, fighters.Select(row => new PvpArenaFighter(row.Token, row.CharacterId, row.Seat, row.Team)
            { Account = row.Account, Job = row.Job, QuestBindings = row.QuestBindings, Rating = ratings?.GetValueOrDefault(row.CharacterId),
                TotalTeam = MatchingLoad?.Members.Single(ticket => ticket.Actor.Character.Id == row.CharacterId).TotalRoster?.Team }).ToImmutableArray(),
            now, loadTimeout, duration);
        foreach (var row in members.Values.ToArray()) members[row.Token] = row with { Ready = false };
        Revision++;
        return Match;
    }

    public bool EndResult(Guid token, Guid match)
    {
        if (!members.ContainsKey(token) || Match is not { CommittedRecord: not null } current || current.Id != match ||
            !current.CanReturnResult(token) ||
            !returned.Add(token)) return false;
        // 已离场者不再阻塞原局；旁观者不增加下一局的参战返回屏障。
        CompleteResultReturn();
        Revision++;
        return true;
    }

    public PvpArenaMember? Leave(Guid token)
    {
        if (!members.Remove(token, out var departed)) return null;
        Match?.Depart(Match.Id, token);
        returned.Remove(token);
        CompleteResultReturn();
        if (Leader == token) Leader = members.Values.OrderBy(row => row.Seat).FirstOrDefault()?.Token ?? Guid.Empty;
        foreach (var row in members.Values.ToArray()) members[row.Token] = row with { Ready = false };
        Revision++;
        return departed;
    }

    public bool ExpireResult(DateTimeOffset now)
    {
        if (Match is not { } current || !current.HasExpiredReturn(now) ||
            current.Fighters.Any(row => members.ContainsKey(row.Membership) && !current.CanReturnResult(row.Membership))) return false;
        foreach (var fighter in current.Fighters)
            if (members.ContainsKey(fighter.Membership)) EndResult(fighter.Membership, current.Id);
        return Match is null;
    }

    void CompleteResultReturn()
    {
        if (Match is not { CommittedRecord: not null } current ||
            current.Fighters.Any(row => members.ContainsKey(row.Membership) && !returned.Contains(row.Membership))) return;
        Match = null;
        returned.Clear();
        foreach (var row in members.Values.ToArray()) members[row.Token] = row with { Ready = false };
    }

    public PvpArenaMember? Member(Guid token) => members.GetValueOrDefault(token);

    public bool AcceptsPassword(string? supplied) => !RequiresPassword ||
        string.Equals(password, supplied, StringComparison.Ordinal);

    public PvpArenaRoomView View() => new(Instance, Id, Channel, Title, Mode, Map,
        members.GetValueOrDefault(Leader)?.Seat ?? byte.MaxValue,
        members.Values.OrderBy(row => row.Seat).ToImmutableArray(), closed.Order().ToImmutableArray(),
        Match?.Id, Match?.Phase, Revision) { TitlePreset = TitlePreset, IsTotal = IsTotal };
}
