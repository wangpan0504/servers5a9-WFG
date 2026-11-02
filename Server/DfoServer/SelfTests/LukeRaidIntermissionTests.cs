using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实会话/owner与确定性时钟，清图及领奖确认采用显式领域事实；不是客户端演出验收。
internal static class LukeRaidIntermissionTests
{
    internal static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-luke-intermission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        try
        {
            foreach (int count in new[] { 1, 4, 8 })
            foreach (bool automatic in new[] { false, true })
            {
                var owners = new AccountOwners();
                var leases = new List<AccountOwners.Lease>();
                var sessions = new List<GameSession>();
                var clock = new LukeRaidClockTests.Clock();
                var origin = new Location(32, 4, 10, 10);
                var map = new TownMap("fixture/wait.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
                var towns = new TownCatalog(new() { [(32, 4)] = new(32, 4, 1, [], [], [], origin, map, [], []) },
                    origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
                var script = AntonRaidBattleTests.Script(10, 20) with { WaitingRoom = new(32, 4), Headers =
                    [new("[phase break time]", 1, [new(0, 600, null)]), new("[start delay time]", 2, [new(0, 3, null)])] };
                Reject<InvalidDataException>(() => new AntonRaidBattle(script, kind: RaidKind.Luke, clock: clock,
                    flow: LukeRaidFlowPolicy.Shared95), "缺少演出时长在开团前拒绝，不能领奖阶段才故障");
                script = script with { Headers = script.Headers.Add(new("[movie end time]", 3, [new(0, 65, null)])) };
                // 本夹具只验证休息时钟，未装配阶段资格/Present集合，不具有客户端演出屏障。
                var battle = new AntonRaidBattle(script, kind: RaidKind.Luke, clock: clock, flow: LukeRaidFlowPolicy.Shared95);
                var policies = Enumerable.Range(0, 2).Select(phase => new RaidPhaseRewardPolicy("fixture/flow",
                    [new(0, 0, [new(100, 1)], [])], new(0, (byte)phase, 0, 0, 0, 0))).ToImmutableArray();
                var repository = new CharacterRepository(Path.Combine(root, $"flow-{count}-{automatic}.db"));
                var registry = new RaidRegistry();
                var actors = new List<RaidActor>();
                var errors = new List<string>();
                var clocks = new LukeRaidClocks(clock, new UnusedStore(), (stage, _) => errors.Add(stage));
                try
                {
                    for (int index = 0; index < count; index++)
                    {
                        var owner = owners.Acquire("flow" + index, () => { }); leases.Add(owner);
                        repository.Create(owner, new("LkFlow" + index, origin, origin) { Level = 90, CompletedQuests = [2903, 4872] });
                        var session = new GameSession(repository, towns, owner, entry: new(new(2, 20), 32, "[raid]"));
                        sessions.Add(session); session.Select(0); session.MarkReady();
                        var actor = session.CaptureRaidActor(registry)!; actors.Add(actor);
                        That(session.AttachRaidCombat(actor, battle, policies), "休息夹具绑定真实当前会话");
                    }
                    using var timer = clocks.Reserve(battle)!; timer.Activate();
                    var id = Guid.NewGuid();
                    var rows = actors.Take(4).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, id, 1))).ToArray();
                    using (var reservation = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 10, rows)!) reservation.Install();
                    battle.CompleteLukeDungeon(rows.Select(row => (row.Actor, new DungeonClearFact(row.Identity, 10, [Guid.NewGuid()])
                        { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map") })).ToArray());
                    foreach (var row in rows) battle.ReleaseDungeon(row.Actor, id);
                    That(battle.View.State == AntonRaidBattleState.Reward && battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is null,
                        "未完成领奖义务不能进入休息或提前开P2");
                    foreach (var actor in actors) battle.ConfirmReward(actor, new(battle.Instance, 0, 0, 0, actor.Character,
                        "fixture/flow", [new(100, 1)], false, 1) { RaidType = RaidKind.Luke });
                    That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision)?.After is { Phase: 0, State: AntonRaidBattleState.Intermission },
                        "领奖齐进入原P1待命，不直接跳P2");
                    That(battle.LukeWaitSeconds(battle.View) == 600, "待命最大600秒且只建立一次锚点");
                    if (automatic)
                    {
                        actors[0].Session.CommandGate.Wait();
                        try
                        {
                            clock.Advance(600);
                            That(!battle.View.LukePreparing && battle.LukeWaitSeconds(battle.View) == 0,
                                "到期遇在途命令保持待命，不绕过共同门禁或续十分钟");
                        }
                        finally { actors[0].Session.CommandGate.Release(); }
                        clock.Advance(1);
                    }
                    else
                    {
                        clock.Advance(100);
                        That(battle.PrepareLukeNextPhase(actors[0]), "团长可在全员候战就绪后提前准备");
                    }
                    That(battle.View is { Phase: 0, LukePreparing: true } && battle.LukeWaitSeconds(battle.View) == 3,
                        "自然到期与手动均使用原三秒准备");
                    var preparing = battle.View;
                    That(!battle.PrepareLukeNextPhase(actors[0]) && ReferenceEquals(preparing, battle.View), "重复开团不能延长准备或重复收费");
                    clock.Advance(2);
                    That(battle.View.Phase == 0, "准备未到期不提前初始化P2");
                    clock.Advance(1);
                    That(battle.View is { Phase: 1, State: AntonRaidBattleState.Fighting, LukePreparing: false } && errors.Count == 0,
                        "同一框架按期初始化P2，不写费用或改变成员身份");
                }
                finally
                {
                    clocks.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    foreach (var session in sessions) session.End();
                    foreach (var lease in leases) lease.Dispose();
                }
            }
            passed = true;
        }
        finally { if (passed) TestFiles.DeleteDirectory(root, true); else Console.WriteLine("FAIL fixture: " + root); }
        Console.WriteLine($"PASS {Count - before} 卢克统一人数休息、提前开战和自动门禁断言");
    }

    sealed class UnusedStore : ILukeRaidLifecycleStore
    {
        public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate) =>
            throw new InvalidOperationException("fixture must not write phase eligibility");
    }
}
