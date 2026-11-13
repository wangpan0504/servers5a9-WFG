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
        foreach (string mode in new[] { "disconnect", "depart", "confirmed-disconnect", "automatic", "ineligible", "ineligible-depart", "ineligible-timeout", "revision" })
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
            pending.Event, pending.Anchor.ToUnixTimeSeconds(), actor.Character, false,
            !mode.StartsWith("ineligible", StringComparison.Ordinal) && (actors.Length == 1 || index != actors.Length - 1),
            "fixture", policies[0].Source, pending.Week, offers)).ToImmutableArray();
        battle.AcceptLukeSettlement(pending, new(eligibility));
        foreach (var row in rows) battle.ReleaseDungeon(row.Actor, run);
        var view = battle.View;
        That(view.State == AntonRaidBattleState.Reward && !battle.LukeMovieCardsReady(view) &&
            !battle.CanClaimRaidReward(actors[0], 0) && !battle.CanClaimRaidReward(actors[0], 0, automaticLuke: true),
            "P1真实终结后仍等待动画，提前660与未到期自动资产均拒绝");
        if (mode.StartsWith("ineligible", StringComparison.Ordinal))
        {
            That(battle.AdvanceAfterRewards(view.Instance, view.Revision) is null,
                "全员达到领奖上限仍保留成功动画，不因空资产义务立即进入等待阶段");
            if (mode == "ineligible-timeout")
            {
                _ = battle.DueLukeRewards();
                clock.Advance(186);
                That(battle.DueLukeRewards().Length == 0 && battle.AdvanceAfterRewards(view.Instance, view.Revision) is not null,
                    "无资格且断线未确认仍沿原185秒有界收尾，不造任何自动奖励");
            }
            else
            {
                foreach (var actor in actors.Take(actors.Length - 1))
                    That(battle.CompleteLukeFirstMovie(actor, battle.View) && battle.View.State == AntonRaidBattleState.Reward,
                        "无资格成员逐个真实确认，最后一人之前不得进入等待阶段");
                if (mode == "ineligible-depart")
                    That(battle.DetachStart(actors[^1]), "最后未确认的无资格成员主动离团释放动画屏障");
                else
                    That(battle.CompleteLukeFirstMovie(actors[^1], battle.View), "最后一个无资格成员确认完成演出");
            }
            That(battle.View.State == AntonRaidBattleState.Intermission && actors.All(actor =>
                battle.LukeEligibility(actor.Character, 0) is { Eligible: false }),
                "成功演出或原超时后才进等待，原无领奖资格保持不变");
            return;
        }
        That(!battle.CompleteLukeFirstMovie(actors[0], view with { Revision = view.Revision + 1 }),
            "复制或过期view不能登记659=1");
        if (mode == "revision")
        {
            var movie = battle.CaptureMovie(actors[0])!.Value;
            int acknowledgements = 0;
            bool Ack() { acknowledgements++; return true; }
            That(battle.ConfirmMovie(actors[0], view, Guid.NewGuid(), true, Ack) is null &&
                battle.ConfirmMovie(actors[0], view with { Phase = 1 }, movie.Event, true, Ack) is null &&
                battle.ConfirmMovie(actors[0], view with { Instance = Guid.NewGuid() }, movie.Event, true, Ack) is null && acknowledgements == 0,
                "旧阶段、另一实例或错误完成事件均不能提交电影事实或排成功ACK");
            // 明确注入捕获后、确认前的无关显示版本更新；不伪称为真实客户端或正式退场。
            typeof(AntonRaidBattle).GetField("current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(battle, view with { Revision = view.Revision + 1 });
            That(!battle.IsCurrent(view) && battle.CaptureMovie(actors[0])?.Event == movie.Event,
                "全局精确view守卫仍拒绝旧版本，同阶段已提交电影事件保持不变");
            That(battle.ConfirmMovie(actors[0], view, movie.Event, true, () => false) is { Changed: true, Acknowledged: false } &&
                battle.ConfirmMovie(actors[0], view, movie.Event, true, Ack) is { Changed: false, Acknowledged: true } && acknowledgements == 1,
                "同电影版本变化仍提交真实确认；ACK入队失败后重试只补ACK，不重复完成事实");
            foreach (var actor in actors.Skip(1)) That(battle.CompleteLukeFirstMovie(actor, battle.View), "其余真实确认解除原屏障");
            That(battle.LukeMovieCardsReady(battle.View), "重试后不丢确认，不等待185秒兜底才开牌");
            return;
        }
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
