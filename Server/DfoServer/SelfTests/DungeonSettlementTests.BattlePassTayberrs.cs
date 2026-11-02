using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunBattlePassTayberrsCore()
    {
        int before = Count;
        var at = new DateTimeOffset(2026, 11, 26, 7, 0, 0, TimeSpan.FromHours(8));
        var clock = new TayberrsClock(at);
        foreach (int id in new[] { 291100320, 291100322, 5349 })
        {
            var definition = Definition(id);
            var role = new Character(1, 0, "TayCore", 0, Location, Location);
            var (run, authority) = CreateRun(role, entry => entry with { DungeonId = checked((uint)id), QuestId = 0, ActiveQuestSlot = byte.MaxValue, RouteQuestId = 0 });
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var pending = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward).Pending!;
            pending.FreezeBattlePassTayberrsClear(definition, run.Entry, clock);
            var source = pending.BattlePassTayberrsClear;
            That(source is not null && source.Identity == run.Snapshot.Identity && source.PlannedAt == at &&
                ReferenceEquals(source.Clock, clock) && source.Matches(run, pending),
                "三正式模式沿原权威末杀/整场ClearFact冻结来源：" + id);
            clock.Now = at.AddMinutes(1);
            pending.FreezeBattlePassTayberrsClear(definition, run.Entry, clock);
            That(ReferenceEquals(pending.BattlePassTayberrsClear, source) && source!.PlannedAt == at,
                "同报告恢复保持原来源身份与统一Clock，不重新冻结计划时间：" + id);
            clock.Now = at;
            var copied = new PreparedDungeonSettlement(run.ClearFact! with
                { Identity = run.Snapshot.Identity with { InstanceId = Guid.NewGuid() } }, [1, 2, 3], Reward, pending.MapId);
            That(!source!.Matches(run, copied), "来源不能改绑另一实例GUID：" + id);
            foreach (var excluded in new[] {
                run.Entry with { IsPractice = true }, run.Entry with { IsTutorial = true },
                run.Entry with { IsQuestReplay = true }, run.Entry with { RaidEntry = new RaidDungeonIdentity(Guid.NewGuid(), 1, 0) },
                run.Entry with { RouteQuestId = 6116 }, run.Entry with { DungeonId = 7142, Maze = run.Entry.Maze with { IsClassExperience = true } }
            })
                That(BattlePassTayberrsClear.Prepare(definition, excluded, run.ClearFact!, clock) is null,
                    "正式同族仍拒练习/教程/重放/攻坚/剧情及职业体验入口：" + id);
            That(BattlePassTayberrsClear.Prepare(definition with { WarriorsDungeon = "normalfiend" }, run.Entry, run.ClearFact!, clock) is null,
                "编号相同但正常定义标签不同不得创建来源：" + id);
        }
        var map = new DungeonMap("boss", new(0, 0), 1, "fixture/tayberrs.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture-only", [map.Position], [map.Position], [map], []);
        foreach (int id in new[] { 291100321, 9541, 7191, 3 })
        {
            var entry = new DungeonEntryPlan(1, 0, checked((uint)id), 0, 0, 0, maze, map, map);
            var fact = new DungeonClearFact(new(1, 0, 1, Guid.NewGuid(), 1), checked((uint)id), [Guid.NewGuid()]);
            That(BattlePassTayberrsClear.Prepare(Definition(id), entry, fact, clock) is null,
                "任务版/升级版及其他副本不借同标签创建来源：" + id);
        }
        var exact = new BattlePassMission(50, 4, 1000, 44, "通关泰波尔斯", -1);
        That(BattlePassTayberrsClear.Matches(exact) && !BattlePassTayberrsClear.Matches(exact with { Id = 49 }) &&
            !BattlePassTayberrsClear.Matches(exact with { RawTypeCode = 8 }) &&
            !BattlePassTayberrsClear.Matches(exact with { RawParameter = 81 }) &&
            !BattlePassTayberrsClear.Matches(exact with { RequiredCount = 1 }) &&
            !BattlePassTayberrsClear.Matches(exact with { Experience = 100 }),
            "只匹配原raw44/ID50/4次/1000XP/parameter-1，不泛其他通关定义");
        Console.WriteLine($"PASS BattlePass Tayberrs source core ({Count - before} checks)");

        static DungeonDefinition Definition(int id) => new(id, "dungeon/legend/tayberrs/core.dgn", null, null, [], [])
            { WarriorsDungeon = "Tayberrs" };
    }

    sealed class TayberrsClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
