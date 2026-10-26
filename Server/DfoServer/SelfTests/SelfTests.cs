using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;

namespace DfoServer;

internal static class SelfTests
{
    public static void Run(TownCatalog towns)
    {
        int assertions = Count;

        void Reject(Action action, string name)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
            {
                rejected = true;
            }

            That(rejected, name);
        }

        That(towns.IsPosition(towns.Room) && towns.IsPosition(towns.InitialReturn), "PVF spawn/return bounds");
        That(towns.Room.X == 450 && towns.Room.Y == 234 && towns.InitialReturn.X == 622, "matching version resource anchors");
        byte[] movement = Convert.FromHexString("CD01EA00066400030000000000");
        var decoded = TownRequests.Position(631, movement);
        That(decoded.X == 461 && decoded.Y == 234 && decoded.Action == 3, "recorded Client movement");
        Reject(() => TownRequests.Position(631, movement[..7]), "reject legacy width");
        byte[] bad = movement.ToArray();
        bad[12] = 1;
        Reject(() => TownRequests.Position(631, bad), "reject unknown movement tail");
        string folder = Path.Combine(Path.GetTempPath(), "ServerS5A9-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string database = Path.Combine(folder, "game.db");
            var repository = new CharacterRepository(database);
            var owners = new AccountOwners();
            bool displaced = false;
            using var owner = owners.Acquire("test", () => displaced = true);
            var c = repository.Create(owner, new("MoveTest", towns.Room, towns.InitialReturn));
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            That(!session.Move(TownRequests.Position(631, movement)) && session.Phase == SessionPhase.Unselected && session.SelectionEpoch == 1, "position before town phase rejected");
            session.MarkReady();
            That(session.Phase == SessionPhase.Town && session.MatchesSelection(session.SelectionEpoch, session.Selected!.Id) && session.Move(TownRequests.Position(631, movement)), "live position committed in town");
            That(repository.List(owner)[0].Position.X == 461, "position durable");
            var reloaded = new CharacterRepository(database);
            That(reloaded.List(owner)[0].Position.X == 461, "reopen restores position");
            That(!session.Move(TownRequests.Position(631, Convert.FromHexString("FFFFEA00056400000000000000"))), "out of bounds rejected");
            Reject(() => repository.Save(owner, c, c.Position, null), "stale revision rejected");
            That(c.Return == towns.InitialReturn, "new character starts with Elvengard return point");
            That(!session.Travel(new(39, 0, 500, 220, 5, 38, 1, 0)), "room cannot exit to a different persisted return town");
            var area = TownRequests.Area(Convert.FromHexString("26008502FB0005260001000000000006"));
            That(area.PreviousTown == 38 && area.PreviousArea == 1 && area.PathKind == 6, "native and live area tail");
            Reject(() => SessionRequests.Empty([0]), "nonempty exit rejected");
            That(session.LeaveRoom() && session.Selected!.Position == towns.InitialReturn, "first bottom exit enters Elvengard");
            That(!session.LeaveRoom(), "repeated bottom exit does not move a street character");
            byte[] streetMove = Convert.FromHexString("FE02DB00056400");
            That(session.Move(TownRequests.Position(35, streetMove)), "street coordinate observation");
            var enter = TownRequests.Area(Convert.FromHexString("26016903FD0005260000000000000000"));
            That(!session.Travel(enter with { PreviousArea = 1 }), "stale source area rejected");
            That(session.Travel(enter with { X = 450, Y = 234 }), "valid route not rejected due to sampled coordinate distance");
            That(session.Selected!.Position.X == 450 && session.Selected.Position.Y == 234, "normal door preserves requested coordinates");
            That(repository.List(owner)[0].Return == (towns.InitialReturn with { Direction = 5 }), "房间返回坐标取PVF门口，并保留本次步行朝向");
            That(session.Travel(area with { X = ushort.MaxValue }) && session.Selected!.Position.X == ushort.MaxValue, "ordinary travel preserves full u16 outside offline geometry");
            That(session.Travel(enter) && session.Travel(area) && session.Selected!.Position == new Location(38, 0, 645, 251, 5), "普通房间出口保持原始上报坐标和已保存朝向");
            var back = towns.InitialReturn;
            That(session.Travel(enter), "reenter before bottom exit recovery");
            var old = repository.List(owner)[0];
            repository.Save(owner, old, old.Position, new(38, 0, 19, 259));
            var resumed = new GameSession(new CharacterRepository(database), towns, owner);
            resumed.Select(0);
            resumed.MarkReady();
            That(resumed.LeaveRoom() && resumed.Selected!.Position == back, "old saved street coordinate cannot displace bottom door arrival after reopen");
            var beforeReturn = repository.List(owner)[0];
            Reject(() => SessionRequests.Empty([0]), "nonempty return-selection request rejected");
            That(resumed.Selected is not null && resumed.Ready, "invalid return preserves selected session");
            resumed.ReturnToRoster();
            That(resumed.Selected is null && !resumed.Ready && resumed.TravelReason == "not-requested", "return-selection clears character and readiness");
            var afterReturn = repository.List(owner)[0];
            That(afterReturn.Id == beforeReturn.Id && afterReturn.Revision == beforeReturn.Revision && afterReturn.Position == beforeReturn.Position && afterReturn.Return == beforeReturn.Return && afterReturn.Skills.SequenceEqual(beforeReturn.Skills) && afterReturn.CompletedQuests.SequenceEqual(beforeReturn.CompletedQuests), "return-selection leaves persisted position, revision and progress intact");
            That(!resumed.Move(TownRequests.Position(35, streetMove)) && !resumed.LeaveRoom(), "old character operations blocked after return");
            resumed.ReturnToRoster();
            That(resumed.Selected is null && !resumed.Ready, "duplicate return remains in roster state");
            resumed.Select(0);
            resumed.MarkReady();
            That(resumed.Selected!.Position == towns.Room && resumed.Selected.Return == back, "same connection reselect uses normal spawn transaction");
            using var newer = owners.Acquire("test", () =>
            {
            });
            That(displaced, "takeover closes old owner");
            Reject(() => session.Move(TownRequests.Position(631, movement)), "stale generation rejected even no-op");
            Reject(() => resumed.ReturnToRoster(), "old generation cannot reset selection state");
            long ended = resumed.SelectionEpoch;
            ushort endedId = resumed.Selected!.Id;
            resumed.End();
            That(resumed.SelectionEpoch == ended + 1 && resumed.Phase == SessionPhase.Unselected && !resumed.MatchesSelection(ended, endedId), "disconnect invalidates the replaced selection");
            owner.Dispose();
            That(repository.List(newer).Length == 1, "old dispose cannot remove new owner");
            using var other = owners.Acquire("other", () =>
            {
            });
            That(repository.List(other).Length == 0, "account isolation");
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        Console.WriteLine($"PASS {Count - assertions} assertions");
    }
}
