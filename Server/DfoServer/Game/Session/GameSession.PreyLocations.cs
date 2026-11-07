using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 调用方已在原owner内提交位置；只取得展示变化，广播在投影成功后执行。
    AntonRaidBattleChange? RecordPreyTownLocation(AntonRaidBattleChange? previous = null)
    {
        RecordPreyBoardingArrival();
        if (raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey } raid ||
            Selected?.Position is not { Town: 59, Area: >= 2 and <= 5 } position ||
            raid.Battle.ReportPreyTownLocation(raid.Actor, position.Area) is not { } changed) return previous;
        return previous is null ? changed : previous with { After = changed.After };
    }
}
