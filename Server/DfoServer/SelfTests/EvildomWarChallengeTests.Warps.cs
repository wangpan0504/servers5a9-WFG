using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EvildomWarChallengeTests
{
    static void RunWarpCore()
    {
        var maps = Enumerable.Range(0, 13).Select(x => new DungeonMap(x == 12 ? "boss" : "map", new(x, 0),
            400000512 + x, x == 12 ? DungeonEvildomWarChallengeWarpRules.MapPath(true) : $"map/fixture-{x}.map"))
            .Concat([new("map", new(12, 1), 400000525, DungeonEvildomWarChallengeWarpRules.MapPath(false)),
                new("map", new(12, 2), 400000526, "map/fixture-unused.map")]).ToArray();
        var boss = maps[12]; var seal = maps[13];
        var maze = new DungeonMaze(0, 13, 3, "fixture", [boss.Position], [boss.Position], maps, [])
        { WarpMaps = [new(boss.Position, seal.Position), new(seal.Position, boss.Position)] };
        var entry = new DungeonEntryPlan(99, 17, 400000105, 0, byte.MaxValue, 2, maze, boss, boss)
        {
            PartyMemberCount = 4, IndividualMapMovement = true, DisableFatigueConsumption = true,
            DisableMonsterAndClearExperience = true, BasisLevel = 135
        };
        var plans = maps.Select(map => new DungeonMonsterPlan(new(map, [], [], []), [])).ToArray();
        plans[12] = Actor(boss, 407000556, "sarpoza.mob", "action/seal_start.act", seal.Position, 10, "[boss]");
        plans[13] = Actor(seal, 407000558, "sarpoza_seal_dummy1.mob", "action/proc_0.act", boss.Position, 11, "[named]");
        var connections = Enumerable.Range(0, 12).Select(x => new DungeonRouteConnection(maps[x].Position, maps[x + 1].Position))
            .Concat([new(boss.Position, seal.Position), new(seal.Position, maps[14].Position)]).ToArray();
        var authority = new WarpCombatAuthority();
        var run = Create(entry);
        That(!run.CanMove(run.Snapshot.Identity, seal.Position), "英雄封印传送仍拒绝初始Loading");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var source = run.Snapshot.Identity;
        That(!run.CanMove(source with { Epoch = source.Epoch - 1 }, seal.Position) &&
            !run.CanMove(source with { InstanceId = Guid.NewGuid() }, seal.Position) && !run.CanMove(source, maps[14].Position),
            "英雄活怪传送拒绝旧epoch、异实例及未证12,2目标");
        var move = Guid.NewGuid();
        That(run.Move(source, move, seal.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.Snapshot.MapPayload == DungeonMapPayload.Build && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }) &&
            run.ClearFact is null && run.Move(source, move, seal.Position).Outcome == DungeonRunOutcome.Duplicate,
            "活Boss未死亡即能进入已证封印房，首次加载只创建原封印演员，重放不重建");
        That(!run.CanMove(run.Snapshot.Identity, boss.Position), "英雄封印房Loading屏障未完成不能返回");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.Snapshot.MapPayload == DungeonMapPayload.Cached && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 10 }) &&
            run.ClearFact is null, "封印活怪未清也能返回原Boss缓存，原key仍存活且不伪造死亡");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), seal.Position);
        That(run.Snapshot.MapPayload == DungeonMapPayload.Cached && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }),
            "同次Run重复封印进入保留原演员身份，不凭过图重生");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 11, 1)).Outcome == DungeonRunOutcome.Accepted &&
            !run.CanMove(run.Snapshot.Identity, maps[14].Position) && run.ClearFact is null,
            "真实封印演员死亡不能借普通Greed进入未证12,2或生成整图通关");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), seal.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.Snapshot.MapPayload == DungeonMapPayload.Cached && run.Snapshot.LivingMonsters.Count == 0,
            "真实死亡的封印演员再次进入仍沿用空缓存，不猜测客户端阶段重建或复活");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 10, 2));
        That(pending.Pending is not null && !run.CanMove(run.Snapshot.Identity, seal.Position),
            "英雄Boss死亡事务准备后不能用活怪warp越过提交");
        That(run.ConfirmCommittedDeath(pending.Pending!).ClearFact is not null &&
            !run.CanMove(run.Snapshot.Identity, seal.Position), "返回后原Boss真实死亡仍生成唯一正式通关，结算阶段不再传送");

        foreach (byte count in new byte[] { 1, 2, 3 })
        {
            var smaller = Create(entry with { PartyMemberCount = count });
            smaller.CompleteLoading(smaller.Snapshot.Identity, Guid.NewGuid());
            That(smaller.Move(smaller.Snapshot.Identity, Guid.NewGuid(), seal.Position).Outcome == DungeonRunOutcome.Accepted,
                "一至三人英雄真实队伍同样支持Boss进入封印房：" + count);
            smaller.CompleteLoading(smaller.Snapshot.Identity, Guid.NewGuid());
            That(smaller.Move(smaller.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted,
                "一至三人英雄队伍返回同一Boss缓存：" + count);
        }
        foreach (var invalid in new[] { entry with { DungeonId = 5422 }, entry with { IndividualMapMovement = false },
            entry with { PartyMemberCount = 0 }, entry with { PartyMemberCount = 5 },
            entry with { Difficulty = 1 }, entry with { DisableFatigueConsumption = false },
            entry with { Maze = maze with { WarpMaps = [] } } })
        {
            var other = Create(invalid);
            other.CompleteLoading(other.Snapshot.Identity, Guid.NewGuid());
            That(!other.CanMove(other.Snapshot.Identity, seal.Position), "其他副本及缺准入或资源声明不能借活Boss传送");
        }
        foreach (var changed in new[]
        {
            plans[12] with { Room = plans[12].Room with { EvildomWarChallengeWarp = null } },
            plans[12] with { Room = plans[12].Room with { EvildomWarChallengeWarp = plans[12].Room.EvildomWarChallengeWarp! with
                { ActionPath = "monster/fixture.act" } } }
        })
        {
            var otherPlans = plans.ToArray(); otherPlans[12] = changed;
            var other = Create(entry, otherPlans);
            other.CompleteLoading(other.Snapshot.Identity, Guid.NewGuid());
            That(!other.CanMove(other.Snapshot.Identity, seal.Position), "仅Boss编号和静态边不足以授予英雄warp，必须匹配ACT来源证据");
        }

        SinglePlayerDungeonRun Create(DungeonEntryPlan planned, DungeonMonsterPlan[]? rooms = null)
        {
            That(SinglePlayerDungeonRun.TryCreate(planned, 1, 100, new(rooms ?? plans, connections), authority,
                out var result, out _), "构造英雄两房合成批准路线");
            return result!;
        }
        static DungeonMonsterPlan Actor(DungeonMap map, int id, string mob, string action, DungeonPoint target, ushort key, string rank)
        {
            string path = DungeonEvildomWarChallengeWarpRules.Root + mob;
            var source = new DungeonRoomMonster(id, [id, 0, 135, 100, 100, 0, 1, 1], "[fixed]", rank, path);
            var room = new DungeonRoomDefinition(map, [source], [], [])
            { EvildomWarChallengeWarp = new(map.Id, map.Path, 0, id, path, DungeonEvildomWarChallengeWarpRules.Root + action, target) };
            return DungeonMonsterPlan.FromRoom(room, 99, key, 135);
        }
    }

    static void RunWarpResources(PvfArchive pvf)
    {
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var maps = catalog.Get(400000105).Mazes.Single().MapSpecification!;
        foreach (int id in new[] { 400000524, 400000525 })
        {
            var room = rooms.Get(maps.Single(map => map.Id == id));
            That(room.EvildomWarChallengeWarp is { } evidence && evidence.MapId == id && evidence.MapPath == room.Map.Path &&
                evidence.MonsterId == (id == 400000524 ? 407000556 : 407000558),
                "候选真实MAP/MOB直接ACT含精确英雄往返行为、条件和来源：" + id);
            var original = room.EvildomWarChallengeWarp!;
            var action = pvf.ReadTokens(original.ActionPath).ToArray();
            int warp = Array.FindIndex(action, token => token.Type == 3 &&
                pvf.ResolveString(token.Value) == (id == 400000524 ? "[MOVE MAP]" : "[KICK OUT MAP CHARACTER]"));
            That(warp >= 0, "候选英雄传送token实际存在");
            action[warp + 2] = new(0, 2);
            That(DungeonEvildomWarChallengeWarpRules.Read(room,
                path => path == original.ActionPath ? action : pvf.ReadTokens(path), pvf.ResolveString) is null,
                "更改真实ACT目标后不能沿旧ID继续授予传送证据");
        }
    }

    sealed class WarpCombatAuthority : DungeonCombatAuthority
    {
        internal ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key, long sequence) =>
            ConfirmDeath(identity, Guid.NewGuid(), key, sequence);
    }
}
