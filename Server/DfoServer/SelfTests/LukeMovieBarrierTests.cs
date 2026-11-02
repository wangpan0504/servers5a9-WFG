using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 借用已存在的原actor，只创建小型领域Battle，不建立Session绑定、SQL或网络实例。
internal static class LukeMovieBarrierTests
{
    internal static void Run(RaidActor[] original)
    {
        foreach (int count in new[] { 1, 4, 8 }) Check(original.Take(count).ToArray(), "all");
        foreach (string mode in new[] { "disconnect", "depart", "confirmed-disconnect", "automatic" })
            Check(original.Take(4).ToArray(), mode);
    }

    static void Check(RaidActor[] actors, string mode)
    {
        var clock = new LukeRaidClockTests.Clock();
        var offers = ImmutableArray.Create(new RaidRewardOffer(1, 0, [new(0, 7)], []),
            new RaidRewardOffer(0, 0, [new(0, 11)], []));
        var policies = Enumerable.Range(0, 2).Select(phase => new RaidPhaseRewardPolicy("fixture/movie-barrier",
            offers, new(0, (byte)phase, 0, 0, 0, 0))).ToImmutableArray();
        var completion = new LukeRaidCompletionPolicy("fixture/movie-barrier", 2,
            new(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
            policies.Select(policy => new LukePhaseCompletionPolicy(0, 2, policy)).ToImmutableArray(),
            LukeParticipationCutoff.TerminalCommittedFacts);
        var script = AntonRaidBattleTests.Script(10, 20) with { Headers =
            [new("[phase break time]", 1, [new(0, 600, null)]), new("[start delay time]", 2, [new(0, 3, null)]),
             new("[movie end time]", 3, [new(0, 65, null)])] };
        var battle = new AntonRaidBattle(script, kind: RaidKind.Luke, clock: clock,
            flow: LukeRaidFlowPolicy.Shared95, completion: completion);
        foreach (var actor in actors) That(battle.JoinStart(actor), "动画夹具使用原Current成员");
        var run = Guid.NewGuid();
        var rows = actors.Take(4).Select(actor => (Actor: actor,
            Identity: new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
        using (var reservation = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 10, rows)!)
            reservation.Install();
        battle.CompleteLukeDungeon(rows.Select(row => (row.Actor,
            new DungeonClearFact(row.Identity, 10, [Guid.NewGuid()])
            { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map") })).ToArray());
        var pending = battle.PendingLukeSettlement()!;
        var eligibility = actors.Select((actor, index) => new RaidPhaseEligibility(battle.Instance, RaidKind.Luke, 0,
            pending.Event, pending.Anchor.ToUnixTimeSeconds(), actor.Character, false, actors.Length == 1 || index != actors.Length - 1,
            "fixture", policies[0].Source, pending.Week, offers)).ToImmutableArray();
        battle.AcceptLukeSettlement(pending, new(eligibility));
        foreach (var row in rows) battle.ReleaseDungeon(row.Actor, run);
        var view = battle.View;
        That(view.State == AntonRaidBattleState.Reward && !battle.LukeMovieCardsReady(view) &&
            !battle.CanClaimRaidReward(actors[0], 0) && !battle.CanClaimRaidReward(actors[0], 0, automaticLuke: true),
            "P1真实终结后仍等待动画，提前660与未到期自动资产均拒绝");
        That(!battle.CompleteLukeFirstMovie(actors[0], view with { Revision = view.Revision + 1 }),
            "复制或过期view不能登记659=1");
        var last = actors[^1];
        That(battle.CanConfirmLukeMovie(last, view), "Present成员有动画确认入口，多人末位无领奖资格仍须确认");
        if (mode == "confirmed-disconnect")
            That(battle.CompleteLukeFirstMovie(last, view), "断线前的真实确认先记账");
        if (mode is "disconnect" or "automatic" or "confirmed-disconnect")
            That(battle.DetachStart(last, disconnected: true), "断线按明确原因移除在线actor");
        if (mode == "depart") That(battle.DetachStart(last), "主动离团退出等待集合");
        foreach (var actor in actors.Take(actors.Length - 1))
        {
            That(battle.CompleteLukeFirstMovie(actor, battle.View), "真实659=1逐个登记");
            That(!battle.CompleteLukeFirstMovie(actor, battle.View), "重复659=1不重复完成");
        }
        if (mode == "all")
        {
            That(!battle.LukeMovieCardsReady(battle.View), "前面成员完成仍不能提前给任何人开牌");
            That(battle.CompleteLukeFirstMovie(last, battle.View), "最后Present成员的真实确认解除全员屏障");
        }
        bool ready = mode is "all" or "depart" or "confirmed-disconnect";
        That(battle.LukeMovieCardsReady(battle.View) == ready && battle.CanClaimRaidReward(actors[0], 0) == ready,
            "只有全员真实确认或主动离团才同时开放UI与手动资产，断线不冒充确认");
        if (mode == "automatic")
        {
            That(battle.DueLukeRewards().Length == 0, "首次读取固定原185秒锚点");
            clock.Advance(186);
            That(battle.CanClaimRaidReward(actors[0], 0, automaticLuke: true) &&
                !battle.CanClaimRaidReward(actors[0], 0) && !battle.LukeMovieCardsReady(battle.View),
                "185秒仅自动资产例外放行，不伪造断线动画确认或提前打开牌面");
        }
    }
}
