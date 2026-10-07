using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Development;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;

namespace DfoServer;

internal static class DockTravelTests
{
    public static void Run(TownCatalog towns, CharacterPreparation preparation)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value)
                throw new Exception("FAIL dock: " + message);
        }

        var completed = preparation.CompletedQuests.ToHashSet();
        var dock = towns.Dock;
        var stops = dock.Active(completed);
        Check(dock.OpenQuest == 3427 && dock.Stops.Length == 8 && stops.Select(s => (s.Point.Town, s.Point.Area, s.Point.X, s.Point.Y, s.Level, s.Quest)).SequenceEqual(new (byte, byte, ushort, ushort, int, int)[] { (40, 1, 1672, 200, 50, 3427), (11, 1, 315, 215, 1, 3616), (17, 1, 1409, 346, 1, 3427), (6, 3, 1668, 213, 1, 3515), (22, 3, 77, 256, 85, 3779), (31, 1, 2508, 220, 86, 4840), (54, 1, 201, 570, 90, 5936) }), "all seven current Client stops, including Harlem and changed West Coast coordinate: " + System.Text.Json.JsonSerializer.Serialize(stops));
        var afterHeaven = new HashSet<int>(completed)
        {
            6359
        };
        Check(!completed.Contains(6359) && stops.Single(s => s.Region == 3).Point.Town == 17 && dock.Active(afterHeaven).Single(s => s.Region == 3).Point.Town == 70, "optional quest6359 replaces Saint Horn only if present, not from Lv95 alone");
        var west = new Location(40, 1, 1675, 151);
        var harlem = new Location(54, 1, 201, 570);
        Check(stops.All(s => towns.IsPosition(s.Point, completed)), "every current dock landing point lies in matching phase geometry");
        Check(stops.All(a => stops.Where(b => a.Region != b.Region).All(b => towns.CanTravel(a.Point, b.Point, 95, completed) && towns.Entrance(a.Point, b.Point, completed) == b.Point)), "all 42 directed dock routes keep declared landing points");
        Check(towns.CanTravel(west, harlem, 95, completed) && towns.Entrance(west, harlem, completed) == harlem, "observed West Coast to Harlem landing is 201,570, not map marker200,507");
        Check(towns.CanTravel(west, new(17, 1, 1409, 346), 95, completed) && !towns.CanTravel(west, new(17, 1, 1409, 346), 95, afterHeaven) && towns.CanTravel(west, new(70, 1, 1409, 346), 95, afterHeaven), "Saint Horn town permissions match dock phase before and after quest6359");
        Check(!towns.CanTravel(west, harlem, 95, completed.Where(q => q != 3427).ToHashSet()), "global dock quest is required");
        Check(!towns.CanTravel(west, harlem, 95, completed.Where(q => q != 5936).ToHashSet()), "Harlem dock opening quest is required");
        Check(!towns.CanTravel(stops[1].Point, harlem, 95, completed.Where(q => q != 3616).ToHashSet()), "source dock opening quest is required");
        Check(dock.Admission(west, stops[1].Point, 49, completed) == "dock-level-required" && !towns.CanTravel(west, harlem, 89, completed), "source and destination levels cannot be bypassed");
        Check(!towns.CanTravel(west, new(31, 1, 2508, 220), 86, completed), "target area level remains stricter than dock display level");
        Check(towns.Admission(west, harlem with { X = 202 }, 95, completed) == "ready", "dock coordinates do not need to equal its static portal area");
        Check(!towns.CanTravel(new(40, 0, 55, 270), harlem, 95, completed), "ordinary West Coast street cannot impersonate its dock area");
        string path = Path.Combine(Path.GetTempPath(), "S5A9-dock-tests-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("dock-test", () =>
        {
        });
        var created = repo.Create(owner, preparation.ToCharacter("Dock95", towns));
        var initial = repo.Save(owner, created, west, null);
        var session = new GameSession(repo, towns, owner);
        session.Select(0);
        session.MarkReady();
        initial = session.Selected!;
        Check(session.Travel(new(40, 1, 1782, 193, 5, 38, 1, 5)), "reselect spawns in room before choosing West Coast dock");
        initial = session.Selected!;
        var request = TownRequests.Area(Convert.FromHexString("3601C9003A0205280001000000000000"));
        Check(!session.Travel(request with { PreviousArea = 0 }) && session.TravelReason == "source-area-mismatch" && session.Selected == initial, "wrong source leaves cached scene unchanged");
        Check(!session.Travel(request with { Town = 255 }) && session.Selected == initial && repo.List(owner).Single().Revision == initial.Revision, "unknown destination does not persist or advance revision");
        Execute("CREATE TRIGGER fail_dock BEFORE UPDATE ON characters BEGIN SELECT RAISE(ABORT,'dock-test'); END;");
        bool failed = false;
        try
        {
            session.Travel(request);
        }
        catch (SqliteException)
        {
            failed = true;
        }

        Check(failed && session.Selected == initial && repo.List(owner).Single().Revision == initial.Revision, "failed database commit cannot publish a dock move");
        Execute("DROP TRIGGER fail_dock;");
        Check(session.Travel(request) && session.Selected!.Position == harlem && session.Selected.Revision == initial.Revision + 1, "exact real request now commits Harlem");
        var reopened = new GameSession(new CharacterRepository(path), towns, owner);
        reopened.Select(0);
        reopened.MarkReady();
        Check(reopened.Selected!.Position == towns.Room && reopened.Selected.Return is { Town: 54, Area: 1 } && reopened.Selected.CompletedQuests.SequenceEqual(preparation.CompletedQuests) && reopened.Selected.AcceptedStoryLevel == 95, "reselect uses Harlem's room birth point and preserves quest/CG progression");
        Check(reopened.LeaveRoom() && reopened.Selected!.Position is { Town: 54, Area: 1 }, "Harlem room exits to its street before selecting another dock");
        Check(reopened.Travel(new(40, 1, 1672, 200, 5, 54, 1, 255)) && reopened.Selected!.Position == new Location(40, 1, 1672, 200), "dock accepts full kind byte and keeps Client West Coast1672,200 rather than old-version point");
        Check(reopened.Travel(request with { X = 65000, Y = 0 }) && reopened.Selected!.Position == new Location(54, 1, 65000, 0), "dock retains request coordinates outside offline walking bounds");
        Console.WriteLine($"PASS {checks} dock assertions; active=7, directed routes=42");
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
