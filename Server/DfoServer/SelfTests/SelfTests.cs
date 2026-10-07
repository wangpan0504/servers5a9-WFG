using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using System.Buffers.Binary;
using DfoServer.Network;
using DfoServer.Persistence;

namespace DfoServer;

internal static class SelfTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters)
    {
        int assertions = 0;
        void Check(bool ok, string name)
        {
            assertions++;
            if (!ok)
                throw new Exception("FAIL: " + name);
        }

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

            Check(rejected, name);
        }

        Check(towns.IsPosition(towns.Room) && towns.IsPosition(towns.InitialReturn), "PVF spawn/return bounds");
        Check(towns.Room.X == 450 && towns.Room.Y == 234 && towns.InitialReturn.X == 622, "matching version resource anchors");
        byte[] movement = Convert.FromHexString("CD01EA00066400030000000000");
        var decoded = TownRequests.Position(631, movement);
        Check(decoded.X == 461 && decoded.Y == 234 && decoded.Action == 3, "recorded Client movement");
        Reject(() => TownRequests.Position(631, movement[..7]), "reject legacy width");
        byte[] bad = movement.ToArray();
        bad[12] = 1;
        Reject(() => TownRequests.Position(631, bad), "reject unknown movement tail");
        byte[] frame = new byte[13 + movement.Length];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), 631);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(3), frame.Length);
        movement.CopyTo(frame, 13);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), GameFrame.Crc(frame.AsSpan(11)));
        Check(GameFrame.ParseClient(frame).Body.SequenceEqual(movement), "recorded body frame integrity");
        frame[^1] ^= 1;
        Reject(() => GameFrame.ParseClient(frame), "bad CRC blocked");
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
            Check(!session.Move(TownRequests.Position(631, movement)) && session.Phase == SessionPhase.Unselected && session.SelectionEpoch == 1, "position before town phase rejected");
            session.MarkReady();
            Check(session.Phase == SessionPhase.Town && session.MatchesSelection(session.SelectionEpoch, session.Selected!.Id) && session.Move(TownRequests.Position(631, movement)), "live position committed in town");
            Check(repository.List(owner)[0].Position.X == 461, "position durable");
            var reloaded = new CharacterRepository(database);
            Check(reloaded.List(owner)[0].Position.X == 461, "reopen restores position");
            Check(!session.Move(TownRequests.Position(631, Convert.FromHexString("FFFFEA00056400000000000000"))), "out of bounds rejected");
            Reject(() => repository.Save(owner, c, c.Position, null), "stale revision rejected");
            Check(c.Return == towns.InitialReturn, "new character starts with Elvengard return point");
            Check(!session.Travel(new(39, 0, 500, 220, 5, 38, 1, 0)), "room cannot exit to a different persisted return town");
            var area = TownRequests.Area(Convert.FromHexString("26008502FB0005260001000000000006"));
            Check(area.PreviousTown == 38 && area.PreviousArea == 1 && area.PathKind == 6, "native and live area tail");
            Reject(() => SessionRequests.Empty([0]), "nonempty exit rejected");
            Check(session.LeaveRoom() && session.Selected!.Position == towns.InitialReturn, "first bottom exit enters Elvengard");
            Check(!session.LeaveRoom(), "repeated bottom exit does not move a street character");
            byte[] streetMove = Convert.FromHexString("FE02DB00056400");
            Check(session.Move(TownRequests.Position(35, streetMove)), "street coordinate observation");
            var enter = TownRequests.Area(Convert.FromHexString("26016903FD0005260000000000000000"));
            Check(!session.Travel(enter with { PreviousArea = 1 }), "stale source area rejected");
            Check(session.Travel(enter with { X = 450, Y = 234 }), "valid route not rejected due to sampled coordinate distance");
            Check(session.Selected!.Position.X == 450 && session.Selected.Position.Y == 234, "normal door preserves requested coordinates");
            Check(repository.List(owner)[0].Return == towns.InitialReturn, "room return uses PVF door instead of last standing point");
            Check(session.Travel(area with { X = ushort.MaxValue }) && session.Selected!.Position.X == ushort.MaxValue, "ordinary travel preserves full u16 outside offline geometry");
            Check(session.Travel(enter) && session.Travel(area) && session.Selected!.Position == new Location(38, 0, 645, 251), "ordinary room exit uses raw sampled request coordinates");
            var back = towns.InitialReturn;
            Check(session.Travel(enter), "reenter before bottom exit recovery");
            var old = repository.List(owner)[0];
            repository.Save(owner, old, old.Position, new(38, 0, 19, 259));
            var resumed = new GameSession(new CharacterRepository(database), towns, owner);
            resumed.Select(0);
            resumed.MarkReady();
            Check(resumed.LeaveRoom() && resumed.Selected!.Position == back, "old saved street coordinate cannot displace bottom door arrival after reopen");
            var beforeReturn = repository.List(owner)[0];
            Reject(() => SessionRequests.Empty([0]), "nonempty return-selection request rejected");
            Check(resumed.Selected is not null && resumed.Ready, "invalid return preserves selected session");
            resumed.ReturnToRoster();
            Check(resumed.Selected is null && !resumed.Ready && resumed.TravelReason == "not-requested", "return-selection clears character and readiness");
            var afterReturn = repository.List(owner)[0];
            Check(afterReturn.Id == beforeReturn.Id && afterReturn.Revision == beforeReturn.Revision && afterReturn.Position == beforeReturn.Position && afterReturn.Return == beforeReturn.Return && afterReturn.Skills.SequenceEqual(beforeReturn.Skills) && afterReturn.CompletedQuests.SequenceEqual(beforeReturn.CompletedQuests), "return-selection leaves persisted position, revision and progress intact");
            Check(!resumed.Move(TownRequests.Position(35, streetMove)) && !resumed.LeaveRoom(), "old character operations blocked after return");
            resumed.ReturnToRoster();
            Check(resumed.Selected is null && !resumed.Ready, "duplicate return remains in roster state");
            resumed.Select(0);
            resumed.MarkReady();
            Check(resumed.Selected!.Position == towns.Room && resumed.Selected.Return == back, "same connection reselect uses normal spawn transaction");
            using var newer = owners.Acquire("test", () =>
            {
            });
            Check(displaced, "takeover closes old owner");
            Reject(() => session.Move(TownRequests.Position(631, movement)), "stale generation rejected even no-op");
            Reject(() => resumed.ReturnToRoster(), "old generation cannot reset selection state");
            long ended = resumed.SelectionEpoch;
            ushort endedId = resumed.Selected!.Id;
            resumed.End();
            Check(resumed.SelectionEpoch == ended + 1 && resumed.Phase == SessionPhase.Unselected && !resumed.MatchesSelection(ended, endedId), "disconnect invalidates the replaced selection");
            owner.Dispose();
            Check(repository.List(newer).Length == 1, "old dispose cannot remove new owner");
            using var other = owners.Acquire("other", () =>
            {
            });
            Check(repository.List(other).Length == 0, "account isolation");
            byte[] actor = TownPackets.Actor(c), player = TownPackets.Player(c, characters);
            Check(actor.Length == 251 && player.Length == 167, "Client body widths retained");
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        Console.WriteLine($"PASS {assertions} assertions");
    }
}
