using DfoServer.Game.Adventure;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassGloryCrystalTests
{
    static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    static readonly Guid RunId = Guid.ParseExact("f33d56d1168d49b4b554eb0f5c771ade", "N");
    static readonly BattlePassMission Mission = new(39, 20, 1000, 29, "获得[荣耀结晶]", -1);

    public static void RunCore()
    {
        int before = Count; var catalog = AdventureCoreTests.SyntheticCatalog(); var plan = Plan();
        var run = plan.Before.State.Expeditions.Single();
        That(BattlePassGloryCrystalSource.Acquired(plan, run, catalog, At) == 1, "原到期远征实际取得1荣耀结晶");
        var reread = run with { Attributes = [.. run.Attributes], Members = [.. run.Members], FrozenMembers = [.. run.FrozenMembers] };
        That(BattlePassGloryCrystalSource.Acquired(plan, reread, catalog, At) == 1,
            "原事务重读分配独立数组但完整身份内容一致时仍是同一真实远征");
        var merged = Plan(balance: 10, reward: 7);
        That(BattlePassGloryCrystalSource.Acquired(merged, merged.Before.State.Expeditions.Single(), catalog, At) == 7,
            "原余额不重复计量，只计原冻结本笔正差7");
        var partial = Plan(balance: 9998, reward: 5, after: 9999);
        That(BattlePassGloryCrystalSource.Acquired(partial, partial.Before.State.Expeditions.Single(), catalog, At) == 1,
            "原冻结奖5但余额上限只到账1时按实际1计量");
        var capped = Plan(balance: 9999, reward: 5, after: 9999);
        That(BattlePassGloryCrystalSource.Acquired(capped, capped.Before.State.Expeditions.Single(), catalog, At) == 0,
            "已满原上限的合法领取不产生虚假取得来源");
        var zero = Plan(reward: 0);
        That(BattlePassGloryCrystalSource.Acquired(zero, zero.Before.State.Expeditions.Single(), catalog, At) == 0,
            "原零奖励领取不产取得事实");
        That(BattlePassGloryCrystalSource.Acquired(plan with { Action = "expedition_start" }, run, catalog, At) == 0 &&
            BattlePassGloryCrystalSource.Acquired(plan with { Action = "expedition_cancel" }, run, catalog, At) == 0,
            "派遣与取消沿原业务但不冒真实取得");
        foreach (var invalid in new[] { plan with { ActorId = 0 }, plan with { ActorId = 2 },
            plan with { After = plan.After with { Expeditions = [run] } }, Plan(after: 2),
            plan with { After = plan.After with { Points = [1, 1, 0, 0, 0] } } })
            Reject<InvalidDataException>(() => BattlePassGloryCrystalSource.Acquired(invalid, run, catalog, At),
                "原actor、运行删除或到账核心不符不能登记荣耀来源");
        Reject<InvalidDataException>(() => BattlePassGloryCrystalSource.Acquired(plan, run, catalog, At.AddSeconds(-1)), "未到期不得冒荣耀领取来源");
        Reject<InvalidDataException>(() => BattlePassGloryCrystalSource.Acquired(plan, run with { Id = Guid.NewGuid() }, catalog, At), "其它远征不能借用原计划");
        That(BattlePassGloryCrystalSource.SourceId(RunId) == RunId.ToString("N") &&
            BattlePassGloryCrystalSource.SourceId(RunId).Length == 32, "来源沿原run GUID确定32hex身份");
        Reject<InvalidDataException>(() => BattlePassGloryCrystalSource.SourceId(Guid.Empty), "空runGUID不形成来源");
        That(BattlePassGloryCrystalSource.Matches(1, Mission) && !BattlePassGloryCrystalSource.Matches(0, Mission), "只认原周39荣耀定义");
        foreach (var wrong in new[] { Mission with { Id = 12 }, Mission with { RawTypeCode = 12 }, Mission with { RawParameter = 0 },
            Mission with { RequiredCount = 21 }, Mission with { Experience = 100 } })
            That(!BattlePassGloryCrystalSource.Matches(1, wrong), "相近任务或字段变化不冒原荣耀tuple");
        That(BattlePassGloryCrystalSource.NextCount(Mission, 0, 1) == 1 &&
            BattlePassGloryCrystalSource.NextCount(Mission, 19, 7) == 20 && BattlePassGloryCrystalSource.NextCount(Mission, 20, 1) == 20,
            "只按真实数量累积并保持原20封顶，不直接领取XP");
        Reject<InvalidDataException>(() => BattlePassGloryCrystalSource.NextCount(Mission, -1, 1), "负进度拒绝");
        Reject<InvalidDataException>(() => BattlePassGloryCrystalSource.NextCount(Mission, 0, 0), "零取得不计量");
        Console.WriteLine($"PASS {Count - before} BattlePassGloryCrystalTests core");
    }

    static AdventureExpeditionPlan Plan(uint balance = 0, uint reward = 1, uint? after = null)
    {
        var run = new AdventureExpedition(1, RunId, At.ToUnixTimeSeconds() - 21600, At.ToUnixTimeSeconds(), reward, 0, [], [1])
        { FrozenMembers = [new(1, 0, "荣耀", 0, 1, 95)] };
        var state = AdventureState.Initial("glory", At) with { Points = [0, balance, 0, 0, 0], Expeditions = [run] };
        var snapshot = new AdventureSnapshot(state, [new(1, 0, "荣耀", 0, 33, 95, 0, 0)]);
        return new(snapshot, state with { Points = [0, after ?? checked(balance + reward), 0, 0, 0], Expeditions = [] }, "expedition_claim", 1);
    }
}
