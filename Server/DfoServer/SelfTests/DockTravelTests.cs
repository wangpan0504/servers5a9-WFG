using static DfoServer.Testing.TestCheck;
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
        int checks = Count;

        var completed = preparation.CompletedQuests.ToHashSet();
        var dock = towns.Dock;
        var stops = dock.Active(completed);
        That(dock.OpenQuest == 3427 && dock.Stops.Length == 8 && stops.Select(s => (s.Point.Town, s.Point.Area, s.Point.X, s.Point.Y, s.Level, s.Quest)).SequenceEqual(new (byte, byte, ushort, ushort, int, int)[] { (40, 1, 1672, 200, 50, 3427), (11, 1, 315, 215, 1, 3616), (17, 1, 1409, 346, 1, 3427), (6, 3, 1668, 213, 1, 3515), (22, 3, 77, 256, 85, 3779), (31, 1, 2508, 220, 86, 4840), (54, 1, 201, 570, 90, 5936) }), "all seven current Client stops, including Harlem and changed West Coast coordinate: " + System.Text.Json.JsonSerializer.Serialize(stops));
        var afterHeaven = new HashSet<int>(completed)
        {
            6359
        };
        That(!completed.Contains(6359) && stops.Single(s => s.Region == 3).Point.Town == 17 && dock.Active(afterHeaven).Single(s => s.Region == 3).Point.Town == 70, "optional quest6359 replaces Saint Horn only if present, not from Lv95 alone");
        var west = new Location(40, 1, 1675, 151);
        var harlem = new Location(54, 1, 201, 570);
        That(stops.All(s => towns.IsPosition(s.Point, completed)), "every current dock landing point lies in matching phase geometry");
        That(stops.All(a => stops.Where(b => a.Region != b.Region).All(b => towns.CanTravel(a.Point, b.Point, 95, completed) && towns.Entrance(a.Point, b.Point, completed) == b.Point)), "all 42 directed dock routes keep declared landing points");
        That(towns.CanTravel(west, harlem, 95, completed) && towns.Entrance(west, harlem, completed) == harlem, "observed West Coast to Harlem landing is 201,570, not map marker200,507");
        That(towns.CanTravel(west, new(17, 1, 1409, 346), 95, completed) && !towns.CanTravel(west, new(17, 1, 1409, 346), 95, afterHeaven) && towns.CanTravel(west, new(70, 1, 1409, 346), 95, afterHeaven), "Saint Horn town permissions match dock phase before and after quest6359");
        That(!towns.CanTravel(west, harlem, 95, completed.Where(q => q != 3427).ToHashSet()), "global dock quest is required");
        That(!towns.CanTravel(west, harlem, 95, completed.Where(q => q != 5936).ToHashSet()), "Harlem dock opening quest is required");
        That(!towns.CanTravel(stops[1].Point, harlem, 95, completed.Where(q => q != 3616).ToHashSet()), "source dock opening quest is required");
        That(dock.Admission(west, stops[1].Point, 49, completed) == "dock-level-required" && !towns.CanTravel(west, harlem, 89, completed), "source and destination levels cannot be bypassed");
        That(!towns.CanTravel(west, new(31, 1, 2508, 220), 86, completed), "target area level remains stricter than dock display level");
        That(towns.Admission(west, harlem with { X = 202 }, 95, completed) == "ready", "dock coordinates do not need to equal its static portal area");
        That(!towns.CanTravel(new(40, 0, 55, 270), harlem, 95, completed), "ordinary West Coast street cannot impersonate its dock area");
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
        That(session.Travel(new(40, 1, 1782, 193, 5, 38, 1, 5)), "reselect spawns in room before choosing West Coast dock");
        initial = session.Selected!;
        var request = TownRequests.Area(Convert.FromHexString("3601C9003A0205280001000000000000"));
        That(!session.Travel(request with { PreviousArea = 0 }) && session.TravelReason == "source-area-mismatch" && session.Selected == initial, "wrong source leaves cached scene unchanged");
        That(!session.Travel(request with { Town = 255 }) && session.Selected == initial && repo.List(owner).Single().Revision == initial.Revision, "unknown destination does not persist or advance revision");
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

        That(failed && session.Selected == initial && repo.List(owner).Single().Revision == initial.Revision, "failed database commit cannot publish a dock move");
        Execute("DROP TRIGGER fail_dock;");
        That(session.Travel(request) && session.Selected!.Position == harlem && session.Selected.Revision == initial.Revision + 1, "exact real request now commits Harlem");
        var reopened = new GameSession(new CharacterRepository(path), towns, owner);
        reopened.Select(0);
        reopened.MarkReady();
        That(reopened.Selected!.Position == towns.Room && reopened.Selected.Return is { Town: 54, Area: 1 } && reopened.Selected.CompletedQuests.SequenceEqual(preparation.CompletedQuests) && reopened.Selected.AcceptedStoryLevel == 95, "reselect uses Harlem's room birth point and preserves quest/CG progression");
        That(reopened.LeaveRoom() && reopened.Selected!.Position is { Town: 54, Area: 1 }, "Harlem room exits to its street before selecting another dock");
        That(reopened.Travel(new(40, 1, 1672, 200, 5, 54, 1, 255)) && reopened.Selected!.Position == new Location(40, 1, 1672, 200), "dock accepts full kind byte and keeps Client West Coast1672,200 rather than old-version point");
        That(reopened.Travel(request with { X = 65000, Y = 0 }) && reopened.Selected!.Position == new Location(54, 1, 65000, 0), "dock retains request coordinates outside offline walking bounds");
        // 旧频道落点曾把黑市存为54/0；重新选角必须恢复正式街道，而不是重复保存旧房间门口。
        foreach (var stage in new[] { new HashSet<int> { 5936 }, new HashSet<int> { 5936, 6045 } })
        {
            var expected = towns.Entrance(towns.Room, new(54, 1, 0, 0), stage);
            That(expected is { Town: 54, Area: 1 } && towns.IsPosition(expected, stage), "黑市两个阶段都有正式54/1房间门口");
            That(towns.ResolveRoomReturn(new(54, 0, 1102, 515), new(54, 0, 1050, 462), 95, stage) == expected &&
                towns.ResolveRoomReturn(towns.Room, new(54, 0, 1050, 462), 95, stage) == expected,
                "旧黑市街道位置与已在房间的旧返回点都解析为54/1");
        }
        var legacy = repo.Create(owner, preparation.ToCharacter("LegacyHarlem", towns) with
        {
            CompletedQuests = [.. preparation.CompletedQuests.Append(6356).Distinct().Order()]
        });
        legacy = repo.Save(owner, legacy, new(54, 0, 1102, 515), new(54, 0, 1050, 462));
        var recovered = new GameSession(new CharacterRepository(path), towns, owner);
        recovered.Select(legacy.Slot);
        recovered.MarkReady();
        That(recovered.Selected!.Position == towns.Room && recovered.Selected.Return is { Town: 54, Area: 1 } &&
            recovered.Selected.CompletedQuests.SequenceEqual(legacy.CompletedQuests) && recovered.Selected.Level == legacy.Level,
            "重开旧黑市存档只修正正常选角的返回地区，不更改任务与等级");
        That(recovered.LeaveRoom() && recovered.Selected!.Position is { Town: 54, Area: 1 }, "旧黑市存档出房进入54/1");
        That(recovered.Selected!.CompletedQuests.Contains(6356) &&
            towns.Areas[(54, 1)].Map(recovered.Selected.CompletedQuests.ToHashSet()).Npcs.Any(npc => npc.Id == 100000175),
            "旧存档重选后保留乔斯拉变身任务，出房地图包含其实际NPC身份");
        That(recovered.Travel(new(54, 2, 941, 85, 5, 54, 1, 0)) &&
            recovered.Travel(new(54, 1, 2256, 576, 5, 54, 2, 0)), "修正后可进入哈林副本入口并返回黑市");
        That(recovered.Travel(new(40, 1, 1672, 200, 5, 54, 1, 255)), "修正后黑市码头可离开哈林");
        Console.WriteLine($"PASS {Count - checks} dock assertions; active=7, directed routes=42");
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
