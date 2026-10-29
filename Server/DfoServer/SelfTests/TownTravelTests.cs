using static DfoServer.Testing.TestCheck;
using System.Text.RegularExpressions;
using PvfLib;
using DfoServer.Game.Town;
using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Development;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;

namespace DfoServer;

internal static class TownTravelTests
{
    public static void Run(TownCatalog towns, CharacterPreparation preparation, PvfArchive pvf)
    {
        int checks = Count;

        var completed = preparation.CompletedQuests.ToHashSet();
        // 两份已知输入只差镇17追加的Area5；按原始声明判分支，不能把loader漏项当作旧输入。
        string saintHorn = pvf.ReadText("town/" + PvfScript.List(pvf.ReadText("town/town.lst"))[17]);
        int[] declaredAreas = Regex.Matches(saintHorn, @"(?m)^\[area\]\s*(\d+)(?=\s|$)")
            .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
        bool declaredRequiem = declaredAreas.Contains(5);
        That(declaredAreas.SequenceEqual(Enumerable.Range(0, declaredRequiem ? 6 : 5)) &&
            towns.Areas.Keys.Where(key => key.Item1 == 17).Select(key => (int)key.Item2).Order().SequenceEqual(declaredAreas),
            "SaintHorn保留原始声明的全部区域，不能漏掉已声明的Area5或制造新区域");
        if (declaredRequiem)
        {
            var declarations = Regex.Matches(saintHorn, @"(?ms)^\[area\]\s*5\s+`([^`]+)`(.*?)^\[/area\]");
            That(declarations.Count == 1, "追加的安魂曲Area5必须有唯一完整声明");
            string body = declarations[0].Groups[2].Value;
            const string requiemPath = "map/town/sainthorn/timegaterequiemwr.map";
            That(PvfArchive.NormalizePath("map/" + declarations[0].Groups[1].Value) == requiemPath &&
                PvfScript.Number(PvfScript.Block(body, "permission"), "need level", 1) == 85 &&
                Regex.IsMatch(body, @"`\[dungeon gate\]`\s+26(?=\s|$)") && PvfScript.Block(body, "phase").Length == 0 &&
                towns.Areas[(17, 5)] is { Level: 85, DungeonWorldMap: 26, Phases.Length: 0, Default.Error: null } requiem &&
                requiem.Default.Path == requiemPath,
                "只认可原声明与正式目录一致的85级world26安魂曲Area5，无相位且地图可读");
        }
        var originalAreas = towns.Areas.Where(row => row.Key.Item1 != 51).ToArray();
        That(originalAreas.Length == (declaredRequiem ? 344 : 343) && originalAreas.Select(row => row.Key.Item1).Distinct().Count() == 65,
            "all original matching PVF town/area rows retained");
        That(!towns.Areas.ContainsKey((51,0)) || towns.Areas.Keys.Where(key => key.Item1 == 51).Select(key => key.Item2).Order().SequenceEqual(new byte[] {0,1,2}),
            "indexed Beast town adds all three original areas without replacing another town");
        That(towns.Areas.ContainsKey((124, 0)) && towns.Areas.ContainsKey((1, 0)) && towns.Areas.ContainsKey((60, 1)), "no main-town or area-number whitelist");
        var maps = towns.Areas.Values.SelectMany(a => a.Phases.Prepend(a.Default)).DistinctBy(m => m.Path).ToArray();
        var originalMaps = maps.Where(map => !map.Path.StartsWith("map/town/beast_centralpark/",StringComparison.OrdinalIgnoreCase)).ToArray();
        That(originalMaps.Length == (declaredRequiem ? 353 : 352) && maps.Count(m => m.Error is not null) == 1, "all original phase maps retained and missing optional resource exposed");
        That(maps.Where(map => map.Path.StartsWith("map/town/beast_centralpark/",StringComparison.OrdinalIgnoreCase)).All(map => map.Error is null),
            "newly indexed Beast town maps must load successfully");
        That(towns.Admission(new(119, 1, 0, 0), new(119, 0, 450, 234), 95, completed).StartsWith("map-resource-missing:"), "missing map never becomes a fabricated region");
        var bridge = new Location(38, 3, 176, 154);
        var hendon = new Location(39, 0, 330, 130);
        That(towns.Admission(towns.InitialReturn, bridge, 95) == "target-quest-required", "unfinished area quest still gates entry");
        That(towns.CanTravel(towns.InitialReturn, bridge, 1, new HashSet<int> { 3155 }), "completed 3155 opens Elvengard-Hendon bridge");
        That(towns.Admission(bridge, hendon, 1, completed) == "target-level-required", "Hendon level 17 requirement");
        That(towns.CanTravel(bridge, hendon, 17, new HashSet<int> { 3155, 3156 }), "completed 3156 opens cross-town directed edge");
        That(!towns.CanTravel(towns.InitialReturn, hendon, 95, completed), "unrelated target cannot bypass connecting map");
        That(towns.Entrance(towns.InitialReturn, bridge, completed) == bridge && towns.Entrance(bridge, hendon, completed) == hendon, "PVF fixed bridge and Hendon door coordinates");
        That(towns.Entrance(bridge, new(38, 0, 500, 220), completed) == new Location(38, 0, 1407, 360), "return to Elvengard uses actual bridge door");
        That(!towns.IsPosition(new(39, 0, 1324, 149), new HashSet<int> { 3156 }) && towns.IsPosition(new(39, 0, 1324, 149), new HashSet<int> { 3156, 3186 }), "quest-controlled geometry follows character progress");
        var ash = towns.Areas[(60, 1)];
        var rect = ash.Default.Bounds.First().Bounds;
        var ashPoint = new Location(60, 1, (ushort)(rect.X + rect.Width / 2), (ushort)(rect.Y + rect.Height / 2));
        That(towns.CanTravel(new(60, 0, 474, 234), ashPoint, 94, new HashSet<int> { 6201 }) && towns.CanTravel(new(60, 0, 474, 234), ashPoint, 94, new HashSet<int> { 6212 }), "OR quest permissions accept either listed completion");
        That(towns.Admission(new(60, 0, 474, 234), ashPoint, 94) == "target-quest-required", "OR permission requires one completion");
        That(towns.Admission(new(17, 0, 474, 234), new(17, 1, 600, 220), 95, new HashSet<int> { 6359 }) == "target-excluded-by-quest", "except quest not inverted into an unlock");
        That(towns.Areas[(54, 1)].Map(new HashSet<int> { 5936 }).Path.EndsWith("blackmarket_before.map") && towns.Areas[(54, 1)].Map(completed).Path.EndsWith("blackmarket_liberation.map"), "last completed town phase selects matching geometry");
        var row = TownCatalog.Rows("[town movable area]\n0 0 10 10 57 2\n[check condition]\n1\n[/check condition]\n20 0 10 10 57 33\n[/town movable area]", "town movable area", 6);
        That(row[0].Conditions.SequenceEqual(new[] { 1 }) && row[1].Conditions.Length == 0, "special instance condition belongs only to its preceding route");
        That(towns.IsRoom(new(17, 0, 474, 234), completed) && towns.DefaultReturn(new(17, 0, 474, 234), completed) is { Town: 17, Area: 1 }, "legacy regional rooms use their own town return");
        That(TownPackets.RoomReturnTown(new(39, 0, 596, 144)).SequenceEqual(new byte[] { 39, 0, 0, 0 }), "native NOTI1379 town identity width");
        string path = Path.Combine(Path.GetTempPath(), "S5A9-town-tests-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("town-test", () =>
        {
        });
        var character = repo.Create(owner, preparation.ToCharacter("Town95", towns));
        var session = new GameSession(repo, towns, owner);
        session.Select(0);
        session.MarkReady();
        That(session.LeaveRoom(), "prepared role leaves its initial room");
        That(session.Travel(TownRequests.Area(Convert.FromHexString("2603B0009A0005260000000000000000"))) && session.Selected!.Position == bridge, "actual rejected request now commits the bridge transition");
        That(session.Travel(new(39, 0, 330, 130, 5, 38, 3, 0)) && session.Selected!.Position == hendon, "Hendon entry commits without another ready callback");
        That(session.Travel(new(40, 0, 55, 270, 5, 39, 0, 0)) && session.Selected!.Position == new Location(40, 0, 55, 270), "cross-town West Coast entry");
        That(session.Travel(new(39, 0, 3547, 270, 5, 40, 0, 0)), "continuous West Coast-Hendon return");
        That(session.Travel(new(38, 1, 433, 311, 5, 39, 0, 0)) && session.Selected!.Position == new Location(38, 1, 433, 311), "animated Hendon portal enters the shared Seria room");
        That(session.Selected!.Return == new Location(39, 0, 596, 144), "save fixed Hendon portal as room return");
        var resumed = new GameSession(new CharacterRepository(path), towns, owner);
        resumed.Select(0);
        resumed.MarkReady();
        That(!resumed.Travel(new(38, 0, 622, 196, 5, 38, 1, 0)) && resumed.TravelReason == "room-return-mismatch", "room cannot silently return to Elvengard after reentering from Hendon");
        That(resumed.LeaveRoom() && resumed.Selected!.Position == new Location(39, 0, 596, 144), "bottom exit restores saved cross-town door after database reopen");
        That(resumed.Travel(new(38, 1, 450, 234, 5, 39, 0, 0)) && resumed.Travel(new(39, 0, 596, 144, 5, 38, 1, 6)), "ordinary room door honors cross-town persisted return");
        That(resumed.Travel(new(38, 3, 780, 362, 5, 39, 0, 0)) && resumed.Travel(new(38, 0, 1407, 360, 5, 38, 3, 0)), "full route roundtrip");
        var saved = repo.List(owner).Single();
        That(saved.Position == new Location(38, 0, 1407, 360) && saved.CompletedQuests.SequenceEqual(preparation.CompletedQuests) && saved.GrowType == 0x25, "movement commits preserve level/job/quest state");
        // 房间里的地图传送门对象走 PathKind 6，目的地是 PVF warpgate 条目；不能按存档返回点拒绝。
        var inRoom = saved with { Position = new Location(38, 1, 450, 234, 0), Return = new Location(39, 0, 596, 144, 0) };
        var quests = saved.CompletedQuests.ToHashSet();
        var portalPlan = TownTravel.Plan(inRoom, new(38, 0, 622, 196, 5, 38, 1, 6), towns, quests);
        That(portalPlan.Accepted && portalPlan.Position == new Location(38, 0, 622, 196, 0),
            "地图传送门 PathKind 6 按 warpgate 目标放行，不受存档返回点限制 原因=" + portalPlan.Reason);
        var doorPlan = TownTravel.Plan(inRoom, new(38, 0, 622, 196, 5, 38, 1, 0), towns, quests);
        That(!doorPlan.Accepted && doorPlan.Reason == "room-return-mismatch", "PathKind 0 仍绑定存档返回点 原因=" + doorPlan.Reason);
        Console.WriteLine($"PASS {Count - checks} all-town assertions; towns={originalAreas.Select(row => row.Key.Item1).Distinct().Count()}, areas={originalAreas.Length}, maps={originalMaps.Length}");
    }
}
