using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EvildomWarChallengeTests
{
    static void RunSpawnCore()
    {
        foreach (uint childId in new uint[] { 407000557, 407000559 })
        {
            uint[] fields = [FiendwarSpawnRequests.MagicV2, 1, 400000105,
                childId == 407000559 ? 400000524u : 400000525u, 123,
                childId == 407000559 ? 12u : 268u, 1, 10, childId, 20];
            byte[] wire = new byte[48];
            for (int index = 0; index < fields.Length; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(index * 4), fields[index]);
            BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(40), 7);
            var report = FiendwarSpawnRequests.Parse(wire);
            That(report.Valid && report.MonsterId == childId && report.LeaseId == 7 &&
                report.Spawn == new FiendwarDynamicSpawn(10, checked((int)childId), 20),
                "英雄两个精确子模板由真实FWS2解析后通过形状检查，不在Session入口提前拒绝");
            foreach (var invalid in new[] { report with { Serial = 0 }, report with { Sequence = 0 },
                report with { Position = 65536 }, report with { ParentKey = 0 }, report with { ParentKey = 65536 },
                report with { ObjectKey = 0 }, report with { ObjectKey = ushort.MaxValue },
                report with { ObjectKey = report.ParentKey }, report with { MonsterId = 407000556 },
                report with { MonsterId = 407000558 }, report with { MonsterId = 407000560 },
                report with { MonsterId = 109008116 } })
                That(!invalid.Valid, "英雄新增模板不放宽零身份、越界键、父子撞键及相邻未授权模板");
        }
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
        plans[12] = Actor(boss, 407000556, 407000559, "sarpoza.mob", "sarpoza_seal_dummy2.mob", "action/seal.act", 10);
        plans[13] = Actor(seal, 407000558, 407000557, "sarpoza_seal_dummy1.mob", "sarpoza_seal.mob", "action/proc_0.act", 11);
        var connections = Enumerable.Range(0, 12).Select(x => new DungeonRouteConnection(maps[x].Position, maps[x + 1].Position))
            .Concat([new(boss.Position, seal.Position), new(seal.Position, maps[14].Position)]).ToArray();
        var authority = new WarpCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, new(plans, connections), authority,
            out var created, out _), "构造英雄动态演员固定路线");
        var run = created!;
        run.BindFiendwarObjectKeys(new(run.Snapshot.Identity.InstanceId));
        var first = new FiendwarDynamicSpawn(10, 407000559, 20);
        That(run.CanRegisterFiendwarDynamic(run.Snapshot.Identity, first) == DungeonRunOutcome.Rejected,
            "英雄动态登记不能越过Loading");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var original = run.Snapshot.Identity;
        foreach (var invalid in new[] { first with { ObjectKey = 10 }, first with { ObjectKey = 99 },
            first with { ObjectKey = ushort.MaxValue }, first with { ParentKey = 11 }, first with { MonsterId = 407000557 } })
            That(run.CanRegisterFiendwarDynamic(original, invalid) == DungeonRunOutcome.Rejected,
                "英雄拒绝静态或角色碰撞、哨兵、跨房父子");
        That(run.RegisterFiendwarDynamic(original, first) == DungeonRunOutcome.Accepted &&
            run.RegisterFiendwarDynamic(original, first) == DungeonRunOutcome.Duplicate &&
            run.RegisterFiendwarDynamic(original, first with { ObjectKey = 21 }) == DungeonRunOutcome.Accepted,
            "559保留真实逐key对象，不捏造资源没有声明的单活上限");
        That(run.Move(original, Guid.NewGuid(), seal.Position).Outcome == DungeonRunOutcome.Accepted,
            "活559可随原Boss缓存保留，真实warp不把动态对象当清房门禁");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.FindCurrentFiendwarDynamic(run.Snapshot.Identity, first.ObjectKey) is null,
            "Boss房动态对象在封印房不可被跨房39退休");
        var child = new FiendwarDynamicSpawn(11, 407000557, 22);
        That(run.RegisterFiendwarDynamic(run.Snapshot.Identity, child) == DungeonRunOutcome.Accepted &&
            run.CanRegisterFiendwarDynamic(run.Snapshot.Identity, child with { ObjectKey = 23 }) == DungeonRunOutcome.Rejected,
            "封印557的CHECKED NO0只允许当前同父一个活对象");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.FindCurrentFiendwarDynamic(run.Snapshot.Identity, 20) == first &&
            run.CanRetireCurrentFiendwarDynamic(authority.Death(original, 20, 1)) == DungeonRunOutcome.Rejected &&
            run.RetireCurrentFiendwarDynamic(authority.Death(run.Snapshot.Identity, 20, 2)) == DungeonRunOutcome.Accepted &&
            run.ClearFact is null && run.Snapshot.LivingMonsters.Contains((ushort)10),
            "回原Boss缓存后活动态对象按当前epoch退休，旧epoch拒绝且Boss原key继续存活");
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), seal.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.FindCurrentFiendwarDynamic(run.Snapshot.Identity, 22) == child &&
            run.CanRegisterFiendwarDynamic(run.Snapshot.Identity, child with { ObjectKey = 23 }) == DungeonRunOutcome.Rejected,
            "回封印缓存仍识别原557活跃槽，不因epoch变化允许第二只");
        var death = authority.Death(run.Snapshot.Identity, 22, 3);
        That(run.RetireCurrentFiendwarDynamic(death) == DungeonRunOutcome.Accepted &&
            run.RetireCurrentFiendwarDynamic(death) == DungeonRunOutcome.Duplicate &&
            run.RegisterFiendwarDynamic(run.Snapshot.Identity, child) == DungeonRunOutcome.Duplicate &&
            run.FindCurrentFiendwarDynamic(run.Snapshot.Identity, 22) is null &&
            !run.CanLeaseFiendwarObjectKey(22), "动态退休幂等，原key保留墓碑且不能复活或重新出租");
        for (ushort key = 23; key < 31; key++)
        {
            var next = child with { ObjectKey = key };
            That(run.RegisterFiendwarDynamic(run.Snapshot.Identity, next) == DungeonRunOutcome.Accepted &&
                run.RetireCurrentFiendwarDynamic(authority.Death(run.Snapshot.Identity, key, key)) == DungeonRunOutcome.Accepted,
                "封印557可按原CHECKED NO0连续多轮，不设任意小轮数上限");
        }
        That(run.ClearFact is null && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 11 }),
            "动态557退休不杀隐形558、不生成Boss通关事实");

        static DungeonMonsterPlan Actor(DungeonMap map, int parent, int child, string mob, string childMob, string action, ushort key)
        {
            string root = DungeonEvildomWarChallengeWarpRules.Root;
            var source = new DungeonRoomMonster(parent, [parent, 0, 135, 100, 100, 0, 1, 1], "[fixed]", map.Kind == "boss" ? "[boss]" : "[normal]", root + mob);
            var room = new DungeonRoomDefinition(map, [source], [], [])
            {
                EvildomWarChallengeWarp = new(map.Id, map.Path, 0, parent, root + mob,
                    root + (map.Kind == "boss" ? "action/seal_start.act" : "action/proc_0.act"), new(12, map.Kind == "boss" ? 1 : 0)),
                EvildomWarChallengeSpawn = new(0, parent, root + mob, child, root + childMob, root + action, child == 407000557)
            };
            return DungeonMonsterPlan.FromRoom(room, 99, key, 135);
        }
    }

    static void RunSpawnResources(PvfArchive pvf)
    {
        var catalog = SelfTestCatalogs.Dungeons(pvf); var rooms = SelfTestCatalogs.Rooms(pvf);
        var maps = catalog.Get(400000105).Mazes.Single().MapSpecification!;
        var paths = DungeonCatalog.ParseDirectory(pvf.ReadTokens("monster/monster.lst"), pvf.ResolveString, "monster");
        foreach (int id in new[] { 400000524, 400000525 })
        {
            var room = rooms.Get(maps.Single(map => map.Id == id));
            That(room.EvildomWarChallengeSpawn is { } evidence && evidence.ParentId == (id == 400000524 ? 407000556 : 407000558) &&
                evidence.ChildId == (id == 400000524 ? 407000559 : 407000557) && evidence.SingleActive == (id == 400000525),
                "候选真实MOB直接ACT、SUMMON全参数及557零数量trigger授予精确动态来源：" + id);
            var source = room.EvildomWarChallengeSpawn!;
            var action = pvf.ReadTokens(source.ActionPath).ToArray();
            int summon = Array.FindIndex(action, token => token.Type == 3 && pvf.ResolveString(token.Value) == "[SUMMON MONSTER]");
            That(summon >= 0, "候选直接ACT确有SUMMON token");
            action[summon + 2] = new(0, source.ChildId + 1);
            That(DungeonEvildomWarChallengeSpawnRules.Read(room, path => path == source.ActionPath ? action : pvf.ReadTokens(path),
                pvf.ResolveString, value => paths.GetValueOrDefault(value)) is null,
                "修改直接SUMMON子模板后不能沿父编号继续授予来源");
        }
    }
}
