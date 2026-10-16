using static DfoServer.Testing.TestCheck;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonProbeRunTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;

        var maze = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf).Get(3).Mazes.Single(candidate => candidate.QuestConnectionTokens.Count == 3
            && candidate.QuestConnectionTokens[1] == new PvfToken(0, 3145));
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var firstMap = maze.MapSpecification!.Single(map => map.Id == 76121);
        var nextMap = maze.MapSpecification!.Single(map => map.Id == 76122);
        var boss = maze.MapSpecification!.Single(map => map.Kind == "boss");
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, firstMap, boss);
        var first = DungeonMonsterPlan.Create(rooms, firstMap, entry.CharacterId, 10);
        var run = new DungeonProbeRun(entry, first, 23);
        That(first.Monsters.Count == 4 && run.CurrentMap.Id == 76121 && run.RoomRevision == 1,
            "first room contains the four matching resource monsters");
        That(run.Matches(99, 17, 23) && !run.Matches(98, 17, 23) && !run.Matches(99, 18, 23)
            && !run.Matches(99, 17, 24), "instance binds character identity revision and selection epoch independently");
        That(run.ReportDeath(10) == DungeonDeathReport.Rejected && run.Target(1, 1) is null,
            "loading cannot accept death or movement");
        run.Loaded();
        That(run.ReportDeath(999) == DungeonDeathReport.Rejected && run.ReportDeath(0x1000A) == DungeonDeathReport.Rejected,
            "unknown and high-bit object keys cannot alias a declared monster");
        That(run.ReportDeath(10) == DungeonDeathReport.Accepted && run.ReportDeath(10) == DungeonDeathReport.Duplicate,
            "death report is idempotent for the same current-room key");
        That(!run.AllReportedDead && run.Target(1, 1) is null, "one dead monster does not unlock the room");
        foreach (var monster in first.Monsters.Skip(1))
            That(run.ReportDeath(monster.ObjectKey) == DungeonDeathReport.Accepted, "remaining monster accepted " + monster.ObjectKey);
        That(run.AllReportedDead && run.Target(1, 1) == nextMap && nextMap.Position == new DungeonPoint(1, 1),
            "all declared keys unlock exactly the adjacent matching map 76122");
        That(run.Target(0, 1) is null && run.Target(1, 4) is null && run.Target(255, 255) is null,
            "same-room distant and absent destinations stay rejected");
        var next = DungeonMonsterPlan.Create(rooms, nextMap, entry.CharacterId, 20);
        run.Enter(nextMap, next);
        That(run.CurrentMap == nextMap && run.RoomRevision == 2 && run.Loading && !run.AllReportedDead,
            "entering another room advances revision and requires loading");
        run.Loaded();
        That(run.ReportDeath(10) == DungeonDeathReport.Rejected, "previous-room death cannot affect a new room");
        foreach (var monster in next.Monsters)
            That(run.ReportDeath(monster.ObjectKey) == DungeonDeathReport.Accepted, "next-room key accepted " + monster.ObjectKey);
        var cached = run.Cached(firstMap);
        That(cached is not null && cached.Monsters.Count == 0 && cached.Room == first.Room,
            "visited room cache removes dead objects without replacing its resource definition");
        run.Enter(firstMap, cached!);
        run.Loaded();
        That(run.AllReportedDead && run.RoomRevision == 3 && run.ReportDeath(10) == DungeonDeathReport.Duplicate,
            "returning to a visited room does not respawn its dead monsters");
        That(run.Cached(nextMap)!.Monsters.Count == 0, "leaving a cleared room retains its dead roster");
        var another = new DungeonProbeRun(entry, first, 24);
        That(run.InstanceId != another.InstanceId && !run.Matches(99, 17, another.SelectionEpoch),
            "new selection epoch and instance identity do not reuse a previous run");
        run.End();
        That(!run.Matches(99, 17, 23) && run.ReportDeath(10) == DungeonDeathReport.Rejected && run.Target(1, 1) is null,
            "ended instance rejects identity death and movement");
        bool unsupported = false;
        try
        {
            DungeonMonsterPlan.FromRoom(first.Room with
            {
                Monsters = [first.Room.Monsters[0] with { RankKind = "[future]" }]
            }, 99, 10);
        }
        catch (NotSupportedException)
        {
            unsupported = true;
        }
        That(unsupported, "unknown room monster schema cannot become an empty cleared room");
        Console.WriteLine($"PASS {Count - checks} dungeon probe run assertions");
    }
}
