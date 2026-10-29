using System.Collections.Immutable;

namespace DfoServer.Game.Pvp;

// 这些值只属于领域，不能直接写入客户端的模式、席位或结果字段。
internal enum PvpArenaMode { Individual, Team, Relay }
internal enum PvpArenaMatchPhase { Loading, Fighting, Finished, Cancelled, Selecting }
internal enum PvpArenaOutcome { Win, Lose, Draw, Cancelled }
internal sealed record PvpArenaFighter(Guid Membership, ushort CharacterId, byte Seat, byte Team)
{
    public string Account { get; init; } = "";
    public byte? Job { get; init; }
    public PvpRatingSnapshot? Rating { get; init; }
    public PvpTotalTeam? TotalTeam { get; init; }
}
internal sealed record PvpArenaResult(Guid MatchId, ImmutableArray<PvpArenaStanding> Standings, bool Forfeit);
internal sealed record PvpArenaStanding(PvpArenaFighter Fighter, PvpArenaOutcome Outcome);

// 每局冻结参与者；所有调用由原房间门禁串行化。迟到报告不能落到下一局。
internal sealed class PvpArenaMatch
{
    readonly HashSet<Guid> loaded = [];
    readonly HashSet<Guid> connected = [];
    readonly HashSet<Guid> eliminated = [];
    readonly HashSet<Guid> departed = [];
    readonly Dictionary<Guid, PvpRatingReceipt> rated = [];
    readonly HashSet<Guid> resultQueued = [];
    readonly Dictionary<Guid, uint> kills = [];
    readonly TimeSpan fightDuration;
    readonly DateTimeOffset loadingDeadline;
    DateTimeOffset? fightDeadline;
    DateTimeOffset? resultReturnDeadline;

    public Guid Id { get; } = Guid.NewGuid();
    public PvpArenaMode Mode { get; }
    public ImmutableArray<PvpArenaFighter> Fighters { get; }
    public PvpTotalRoundState? Total { get; }
    public PvpArenaMatchPhase Phase { get; private set; } = PvpArenaMatchPhase.Loading;
    public PvpArenaResult? Result { get; private set; }
    PvpArenaResultRecord? pendingRecord;
    public PvpArenaResultRecord? CommittedRecord { get; private set; }
    public bool HasRatingReceipt(Guid member) => rated.ContainsKey(member);
    public PvpRatingReceipt? RatingReceipt(Guid member) => rated.GetValueOrDefault(member);
    public bool IsResultQueued(Guid member) => resultQueued.Contains(member);
    public uint Kills(Guid member) => kills.GetValueOrDefault(member);
    public bool HasExpiredClock(DateTimeOffset now) => Phase == PvpArenaMatchPhase.Loading && now >= loadingDeadline ||
        Phase == PvpArenaMatchPhase.Fighting && now >= fightDeadline + TimeSpan.FromSeconds(15) ||
        Phase == PvpArenaMatchPhase.Selecting && Total is { } total && now >= total.Deadline;

    // 无报告时不猜剩余HP；超出报告宽限后按本服兼容平局收尾，加载失败则无奖取消。
    public bool ExpireClock(DateTimeOffset now)
    {
        if (Phase == PvpArenaMatchPhase.Loading) return ExpireLoading(Id, now);
        if (!HasExpiredClock(now)) return false;
        if (Phase == PvpArenaMatchPhase.Selecting && Total!.Start(expired: true))
        {
            Phase = PvpArenaMatchPhase.Fighting;
            fightDeadline = now + fightDuration;
            return true;
        }
        return ResolveTimeout(Id, null, now);
    }

    public void InstallRatingReceipt(Guid member, PvpRatingReceipt receipt)
    {
        if (CommittedRecord is null || receipt.MatchId != Id ||
            Result?.Standings.SingleOrDefault(row => row.Fighter.Membership == member) is not { } standing ||
            standing.Fighter.CharacterId != receipt.CharacterId || standing.Fighter.Account != receipt.Account ||
            standing.Outcome != receipt.Outcome)
            throw new InvalidOperationException("经验回执不属于本局原参战角色");
        if (rated.TryGetValue(member, out var existing))
        {
            if (existing != receipt) throw new InvalidOperationException("原参战角色已有不同的经验回执");
            return;
        }
        rated.Add(member, receipt);
    }

    // 仅在完整结果通知成功入队后确认；保存事实或到账本身不能打开返回门禁。
    public bool ConfirmResultQueued(Guid match, Guid member, PvpRatingReceipt receipt, DateTimeOffset? now = null)
    {
        if (match != Id || CommittedRecord is null || !rated.TryGetValue(member, out var installed) ||
            !ReferenceEquals(installed, receipt) || !resultQueued.Add(member)) return false;
        resultReturnDeadline = (now ?? DateTimeOffset.UtcNow) + TimeSpan.FromSeconds(30);
        return true;
    }

    public bool HasExpiredReturn(DateTimeOffset now) => resultReturnDeadline is { } deadline && now >= deadline &&
        Phase is PvpArenaMatchPhase.Finished or PvpArenaMatchPhase.Cancelled;

    public bool CanReturnResult(Guid member) => HasRatingReceipt(member) && resultQueued.Contains(member);

    public PvpArenaResultRecord? CaptureRecord(Guid room, ushort channel, DateTimeOffset now,
        PvpRatingMode ratingMode = PvpRatingMode.Ordinary)
    {
        if (Result is null) return null;
        // 首次失败后保留同一快照，重试不能更换时间或参与者来绕过去重。
        pendingRecord ??= new(Id, room, channel, Mode, Result.Standings.Select(row =>
            new PvpArenaResultParticipant(row.Fighter.Account, row.Fighter.CharacterId, row.Fighter.Seat,
                row.Fighter.Team, row.Outcome) { Rating = row.Fighter.Rating, TotalTeam = row.Fighter.TotalTeam, Job = row.Fighter.Job, Kills = Kills(row.Fighter.Membership) }).ToImmutableArray(), Result.Forfeit, now.ToUnixTimeSeconds())
            { RatingMode = ratingMode };
        pendingRecord.Validate();
        return pendingRecord;
    }

    public void InstallRecord(PvpArenaResultRecord record)
    {
        if (!ReferenceEquals(record, pendingRecord)) throw new InvalidOperationException("决斗回执不属于原局提交");
        CommittedRecord = record;
    }

    public PvpArenaMatch(PvpArenaMode mode, ImmutableArray<PvpArenaFighter> fighters,
        DateTimeOffset now, TimeSpan loadTimeout, TimeSpan duration)
    {
        if (!Enum.IsDefined(mode) || fighters.Length is < 2 or > 8 ||
            fighters.Any(row => row.Membership == Guid.Empty || row.CharacterId is 0 or ushort.MaxValue || row.Seat > 7) ||
            fighters.Select(row => row.Membership).Distinct().Count() != fighters.Length ||
            fighters.Select(row => row.CharacterId).Distinct().Count() != fighters.Length ||
            fighters.Select(row => row.Seat).Distinct().Count() != fighters.Length ||
            loadTimeout <= TimeSpan.Zero || duration <= TimeSpan.Zero ||
            loadTimeout > TimeSpan.FromMinutes(5) || duration > TimeSpan.FromHours(1) ||
            mode != PvpArenaMode.Individual && (fighters.Any(row => row.Team > 1) || fighters.Select(row => row.Team).Distinct().Count() != 2))
            throw new ArgumentException("决斗参与者、模式或时间边界无效");
        Mode = mode;
        Fighters = fighters;
        if (fighters.Any(row => row.TotalTeam is not null))
        {
            if (mode != PvpArenaMode.Individual || fighters.Length != 2 || fighters.Any(row => row.TotalTeam is not { Complete: true }))
                throw new ArgumentException("总力战必须冻结两个真实完整队伍");
            Total = new(fighters);
        }
        loadingDeadline = now + loadTimeout;
        fightDuration = duration;
    }

    public bool MarkLoaded(Guid match, Guid member, DateTimeOffset now) => MarkBarrier(match, member, now, loaded);
    public bool MarkConnected(Guid match, Guid member, DateTimeOffset now) => MarkBarrier(match, member, now, connected);

    bool MarkBarrier(Guid match, Guid member, DateTimeOffset now, HashSet<Guid> barrier)
    {
        if (match != Id || Phase != PvpArenaMatchPhase.Loading || !Contains(member)) return false;
        if (now >= loadingDeadline) { Cancel(); return false; }
        if (!barrier.Add(member)) return false;
        if (loaded.Count == Fighters.Length && (Total is not null || connected.Count == Fighters.Length))
        {
            if (Total is not null) { Total.BeginSelection(now); Phase = PvpArenaMatchPhase.Selecting; }
            else { Phase = PvpArenaMatchPhase.Fighting; fightDeadline = now + fightDuration; }
        }
        return true;
    }

    public bool IsActive(Guid member)
    {
        if (Phase != PvpArenaMatchPhase.Fighting || eliminated.Contains(member)) return false;
        var fighter = Fighters.FirstOrDefault(row => row.Membership == member);
        if (fighter is null) return false;
        return Mode != PvpArenaMode.Relay || Fighters.First(row => row.Team == fighter.Team &&
            !eliminated.Contains(row.Membership)).Membership == member;
    }

    // P2P 的死亡报告只允许报告本人；击杀者还须是本局当前参战的敌方。
    public bool ReportDeath(Guid match, Guid victim, Guid? killer, DateTimeOffset? now = null)
    {
        if (match != Id || !IsActive(victim) || killer is { } other &&
            (other == victim || !IsActive(other) || Mode != PvpArenaMode.Individual && Team(other) == Team(victim)))
            return false;
        if (killer is { } credited) kills[credited] = checked(Kills(credited) + 1);
        if (Total is not null)
        {
            Total.Defeat(victim);
            FinishTotalRound(now ?? DateTimeOffset.UtcNow);
            return true;
        }
        eliminated.Add(victim);
        CompleteIfDecided(false);
        return true;
    }

    public bool Depart(Guid match, Guid member)
    {
        if (match != Id || !Contains(member) || Phase is PvpArenaMatchPhase.Finished or PvpArenaMatchPhase.Cancelled ||
            !departed.Add(member)) return false;
        if (Phase == PvpArenaMatchPhase.Loading) { Cancel(); return true; }
        if (Total is not null) { Complete(Fighters.Single(row => row.Membership != member), true); return true; }
        eliminated.Add(member);
        CompleteIfDecided(true);
        return true;
    }

    public bool ExpireLoading(Guid match, DateTimeOffset now)
    {
        if (match != Id || Phase != PvpArenaMatchPhase.Loading || now < loadingDeadline) return false;
        Cancel();
        return true;
    }

    // 胜者必须来自已核验的本局超时判定；这里仅验证时钟与参战身份，不猜 HP 或评分公式。
    public bool ResolveTimeout(Guid match, Guid? winner, DateTimeOffset now)
    {
        if (match != Id || Phase != PvpArenaMatchPhase.Fighting || now < fightDeadline ||
            winner is { } member && !IsActive(member)) return false;
        if (Total is not null)
        {
            foreach (var fighter in Fighters)
                if (winner is null || fighter.Membership != winner) Total.Defeat(fighter.Membership);
            FinishTotalRound(now);
        }
        else Complete(winner is { } id ? Fighters.Single(row => row.Membership == id) : null, false);
        return true;
    }

    public bool SelectTotal(Guid match, Guid member, byte index, DateTimeOffset now)
    {
        if (match != Id || Phase != PvpArenaMatchPhase.Selecting || Total is null || now >= Total.Deadline ||
            !Total.Select(member, index)) return false;
        if (Total.Start()) { Phase = PvpArenaMatchPhase.Fighting; fightDeadline = now + fightDuration; }
        return true;
    }

    void FinishTotalRound(DateTimeOffset now)
    {
        var alive = Fighters.Where(row => !Total!.Exhausted(row.Membership)).ToArray();
        if (alive.Length <= 1) Complete(alive.FirstOrDefault(), false);
        else { Total!.BeginSelection(now); Phase = PvpArenaMatchPhase.Selecting; }
    }

    // 八席值的原服完整业务未还原；本服兼容为按组平均值最小者获胜，末个最大值席报告。
    public bool ReportTimeout(Guid match, Guid reporter, IReadOnlyList<uint> values, DateTimeOffset now)
    {
        if (match != Id || Phase != PvpArenaMatchPhase.Fighting || now < fightDeadline || values.Count != 8 ||
            !Contains(reporter) || departed.Contains(reporter)) return false;
        var present = Fighters.Where(row => !departed.Contains(row.Membership)).OrderBy(row => row.Seat).ToArray();
        for (int seat = 0; seat < 8; seat++)
            if (values[seat] > int.MaxValue || values[seat] != 0 && present.All(row => row.Seat != seat)) return false;
        var elected = present.Aggregate((left, right) => values[right.Seat] >= values[left.Seat] ? right : left);
        if (elected.Membership != reporter) return false;
        var groups = present.GroupBy(row => Mode == PvpArenaMode.Individual ? row.Seat : row.Team)
            .Where(group => group.Any(row => IsActive(row.Membership)))
            .Select(group => new
            {
                Sum = group.Aggregate(0UL, (sum, row) => sum + values[row.Seat]),
                Count = (ulong)group.Count(), Representative = group.First(row => IsActive(row.Membership))
            }).ToArray();
        if (groups.Length == 0) return false;
        var best = groups[0];
        bool tied = false;
        foreach (var group in groups.Skip(1))
        {
            ulong candidate = group.Sum * best.Count, current = best.Sum * group.Count;
            if (candidate < current) { best = group; tied = false; }
            else if (candidate == current) tied = true;
        }
        return ResolveTimeout(match, tied ? null : best.Representative.Membership, now);
    }

    void CompleteIfDecided(bool forfeit)
    {
        var alive = Fighters.Where(row => !eliminated.Contains(row.Membership)).ToArray();
        if (Mode == PvpArenaMode.Individual)
        {
            if (alive.Length <= 1) Complete(alive.FirstOrDefault(), forfeit);
        }
        else if (alive.Select(row => row.Team).Distinct().Count() <= 1)
            Complete(alive.FirstOrDefault(), forfeit);
    }

    void Complete(PvpArenaFighter? winner, bool forfeit)
    {
        Phase = PvpArenaMatchPhase.Finished;
        Result = new(Id, Fighters.Select(row => new PvpArenaStanding(row, winner is null ? PvpArenaOutcome.Draw :
            (Mode == PvpArenaMode.Individual ? row.Membership == winner.Membership : row.Team == winner.Team)
                ? PvpArenaOutcome.Win : PvpArenaOutcome.Lose)).ToImmutableArray(), forfeit || departed.Count != 0);
    }

    void Cancel()
    {
        Phase = PvpArenaMatchPhase.Cancelled;
        Result = new(Id, Fighters.Select(row => new PvpArenaStanding(row, PvpArenaOutcome.Cancelled)).ToImmutableArray(), false);
    }

    bool Contains(Guid member) => Fighters.Any(row => row.Membership == member);
    byte Team(Guid member) => Fighters.Single(row => row.Membership == member).Team;
}
