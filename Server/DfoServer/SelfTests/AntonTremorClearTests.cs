using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonTremorClearTests
{
    internal static void Run()
    {
        int before = Count;
        foreach (int dungeon in new[] { 212, 214 })
        foreach (bool leftFirst in new[] { true, false })
        {
            var (run, authority, plans) = Create(dungeon);
            var start = run.Snapshot.CurrentMap.Position;
            var boss = run.Entry.BossMap.Position;
            That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss).Outcome == DungeonRunOutcome.Rejected,
                "两侧尚未击杀时拒绝直走上方装置房，避免卡在不可战斗终点");
            var sides = leftFirst ? new[] { plans[1], plans[0] } : new[] { plans[0], plans[1] };
            for (int index = 0; index < sides.Length; index++)
            {
                var plan = sides[index];
                Move(plan.Room.Map.Position);
                var death = authority.Death(run.Snapshot.Identity, plan.Monsters[0].ObjectKey);
                var prepared = run.PrepareConfirmedDeath(death).Pending!;
                That(prepared.Result.ClearFact is null && run.ClearFact is null,
                    "左右真实精英死亡不在支路结算；规划也不提前生成终末事实");
                That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), start).Outcome == DungeonRunOutcome.Rejected,
                    "死亡尚未提交不能离房或越过死亡事务");
                run.ConfirmCommittedDeath(prepared);
                That(run.AcceptConfirmedDeath(death).Outcome == DungeonRunOutcome.Duplicate,
                    "重复精英死亡不重复计算清图资格");
                Move(start);
                if (index == 0)
                    That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss).Outcome == DungeonRunOutcome.Rejected,
                        "只杀一侧仍不能进入终点");
            }
            That(run.ClearFact is null && !run.HasCommittedAntonTremorClear, "双精英死亡仍须玩家实际加载上方终点");
            That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss).Outcome == DungeonRunOutcome.Accepted,
                "两次已提交击杀放行上方终点");
            var identity = run.Snapshot.Identity;
            var action = Guid.NewGuid();
            var loaded = run.CompleteLoading(identity, action);
            That(loaded.ClearFact is null && !run.HasCommittedAntonTremorClear &&
                loaded.Snapshot.LivingMonsters.Contains(plans[3].Monsters[0].ObjectKey),
                "终点加载不能提前清图或显示菜单，仍等待炮击销毁指引物件");
            That(run.CompleteLoading(identity, action).Outcome == DungeonRunOutcome.Duplicate &&
                run.ClearFact == loaded.ClearFact, "重复加载重放同一清图事实");
            That(run.PreparePassiveClear(run.Snapshot.Identity, Guid.NewGuid(), 15470, 0) is null,
                "无线电兵或其它物件不能代替炮击指引终末");
            var pending = run.PreparePassiveClear(run.Snapshot.Identity, Guid.NewGuid(), 15467, 0);
            That(pending is not null && run.ClearFact is null, "物件报告只准备清图，提交前不能许可菜单");
            var cleared = run.ConfirmCommittedPassiveClear(pending!);
            That(cleared.ClearFact is { ConfirmedDeathEvents.Count: 2, PassiveSource.TemplateId: 15467 } &&
                run.HasCommittedAntonTremorClear, "炮击销毁指引提交后以双精英事实通关");
            That(run.ConfirmCommittedPassiveClear(pending!).Outcome == DungeonRunOutcome.Duplicate,
                "重复物件报告不重复结算");
            That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), start).Outcome == DungeonRunOutcome.Rejected,
                "结算后旧Run不能继续推进或重刷资格");

            void Move(DungeonPoint target)
            {
                That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), target).Outcome == DungeonRunOutcome.Accepted,
                    "实际支路及已清起点沿原门墙移动");
                That(run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                    "每次访问独立完成加载");
            }
        }
        foreach (int dungeon in new[] { 212, 214 })
        foreach (bool leftFirst in new[] { true, false })
        {
            var (run, authority, plans) = Create(dungeon, officialEmpty: true);
            var start = run.Snapshot.CurrentMap.Position;
            var boss = run.Entry.BossMap.Position;
            That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss).Outcome == DungeonRunOutcome.Rejected,
                "官方空条件下两侧尚未击杀时仍拒绝直走上方终点");
            var sides = leftFirst ? new[] { plans[1], plans[0] } : new[] { plans[0], plans[1] };
            for (int index = 0; index < sides.Length; index++)
            {
                var plan = sides[index];
                MoveOfficial(plan.Room.Map.Position);
                var death = authority.Death(run.Snapshot.Identity, plan.Monsters[0].ObjectKey);
                var prepared = run.PrepareConfirmedDeath(death).Pending!;
                That(prepared.Result.ClearFact is null && run.ClearFact is null,
                    "官方空条件下左右精英死亡仍不在支路结算");
                run.ConfirmCommittedDeath(prepared);
                MoveOfficial(start);
                if (index == 0)
                    That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss).Outcome == DungeonRunOutcome.Rejected,
                        "官方空条件下只杀一侧仍不能进入终点");
            }
            That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss).Outcome == DungeonRunOutcome.Accepted,
                "官方空条件下两次已提交击杀放行上方终点");
            var identity = run.Snapshot.Identity;
            var action = Guid.NewGuid();
            var loaded = run.CompleteLoading(identity, action);
            That(loaded.ClearFact is { ConfirmedDeathEvents.Count: 2 } && run.HasCommittedAntonTremorClear &&
                loaded.Snapshot.LivingMonsters.Contains(plans[3].Monsters[0].ObjectKey) &&
                loaded.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement,
                "官方空条件在双精英提交后加载终点即通关，不击杀69308、不等待15467");
            That(run.CompleteLoading(identity, action).Outcome == DungeonRunOutcome.Duplicate &&
                run.ClearFact == loaded.ClearFact, "官方空条件重复加载重放同一清图事实");
            That(run.PreparePassiveClear(run.Snapshot.Identity, Guid.NewGuid(), 15467, 0) is null,
                "官方空条件不绑定15467，加载后的物件报告不能当作唯一通关门");

            void MoveOfficial(DungeonPoint target)
            {
                That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), target).Outcome == DungeonRunOutcome.Accepted,
                    "官方空条件支路沿原门墙移动");
                That(run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                    "官方空条件每次访问独立完成加载");
            }
        }
        foreach (var kind in new[] { RaidKind.Luke, RaidKind.Fiendwar })
        {
            var (run, _, _) = Create(212, kind);
            That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), run.Entry.BossMap.Position).Outcome == DungeonRunOutcome.Accepted &&
                run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).ClearFact is null,
                "非安徒恩准入不启用震颤特殊规则");
        }
        Console.WriteLine($"PASS {Count - before} 安徒恩震颤双支路通关断言");
    }

    static (SinglePlayerDungeonRun Run, Authority Authority, DungeonMonsterPlan[] Plans) Create(int dungeon, RaidKind kind = RaidKind.Anton, bool officialEmpty = false)
    {
        int first = dungeon == 212 ? 32220 : 32240;
        var maps = new[] { new DungeonMap("map", new(2, 1), first, "map/tremor-right.map"),
            new DungeonMap("map", new(0, 1), first + 1, "map/tremor-left.map"),
            new DungeonMap("map", new(1, 1), first + 3, "map/tremor-start.map"),
            new DungeonMap("boss", new(1, 0), first + 2, "map/tremor-terminal.map") };
        var maze = new DungeonMaze(0, 3, 2, "AAIIAABBHHEE", [maps[2].Position], [maps[3].Position], maps, [])
            { ClearConditions = officialEmpty ? [] : [new("destroy object", 15467, 1)] };
        var entry = new DungeonEntryPlan(99, 2, checked((uint)dungeon), 0, byte.MaxValue, 2, maze, maps[2], maps[3])
            { RaidEntry = new(Guid.NewGuid(), 0, 1, kind) };
        DungeonRoomMonster Monster(int id, string rank) => new(id, [id, 0, 20, 0, 0, 0, 1, 1], "[fixed]", rank, "monster/tremor.mob");
        var plans = maps.Select((map, index) => DungeonMonsterPlan.FromRoom(new(map,
            index == 2 ? [] : [Monster(index == 0 ? 59055 : index == 1 ? 56635 : 69308, index == 3 ? "[boss]" : "[normal]")], [], [])
            { PassiveObjects = index == 3 ? [new(15467, [15467, 630, 303, 0], "passiveobject/tremor.obj")] : [] },
            99, checked((ushort)(10 + index)))).ToArray();
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new(plans,
            maps.Where(map => map != maps[2]).Select(map => new DungeonRouteConnection(maps[2].Position, map.Position)).ToArray())
            { PassiveClearTarget = officialEmpty ? null : 15467 },
            authority, out var run, out _), "四房正式Run创建成功");
        run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        return (run, authority, plans);
    }

    sealed class Authority : DungeonCombatAuthority
    {
        long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) =>
            ConfirmDeath(identity, Guid.NewGuid(), key, ++sequence);
    }
}
