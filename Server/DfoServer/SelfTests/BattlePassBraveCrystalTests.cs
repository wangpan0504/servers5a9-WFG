using DfoServer.Game.Adventure;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassBraveCrystalTests
{
    static readonly BattlePassMission Mission = new(28, 1, 1000, 26, "获得[骁勇结晶]", -1);
    public static void RunCore()
    {
        int count = Count; var catalog = AdventureCoreTests.SyntheticCatalog();
        var before = AdventureState.Initial("brave", DateTimeOffset.FromUnixTimeSeconds(1800000000));
        AdventureCreditPlan Plan(ulong overflow, AdventureState? state = null)
        {
            var first = state ?? before;
            return new(first, AdventureProgression.Advance(first, 1, overflow, catalog), 100, 100 + overflow,
                100, 100 + overflow, 1, overflow, 0);
        }
        var one = Plan(5);
        var originalAt = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var timed = one with { OccurredAt = originalAt };
        That(BattlePassBraveCrystalSource.OccurredAt(timed) == originalAt && BattlePassBraveCrystalSource.OccurredAt(timed) == originalAt,
            "原经验时点在同计划重试中保持不变，不二次读wallclock");
        Reject<InvalidDataException>(() => BattlePassBraveCrystalSource.OccurredAt(one), "无原业务时点不能提交取得");
        Reject<InvalidDataException>(() => BattlePassBraveCrystalSource.OccurredAt(one with { OccurredAt = DateTimeOffset.UnixEpoch }), "无效时点拒绝");
        That(BattlePassBraveCrystalSource.Acquired(one, AdventureCreditSource.Earned, catalog) == 1, "真实原溢出转换只计实际取得1");
        That(BattlePassBraveCrystalSource.Acquired(Plan(4), AdventureCreditSource.Earned, catalog) == 0, "未跨原兑换阈值无取得");
        That(BattlePassBraveCrystalSource.Acquired(Plan(2, before with { BraveExperience = 4 }), AdventureCreditSource.Earned, catalog) == 1, "原余数自然跨阈值计取得");
        That(BattlePassBraveCrystalSource.Acquired(Plan(10), AdventureCreditSource.Earned, catalog) == 2, "原每步数量保留");
        That(BattlePassBraveCrystalSource.Acquired(Plan(10, before with { Points = [249, 0, 0, 0, 0] }), AdventureCreditSource.Earned, catalog) == 1, "原250上限部分到账仅计真实1");
        That(BattlePassBraveCrystalSource.Acquired(Plan(10, before with { Points = [250, 0, 0, 0, 0] }), AdventureCreditSource.Earned, catalog) == 0, "满额不虚构取得");
        That(BattlePassBraveCrystalSource.Acquired(one, AdventureCreditSource.ExcludedGrant, catalog) == 0 &&
            BattlePassBraveCrystalSource.Acquired(one, AdventureCreditSource.ExcludedState, catalog) == 0, "道具经验和GM状态改写排除");
        foreach (var bad in new[] { one with { After = one.After with { Points = [2, 0, 0, 0, 0] } },
            one with { After = one.After with { BraveExperience = 1 } },
            one with { After = one.After with { Points = [1, 1, 0, 0, 0] } } })
            Reject<InvalidDataException>(() => BattlePassBraveCrystalSource.Acquired(bad, AdventureCreditSource.Earned, catalog), "冻结原兑换和其它余额不得变化");
        That(BattlePassBraveCrystalSource.SourceId(0).Length == 32 && BattlePassBraveCrystalSource.SourceId(0) == BattlePassBraveCrystalSource.SourceId(0) &&
            BattlePassBraveCrystalSource.SourceId(1) != BattlePassBraveCrystalSource.SourceId(0), "账号原余额revision为稳定32hex来源");
        Reject<InvalidDataException>(() => BattlePassBraveCrystalSource.SourceId(-1), "负revision拒绝");
        That(BattlePassBraveCrystalSource.Matches(1, Mission) && !BattlePassBraveCrystalSource.Matches(0, Mission), "仅原周28tuple");
        foreach (var bad in new[] { Mission with { Id = 29 }, Mission with { RequiredCount = 2 }, Mission with { Experience = 100 },
            Mission with { RawTypeCode = 29 }, Mission with { RawParameter = 0 } })
            That(!BattlePassBraveCrystalSource.Matches(1, bad), "相近任务不借原取得");
        That(BattlePassBraveCrystalSource.NextCount(Mission, 0, 2) == 1 && BattlePassBraveCrystalSource.NextCount(Mission, 1, 1) == 1, "原1封顶且不自动领取任务经验");
        Reject<InvalidDataException>(() => BattlePassBraveCrystalSource.NextCount(Mission, 0, 0), "零取得不计量");
        Console.WriteLine($"PASS {Count - count} BattlePassBraveCrystalTests core");
    }
}
