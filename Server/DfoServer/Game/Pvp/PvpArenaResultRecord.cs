using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Pvp;

// 整局事实与奖励分开保存；未知奖励字段不能由胜负回执推导成到账。
internal sealed record PvpArenaResultParticipant(string Account, ushort CharacterId, byte Seat, byte Team,
    PvpArenaOutcome Outcome)
{
    public PvpRatingSnapshot? Rating { get; init; }
    public PvpTotalTeam? TotalTeam { get; init; }
}
internal sealed record PvpArenaResultRecord(Guid MatchId, Guid RoomInstance, ushort Channel, PvpArenaMode Mode,
    ImmutableArray<PvpArenaResultParticipant> Participants, bool Forfeit, long FinishedAt)
{
    public PvpRatingMode RatingMode { get; init; } = PvpRatingMode.Ordinary;

    public void Validate()
    {
        if (MatchId == Guid.Empty || RoomInstance == Guid.Empty || !Enum.IsDefined(Mode) || !Enum.IsDefined(RatingMode) || FinishedAt < 0 ||
            Participants.IsDefault || Participants.Length is < 2 or > 8 ||
            Participants.Any(row => row is null || string.IsNullOrWhiteSpace(row.Account) || row.CharacterId is 0 or ushort.MaxValue ||
                row.Seat > 7 || row.Team > 1 || !Enum.IsDefined(row.Outcome)) ||
            Participants.Select(row => row.Account).Distinct(StringComparer.Ordinal).Count() != Participants.Length ||
            Participants.Select(row => row.CharacterId).Distinct().Count() != Participants.Length ||
            Participants.Select(row => row.Seat).Distinct().Count() != Participants.Length ||
            Mode != PvpArenaMode.Individual && Participants.Select(row => row.Team).Distinct().Count() != 2 ||
            Participants.Any(row => row.Outcome == PvpArenaOutcome.Cancelled) &&
                Participants.Any(row => row.Outcome != PvpArenaOutcome.Cancelled) ||
            Participants.Any(row => row.Outcome == PvpArenaOutcome.Draw) &&
                Participants.Any(row => row.Outcome != PvpArenaOutcome.Draw))
            throw new InvalidDataException("整局决斗回执身份或结果无效");
        if (RatingMode == PvpRatingMode.Matching && (Mode != PvpArenaMode.Individual || Participants.Length != 2 ||
            Participants.Any(row => row.Rating is null || row.Rating.Revision < 0)) ||
            RatingMode == PvpRatingMode.Ordinary && Participants.Any(row => row.Rating is not null))
            throw new InvalidDataException("匹配整局回执必须包含双方冻结评分，普通局不能冒用匹配快照");
        if (RatingMode == PvpRatingMode.Total
            ? Mode != PvpArenaMode.Individual || Participants.Length != 2 || Participants.Any(row => row.Rating is null ||
                row.Rating.Revision < 0 || row.TotalTeam is not { Complete: true } team || team.Id == Guid.Empty ||
                team.Revision <= 0 || !PvpTotalTeam.ValidName(team.Name) || team.Characters.Any(id => id is 0 or ushort.MaxValue) ||
                team.Characters.Distinct().Count() != 3 || !team.Characters.Contains(row.CharacterId)) ||
                Participants.SelectMany(row => row.TotalTeam!.Characters).Distinct().Count() != 6
            : Participants.Any(row => row.TotalTeam is not null))
            throw new InvalidDataException("总力事实须冻结两个不同账号的真实三角色队伍与独立队伍评分");
        if (Participants.Any(row => row.Outcome == PvpArenaOutcome.Win))
        {
            var winners = Participants.Where(row => row.Outcome == PvpArenaOutcome.Win).ToArray();
            if (Mode == PvpArenaMode.Individual && winners.Length != 1 ||
                Mode != PvpArenaMode.Individual && (winners.Select(row => row.Team).Distinct().Count() != 1 ||
                    Participants.Any(row => row.Team == winners[0].Team && row.Outcome != PvpArenaOutcome.Win)))
                throw new InvalidDataException("整局决斗胜方与模式不符");
        }
        else if (Participants.Any(row => row.Outcome == PvpArenaOutcome.Lose))
            throw new InvalidDataException("整局决斗失败结果缺少胜方");
    }
}

internal interface IPvpArenaResultStore
{
    PvpArenaResultRecord CommitPvpArenaResult(AccountOwners.Lease reporter, PvpArenaResultRecord result);
    PvpArenaResultRecord? ReadPvpArenaResult(AccountOwners.Lease reader, Guid matchId);
}
