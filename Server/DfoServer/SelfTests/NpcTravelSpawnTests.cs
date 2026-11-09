using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Development;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using PvfLib;

namespace DfoServer;

internal static class NpcTravelSpawnTests
{
    public static void Run(TownCatalog towns, CharacterPreparation preparation, PvfArchive pvf)
    {
        int checks = Count;

        var completed = preparation.CompletedQuests.ToHashSet();
        var seriaRoom = towns.Areas[(towns.Room.Town, towns.Room.Area)];
        That(seriaRoom.Map(new HashSet<int>()).Npcs.Any(n => n.Id == 2)
            && seriaRoom.Map(new HashSet<int> { 3149 }).Npcs.Any(n => n.Id == 2),
            "Seria room keeps NPC 2 in the map list before and after quest 3149");
        var request = TownRequests.Area(Convert.FromHexString("76007B01E500053C0001003E84D71700"));
        var gray = new Location(60, 1, 1110, 257);
        var grayDoor = new Location(60, 1, 1241, 139);
        var prey = new Location(118, 0, 379, 229);
        That(request.TargetNpc == 400000062 && request.PathKind == 0 && request.PreviousTown == 60 && request.PreviousArea == 1, "actual disconnect packet retains target NPC u32");
        That(towns.NpcMoves[8001].SequenceEqual(new uint[] { 400000062 }) && towns.NpcMoves[400000062].SequenceEqual(new uint[] { 8001 }), "matching Arthur NPC roles define both directions");
        That(towns.Admission(gray, prey, 95, completed, targetNpc: 400000062) == "ready", "actual NPC landing succeeds without a map door");
        That(towns.Admission(gray, prey, 94, completed, targetNpc: 400000062) == "target-level-required", "NPC teleport retains level requirement");
        That(towns.Admission(gray, prey, 95, completed.Except(new[] { 6211, 6219 }).ToHashSet(), targetNpc: 400000062) == "target-quest-required", "NPC teleport retains either completed quest requirement");
        That(towns.Admission(towns.InitialReturn, prey, 95, completed, targetNpc: 400000062) == "npc-route-not-in-pvf", "target NPC cannot authorize a route from unrelated source");
        That(towns.Admission(gray, new(54, 1, 201, 570), 95, completed, targetNpc: 400000062) == "npc-target-not-in-area", "target NPC must be in the requested map and phase");
        That(towns.Admission(gray, prey with { X = 380 }, 95, completed, targetNpc: 400000062) == "ready", "NPC travel is not restricted to inferred npc+30 coordinates");
        var weekly = new Location(107, 0, 400, 250);
        That(towns.NpcMoves[400001083].SequenceEqual(new uint[] { 400001081 }) &&
            towns.NpcMoves[400001082].SequenceEqual(new uint[] { 400001081 }),
            "每周猫咪与巴斯泰托的 enter town 读取原目标 NPC，不硬编码路线");
        That(towns.Admission(towns.Room, weekly, 50, new HashSet<int> { 8709 }, targetNpc: 400001081) == "ready",
            "已完成8709的50级角色可从实际猫咪地图进入遗迹");
        That(towns.Admission(towns.Room, weekly, 49, new HashSet<int> { 8709 }, targetNpc: 400001081) == "target-level-required" &&
            towns.Admission(towns.Room, weekly, 50, new HashSet<int>(), targetNpc: 400001081) == "target-quest-required",
            "enter town 不跳过50级或8709前置");
        That(towns.Admission(gray, weekly, 95, new HashSet<int> { 8709 }, targetNpc: 400001081) == "npc-route-not-in-pvf" &&
            towns.Admission(towns.Room, prey, 95, completed, targetNpc: 400001081) == "npc-target-not-in-area",
            "enter town 继续绑定来源NPC与目标实际区域，不能任意传送");
        That(towns.NpcExits.Contains(400001081) && towns.HasNpcExit(weekly, new HashSet<int> { 8709 }),
            "遗迹巴斯泰托 exit town 0 保留为返回入口，不伪造目标NPC 0");
        string path = Path.Combine(Path.GetTempPath(), "S5A9-npc-spawn-tests-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("npc-spawn", () =>
        {
        });
        repo.Create(owner, preparation.ToCharacter("Spawn95", towns));
        foreach (var (position, remembered, expected) in new (Location, Location?, Location)[]
        {
            (new(38, 0, 1051, 239, 3), null, towns.InitialReturn),
            (new(39, 2, 552, 218), null, new(39, 0, 596, 144)),
            (new(60, 3, 600, 350), null, grayDoor),
            (new(38, 1, 873, 253, 2), gray, grayDoor),
            (prey, gray, grayDoor),
            (prey, null, grayDoor),
            (new(60, 1, 65535, 65535), null, grayDoor)
        })
        {
            var before = repo.Save(owner, repo.List(owner).Single(), position, remembered);
            var session = Select();
            var selected = session.Selected!;
            var stored = repo.List(owner).Single();
            That(selected.Position == new Location(38, 1, 450, 234) && selected.Return == expected && stored.Position == selected.Position && stored.Return == expected && stored.Revision == before.Revision + 1, $"login resets {position.Town}/{position.Area} to birth point with district {expected.Town}/{expected.Area}");
            That(stored.Level == 95 && stored.GrowType == 0x25 && stored.Skills.SequenceEqual(preparation.Skills) && stored.CompletedQuests.SequenceEqual(preparation.CompletedQuests) && stored.AcceptedStoryLevel == 95, "spawn reset preserves progression");
            session.MarkReady();
            That(session.LeaveRoom() && session.Selected!.Position == expected, "bottom exit uses fixed district door rather than logout position");
        }

        repo.Save(owner, repo.List(owner).Single(), gray, null);
        var live = Select();
        live.MarkReady();
        That(live.LeaveRoom(), "gray room exits before teleport");
        var beforeNpc = live.Selected!;
        That(!live.Travel(request with { TargetNpc = uint.MaxValue }) && live.Selected == beforeNpc && repo.List(owner).Single().Revision == beforeNpc.Revision, "unknown NPC is rejected without a write");
        That(live.Travel(request) && live.Selected!.Position == prey && live.Selected.Return == grayDoor, "exact disconnecting request commits NPC landing and retains gray district");
        var actualReturn = TownRequests.Area(Convert.FromHexString("3C0174045A000576000000411F000000"));
        That(live.Travel(actualReturn with { PathKind = 255 }) && live.Selected!.Position == new Location(60, 1, 1140, 90), "actual reverse Arthur request preserves 1140,90 despite npc/static walking mismatch");
        That(live.Travel(request with { X = 65535, Y = 65535 }) && live.Selected!.Position == new Location(118, 0, 65535, 65535), "NPC travel does not clamp the unsigned coordinate range");
        var beforeFail = repo.List(owner).Single();
        // 实际换频道请求：123/0仍在客户端快照中，但新Session没有旧NPC入场票据。
        var rift = new Location(123, 0, 1190, 164, 5);
        var roomReturn = new AreaRequest(38, 1, 493, 219, 5, 123, 0, 0, 0);
        repo.Save(owner, beforeFail, rift, towns.InitialReturn);
        var riftTowns = TownCatalog.Load(pvf, EpicRoad2019NpcRestoreTests.Geometry(pvf), epicRoad2019Enabled: true);
        var resumedRift = new GameSession(repo, riftTowns, owner);
        resumedRift.Select(0, resumeTown: true);
        resumedRift.MarkReady();
        var riftBefore = resumedRift.Selected!;
        That(resumedRift.TownSnapshotResumed && riftBefore.Position == rift,
            "换频道保留超星空入口地图，不能以登录重置掩盖返回缺陷");
        foreach (var invalid in new[] { roomReturn with { Town = 39, Area = 0 },
            roomReturn with { X = ushort.MaxValue, Y = ushort.MaxValue }, roomReturn with { PreviousTown = 38 },
            roomReturn with { TargetNpc = uint.MaxValue }, roomReturn with { Movement = 4 }, roomReturn with { PathKind = 1 } })
            That(!resumedRift.Travel(invalid) && resumedRift.Selected == riftBefore &&
                repo.List(owner).Single().Revision == riftBefore.Revision,
                "恢复NPC返回仍拒绝伪造来源、目的、坐标和请求形状，且不写库");
        That(resumedRift.Travel(roomReturn) && resumedRift.Selected!.Position == new Location(38, 1, 493, 219, 5) &&
            resumedRift.Selected.Return == towns.InitialReturn &&
            repo.List(owner).Single().Position == resumedRift.Selected.Position,
            "丢失NPC临时票据后按原资源双向路线返回房间并保存，保留街区返回点");
        beforeFail = repo.List(owner).Single();
        Execute("CREATE TRIGGER fail_spawn BEFORE UPDATE ON characters BEGIN SELECT RAISE(ABORT,'spawn-test'); END;");
        var failed = new GameSession(repo, towns, owner);
        bool thrown = false;
        try
        {
            failed.Select(0);
        }
        catch (SqliteException)
        {
            thrown = true;
        }

        That(thrown && failed.Selected is null && !failed.Ready && repo.List(owner).Single().Revision == beforeFail.Revision, "failed spawn commit does not publish selection or advance revision");
        Execute("DROP TRIGGER fail_spawn;");
        var normalized = Select().Selected!;
        var repeat = Select().Selected!;
        That(normalized.Revision == repeat.Revision && repeat.Position == towns.Room, "already normalized spawn does not write again");
        using var newer = owners.Acquire("npc-spawn", () =>
        {
        });
        bool stale = false;
        try
        {
            Select();
        }
        catch (InvalidOperationException)
        {
            stale = true;
        }

        That(stale, "stale owner cannot reset persisted spawn");
        Console.WriteLine($"PASS {Count - checks} NPC travel and login-spawn assertions");
        GameSession Select()
        {
            var s = new GameSession(repo, towns, owner);
            s.Select(0);
            return s;
        }

        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
