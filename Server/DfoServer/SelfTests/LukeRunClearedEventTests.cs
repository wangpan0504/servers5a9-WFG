using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 借用原Current成员，仅替换并还原网络回调；不创建连接、数据库或发送泵。
internal static class LukeRunClearedEventTests
{
    internal static void Run(RaidActor[] actors)
    {
        foreach (string mode in new[] { "normal", "failure", "unsubscribed" }) Check(actors, mode);
    }

    static void Check(RaidActor[] actors, string mode)
    {
        var callbacks = actors.Select(actor => actor.Session.RaidRunClearedArrived).ToArray();
        var offers = ImmutableArray.Create(new RaidRewardOffer(0, 0, [new(100, 1)], []));
        var policies = Enumerable.Range(0, 2).Select(phase => new RaidPhaseRewardPolicy("fixture/run-clear",
            offers, new(0, (byte)phase, 0, 0, 0, 0))).ToImmutableArray();
        var completion = new LukeRaidCompletionPolicy("fixture/run-clear", 2,
            new(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
            policies.Select(policy => new LukePhaseCompletionPolicy(0, 2, policy)).ToImmutableArray(),
            LukeParticipationCutoff.TerminalCommittedFacts);
        var battle = new AntonRaidBattle(AntonRaidBattleTests.Script(10, 20), kind: RaidKind.Luke, completion: completion);
        var expected = new InvalidOperationException("fixture receiver failure");
        try
        {
            foreach (var actor in actors) That(battle.JoinStart(actor), "通关事件夹具登记原Current成员");
            for (int phase = 0; phase < 2; phase++)
            {
                int attempts = 0;
                var received = new HashSet<ushort>();
                foreach (var recipient in actors)
                    recipient.Session.RaidRunClearedArrived = mode == "unsubscribed" ? null : (cleared, actor) =>
                    {
                        attempts++;
                        That(received.Add(actor.Character) && ReferenceEquals(cleared.Battle, battle) &&
                            cleared.Phase == phase && cleared.Dungeon == (uint)(phase == 0 ? 10 : 20) &&
                            cleared.Members.SequenceEqual(actors.Take(4).Select(member => member.Character).Order()),
                            "每个原团员恰好收到同一正式Run的完整冻结成员事件");
                        That(!battle.RequiresLukeCompletedPhaseReturn(actors[0], battle.Instance, phase),
                            "全团最后一次入队尝试结束前，资格时钟也不能自动退场");
                        if (mode == "failure" && actor == actors[0]) throw expected;
                    };
                var run = Guid.NewGuid();
                var rows = actors.Take(4).Select(actor => (Actor: actor,
                    Identity: new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
                using (var reservation = battle.ReserveDungeon(new(battle.Instance, phase, 1, RaidKind.Luke),
                    (uint)(phase == 0 ? 10 : 20), rows)!) reservation.Install();
                var facts = rows.Select(row => (row.Actor,
                    new DungeonClearFact(row.Identity, (uint)(phase == 0 ? 10 : 20), [Guid.NewGuid()])
                    { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map") })).ToArray();
                var change = battle.CompleteLukeDungeon(facts)!;
                That(change.LukeClear is not null && battle.CompleteLukeDungeon(facts) is null &&
                    ReferenceEquals(change.LukeClear, battle.CaptureLukeRunCleared(run)),
                    "重复死亡只复用原Run的单次已提交事件");
                var candidate = battle.PendingLukeSettlement()!;
                var receipt = candidate.Members.Select(member => new RaidPhaseEligibility(battle.Instance,
                    RaidKind.Luke, phase, candidate.Event, candidate.Anchor.ToUnixTimeSeconds(), member.Character,
                    false, phase == 1, "fixture", policies[phase].Source, candidate.Week, offers)).ToImmutableArray();
                battle.AcceptLukeSettlement(candidate, new(receipt));
                That(battle.RequiresLukeCompletedPhaseReturn(actors[0], battle.Instance, phase) == (mode == "unsubscribed"),
                    "已提交资格仍等待网络入队义务，无消费者的领域诊断不被假阻塞");
                Exception? failure = null;
                try { change.LukeClear!.Publish(); }
                catch (Exception error) { failure = error; }
                That(mode == "failure" ? ReferenceEquals(failure, expected) : failure is null,
                    "全团尝试完成后保留接收者原异常");
                That(attempts == (mode == "unsubscribed" ? 0 : actors.Length) &&
                    battle.RequiresLukeCompletedPhaseReturn(actors[0], battle.Instance, phase),
                    "单个接收者抛错不截断其他团员，全部尝试结束即可自动退场");
                change.LukeClear!.Publish();
                That(attempts == (mode == "unsubscribed" ? 0 : actors.Length), "重复发布不重发已提交通关事件");
                foreach (var row in rows) battle.ReleaseDungeon(row.Actor, run);
                That(phase == 0 ? battle.View.Phase == 1 : battle.View.State == AntonRaidBattleState.Reward,
                    "入队后仍由原Run退出与资格屏障推进阶段");
            }
        }
        finally
        {
            for (int index = 0; index < actors.Length; index++) actors[index].Session.RaidRunClearedArrived = callbacks[index];
        }
    }
}
