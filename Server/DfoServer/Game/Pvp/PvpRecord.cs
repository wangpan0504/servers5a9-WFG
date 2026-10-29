using DfoServer.Game.Accounts;
using DfoServer.Game.Social;

namespace DfoServer.Game.Pvp;

internal sealed record PvpRecordStatistics(int Wins, int Losses, int Draws,
    byte RecentWins, byte RecentLosses, byte RecentDraws, int WinStreak, int HighestWinStreak)
{
    internal static readonly PvpRecordStatistics Empty = new(0, 0, 0, 0, 0, 0, 0, 0);
}

internal interface IPvpRecordStore
{
    PvpRecordStatistics ReadPvpRecord(AccountOwners.Lease owner, ushort characterId);
}

internal sealed record PvpRecordSnapshot(ushort CharacterId, byte View, bool Self, PvpRecordStatistics Statistics);

// 当前服务端的个人匹配记录归当前赛季；没有历史赛季来源时不把当前记录重复投影到旧页。
internal sealed class PvpRecordQuery(IPvpRecordStore store, IPvpTotalTeamStore teams)
{
    internal PvpRecordSnapshot? Capture(SocialActor target, ushort expectedCharacterId, byte view, bool self, int memberIndex)
    {
        if (view is < 2 or > 6 || memberIndex is < -1 or > 2 || target.Character.Id != expectedCharacterId) return null;
        try
        {
            return target.Execute<PvpRecordSnapshot?>(() =>
            {
                if (memberIndex >= 0 && (teams.ReadPvpTotalTeam(target.Owner) is not { } team ||
                    team.Characters[memberIndex] != target.Character.Id)) return null;
                return new PvpRecordSnapshot(target.Character.Id, view, self,
                    view == 6 ? store.ReadPvpRecord(target.Owner, target.Character.Id) : PvpRecordStatistics.Empty);
            });
        }
        catch (InvalidOperationException) when (!target.IsCurrent()) { return null; }
    }
}
