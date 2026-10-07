using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Development;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;

namespace DfoServer;

internal static class NpcTravelSpawnTests
{
    public static void Run(TownCatalog towns, CharacterPreparation preparation)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value)
                throw new Exception("FAIL NPC/spawn: " + message);
        }

        var completed = preparation.CompletedQuests.ToHashSet();
        var seriaRoom = towns.Areas[(towns.Room.Town, towns.Room.Area)];
        Check(seriaRoom.Map(new HashSet<int>()).Npcs.Any(n => n.Id == 2)
            && seriaRoom.Map(new HashSet<int> { 3149 }).Npcs.Any(n => n.Id == 2),
            "Seria room keeps NPC 2 in the map list before and after quest 3149");
        var request = TownRequests.Area(Convert.FromHexString("76007B01E500053C0001003E84D71700"));
        var gray = new Location(60, 1, 1110, 257);
        var grayDoor = new Location(60, 1, 1241, 139);
        var prey = new Location(118, 0, 379, 229);
        Check(request.TargetNpc == 400000062 && request.PathKind == 0 && request.PreviousTown == 60 && request.PreviousArea == 1, "actual disconnect packet retains target NPC u32");
        Check(towns.NpcMoves[8001].SequenceEqual(new uint[] { 400000062 }) && towns.NpcMoves[400000062].SequenceEqual(new uint[] { 8001 }), "matching Arthur NPC roles define both directions");
        Check(towns.Admission(gray, prey, 95, completed, targetNpc: 400000062) == "ready", "actual NPC landing succeeds without a map door");
        Check(towns.Admission(gray, prey, 94, completed, targetNpc: 400000062) == "target-level-required", "NPC teleport retains level requirement");
        Check(towns.Admission(gray, prey, 95, completed.Except(new[] { 6211, 6219 }).ToHashSet(), targetNpc: 400000062) == "target-quest-required", "NPC teleport retains either completed quest requirement");
        Check(towns.Admission(towns.InitialReturn, prey, 95, completed, targetNpc: 400000062) == "npc-route-not-in-pvf", "target NPC cannot authorize a route from unrelated source");
        Check(towns.Admission(gray, new(54, 1, 201, 570), 95, completed, targetNpc: 400000062) == "npc-target-not-in-area", "target NPC must be in the requested map and phase");
        Check(towns.Admission(gray, prey with { X = 380 }, 95, completed, targetNpc: 400000062) == "ready", "NPC travel is not restricted to inferred npc+30 coordinates");
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
            Check(selected.Position == new Location(38, 1, 450, 234) && selected.Return == expected && stored.Position == selected.Position && stored.Return == expected && stored.Revision == before.Revision + 1, $"login resets {position.Town}/{position.Area} to birth point with district {expected.Town}/{expected.Area}");
            Check(stored.Level == 95 && stored.GrowType == 0x25 && stored.Skills.SequenceEqual(preparation.Skills) && stored.CompletedQuests.SequenceEqual(preparation.CompletedQuests) && stored.AcceptedStoryLevel == 95, "spawn reset preserves progression");
            session.MarkReady();
            Check(session.LeaveRoom() && session.Selected!.Position == expected, "bottom exit uses fixed district door rather than logout position");
        }

        repo.Save(owner, repo.List(owner).Single(), gray, null);
        var live = Select();
        live.MarkReady();
        Check(live.LeaveRoom(), "gray room exits before teleport");
        var beforeNpc = live.Selected!;
        Check(!live.Travel(request with { TargetNpc = uint.MaxValue }) && live.Selected == beforeNpc && repo.List(owner).Single().Revision == beforeNpc.Revision, "unknown NPC is rejected without a write");
        Check(live.Travel(request) && live.Selected!.Position == prey && live.Selected.Return == grayDoor, "exact disconnecting request commits NPC landing and retains gray district");
        var actualReturn = TownRequests.Area(Convert.FromHexString("3C0174045A000576000000411F000000"));
        Check(live.Travel(actualReturn with { PathKind = 255 }) && live.Selected!.Position == new Location(60, 1, 1140, 90), "actual reverse Arthur request preserves 1140,90 despite npc/static walking mismatch");
        Check(live.Travel(request with { X = 65535, Y = 65535 }) && live.Selected!.Position == new Location(118, 0, 65535, 65535), "NPC travel does not clamp the unsigned coordinate range");
        var beforeFail = repo.List(owner).Single();
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

        Check(thrown && failed.Selected is null && !failed.Ready && repo.List(owner).Single().Revision == beforeFail.Revision, "failed spawn commit does not publish selection or advance revision");
        Execute("DROP TRIGGER fail_spawn;");
        var normalized = Select().Selected!;
        var repeat = Select().Selected!;
        Check(normalized.Revision == repeat.Revision && repeat.Position == towns.Room, "already normalized spawn does not write again");
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

        Check(stale, "stale owner cannot reset persisted spawn");
        Console.WriteLine($"PASS {checks} NPC travel and login-spawn assertions");
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
