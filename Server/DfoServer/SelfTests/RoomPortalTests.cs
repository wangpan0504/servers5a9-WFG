using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Development;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;

namespace DfoServer;

internal static class RoomPortalTests
{
    public static void Run(TownCatalog towns, CharacterPreparation preparation)
    {
        int checks = Count;

        var completed = preparation.CompletedQuests.ToHashSet();
        That(towns.RoomPortals.Select(p => (p.Town, p.Area, p.Bounds.X, p.Bounds.Y, p.Bounds.Width, p.Bounds.Height, p.Level, p.Quest)).SequenceEqual(new (byte, byte, int, int, int, int, int, int)[] { (38, 0, 570, 172, 200, 80, 0, 0), (39, 2, 355, 204, 200, 80, 40, 0), (40, 1, 1650, 176, 200, 80, 50, 0), (38, 5, 530, 237, 200, 80, 60, 0), (39, 0, 1737, 245, 200, 80, 0, 10100), (117, 1, 758, 228, 200, 80, 0, 8927) }), "all six matching XUI buttons and conditions");
        byte[] west = Convert.FromHexString("2801F606C10005260001000000000005");
        That(Enumerable.Range(0, 256).All(kind =>
        {
            var b = west.ToArray();
            b[15] = (byte)kind;
            var p = TownRequests.Area(b);
            return p.PathKind == kind && p.Town == 40 && p.Area == 1 && p.X == 1782 && p.Y == 193 && p.PreviousTown == 38 && p.PreviousArea == 1;
        }), "every kind byte parses unchanged instead of terminating the connection");
        var target = new Location(40, 1, 1782, 193);
        That(towns.Admission(towns.Room, target, 95, completed, towns.InitialReturn, true) == "ready", "actual West Coast room portal request matches XUI bounds");
        That(towns.Admission(towns.Room, target, 49, completed, towns.InitialReturn, true) == "portal-level-required", "portal level50 is stricter than target area17");
        That(towns.Admission(towns.Room, new(39, 0, 1837, 285), 95, completed.Where(q => q != 10100).ToHashSet(), null, true) == "portal-quest-required", "portal quest10100 is not bypassed by area access");
        That(towns.Admission(towns.Room, new(40, 1, 1900, 250), 95, completed, null, true) == "ready", "portal coordinates are not restricted to the static XUI rectangle");
        That(towns.Admission(towns.Room, new(54, 1, 201, 570), 95, completed, null, true) == "portal-target-not-in-pvf", "room menu cannot impersonate a dock destination");
        That(towns.Admission(towns.InitialReturn, target, 95, completed, null, true) == "portal-source-not-room", "portal selection requires a room source");
        string path = Path.Combine(Path.GetTempPath(), "S5A9-room-portal-tests-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("portal-test", () =>
        {
        });
        repo.Create(owner, preparation.ToCharacter("Portal95", towns));
        var session = FromRoom();
        var initial = session.Selected!;
        That(!session.Travel(TownRequests.Area(west) with { PreviousArea = 0 }) && session.Selected == initial, "wrong previous area remains rejected without updating scene");
        That(session.Travel(TownRequests.Area(west)) && session.Selected!.Position == target && session.Selected.Return is { Town: 40, Area: 0 }, "exact disconnecting request persists selected landing and its district");
        var reopened = new GameSession(new CharacterRepository(path), towns, owner);
        reopened.Select(0);
        reopened.MarkReady();
        That(reopened.Selected!.Position == towns.Room && reopened.Selected.Return is { Town: 40, Area: 0 } && reopened.Selected.CompletedQuests.SequenceEqual(preparation.CompletedQuests) && reopened.Selected.AcceptedStoryLevel == 95, "portal district survives reselect at room birth point, preserving quest/CG state");
        session = FromRoom();
        That(session.Travel(TownRequests.Area(Convert.FromHexString("27022802DA0005260001000000000005"))) && session.Selected!.Position == new Location(39, 2, 552, 218), "earlier recorded Hendon menu selection works independently of saved room return");
        foreach (var portal in towns.RoomPortals)
        {
            session = FromRoom();
            var point = new Location(portal.Town, portal.Area, (ushort)(portal.Bounds.X + portal.Bounds.Width / 2), (ushort)(portal.Bounds.Y + portal.Bounds.Height / 2));
            That(session.Travel(new(point.Town, point.Area, point.X, point.Y, 5, 38, 1, 5)) && session.Selected!.Position == point, $"configured room destination {point.Town}/{point.Area}");
        }

        session = FromRoom();
        That(session.Travel(TownRequests.Area(west) with { PathKind = 255 }) && session.Selected!.Position == target, "configured portal route accepts other kind values");
        session = FromRoom();
        That(session.Travel(new(38, 0, 586, 209, 5, 38, 1, 6)) && session.Selected!.Position == new Location(38, 0, 586, 209), "ordinary return6 preserves client landing");
        session = FromRoom();
        That(session.Travel(new(38, 0, 586, 209, 5, 38, 1, 5)) && session.Selected!.Position == new Location(38, 0, 586, 209), "portal5 to same return town keeps chosen XUI landing rather than ordinary door");
        session = FromRoom();
        That(session.Travel(new(38, 0, 1051, 239, 5, 38, 1, 254)) && session.Selected!.Position == new Location(38, 0, 1051, 239), "other kinds retain client landing outside portal rectangles");
        session = FromRoom();
        That(session.Travel(new(40, 1, 65000, 0, 5, 38, 1, 5)) && session.Selected!.Position == new Location(40, 1, 65000, 0), "portal does not clamp or reject client coordinates");
        Console.WriteLine($"PASS {Count - checks} room portal assertions; destinations=6, kind bytes=256");
        GameSession FromRoom()
        {
            var stored = repo.List(owner).Single();
            repo.Save(owner, stored, towns.Room, towns.InitialReturn);
            var next = new GameSession(repo, towns, owner);
            next.Select(0);
            next.MarkReady();
            return next;
        }
    }
}
