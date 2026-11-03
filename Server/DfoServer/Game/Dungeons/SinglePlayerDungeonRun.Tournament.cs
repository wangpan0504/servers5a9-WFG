namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    // 四轮对象预先冻结在同一房间，但未完成的前轮不能被未来轮死亡越过。
    // 轮次只取已提交死亡集；准备或回滚均不能提前开放下一轮。
    internal bool CanReportTournamentDeath(ushort key)
    {
        if (current.Plan.Tournament is not { } tournament) return true;
        var opponent = tournament.Opponents.SingleOrDefault(row => row.ObjectKey == key);
        return opponent is null || tournament.Opponents.All(row => row.Round >= opponent.Round || current.Dead.Contains(row.ObjectKey));
    }
}
