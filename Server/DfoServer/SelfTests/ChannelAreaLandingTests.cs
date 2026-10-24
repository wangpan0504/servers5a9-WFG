using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;

namespace DfoServer;

internal static class ChannelAreaLandingTests
{
    public static void Run(TownCatalog towns)
    {
        int before = Count;
        var none = new HashSet<int>();
        var elven = new ChannelEntry(new(2, 1), 22, "[elven_guard]");
        var sky = new ChannelEntry(new(2, 11), 22, "[sky_catle]");
        var harlem = new ChannelEntry(new(2, 37), 22, "[harlem]");
        var fiend = new ChannelEntry(new(2, 39), 45, "[fiendwar]");
        var pvp = new ChannelEntry(new(2, 40), 24, "[pvp]");

        var elvenSpot = ChannelAreaLanding.Place(towns, elven, 1, none);
        That(elvenSpot is { Town: 38, Area: 0 } && elvenSpot != towns.Room, "艾尔文防线新选角落到艾尔文街道");
        var granfloris = new ChannelEntry(new(2, 10), 22, "[granfloris]");
        That(ChannelAreaLanding.Place(towns, granfloris, 1, none) is null && ChannelAreaLanding.Place(towns, granfloris, 95, none) is null,
            "格兰之森是主入口频道，普通选角留在共享房间");
        var luke = new ChannelEntry(new(2, 20), 32, "[luke_raid]");
        var lukeSpot = new Location(32, 0, 474, 234);
        That(ChannelAreaLanding.Place(towns, luke, 95, none) == lukeSpot, "卢克等级达到后落到机场门口");
        That(ChannelAreaLanding.Place(towns, luke, 85, none) is null, "卢克等级不足时留在共享房间");
        That(ChannelAreaLanding.Place(towns, sky, 95, none) is null, "天空之城任务不足时不离开共享房间");
        That(ChannelAreaLanding.Place(towns, harlem, 1, none) is null, "哈林任务不足时留在共享房间");
        That(ChannelAreaLanding.Place(towns, fiend, 95, none) is null, "未完成入场任务 8672 的超时空之战频道留在共享房间");
        That(ChannelAreaLanding.Place(towns, fiend, 95, new HashSet<int> { 8672 }) is { Town: 115, Area: 0 }, "完成 8672 的超时空之战频道落到最后防线 115/0");
        That(towns.Areas.ContainsKey((10, 0)) && towns.Areas[(10, 0)].Gate is not null, "决斗场城镇 10 在目录里且写有门口");
        That(ChannelAreaLanding.Place(towns, pvp, 1, none) is null, "决斗场等级不足时留在共享房间");
        That(ChannelAreaLanding.Place(towns, pvp, 95, none) is { Town: 10, Area: 0, X: 474, Y: 234 }, "决斗场等级达到后落到目录门口");
        That(ChannelAreaLanding.Place(towns, new ChannelEntry(new(2, 19), 23, "[raid]"), 95, none) is { Town: 19, Area: 0 }, "安徒恩落到已有战舰门口");
        That(ChannelAreaLanding.Place(towns, new ChannelEntry(new(2, 31), 18, "[attackzone]"), 95, none) is { Town: 15, Area: 0 }, "攻击区落到已有城镇 15");
        That(towns.Areas[(19, 0)].Gate == new Location(19, 0, 474, 234), "安徒恩 474,234 是战舰区域目录门口");
        That(towns.Areas[(32, 0)].Gate == lukeSpot, "卢克 474,234 是机场区域目录门口");
        That(towns.Areas[(17, 0)].Gate == new Location(17, 0, 474, 234), "圣痕 474,234 是区域目录门口");
        That(towns.Areas[(10, 0)].Gate == new Location(10, 0, 474, 234), "决斗场 474,234 是区域目录门口");

        string path = Path.Combine(Path.GetTempPath(), "S5A9-channel-landing-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("channel-landing", () =>
        {
        });
        var created = repo.Create(owner, new("Landing", towns.Room, towns.InitialReturn));
        var placed = CharacterSelection.Prepare(repo, owner, created, towns, none, null, false, null, channel: elven);
        That(placed.Position == towns.Room, "频道选角先进赛丽亚房间（艾尔文频道）");
        That(TownTravel.LeaveRoom(placed, towns, none, elven) is { Position: var elvenExit, Reason: "ready" } && elvenExit == elvenSpot,
            "赛丽亚房间出门到艾尔文频道落点 38/0");
        That(TownTravel.LeaveRoom(placed, towns, none) is { Reason: "ready" } plain && plain.Position!.Town == towns.InitialReturn.Town, "无频道时出门按存档返回地区");

        var granRole = repo.Create(owner, new("Gran", towns.Room, towns.InitialReturn));
        var granPlaced = CharacterSelection.Prepare(repo, owner, granRole, towns, none, null, false, null, channel: granfloris);
        That(granPlaced.Position == towns.Room, "主入口格兰之森正式选角保持共享房间");
        var lukeHigh = repo.Create(owner, new("Luke95", towns.Room, towns.InitialReturn) { Level = 95 });
        var lukePlaced = CharacterSelection.Prepare(repo, owner, lukeHigh, towns, none, null, false, null, channel: luke);
        That(lukePlaced.Position == towns.Room, "卢克频道选角先进赛丽亚房间");
        var lukeExit = TownTravel.LeaveRoom(lukePlaced, towns, none, luke);
        That(lukeExit.Position is { Town: 32, Area: 0, X: 474, Y: 234 } && lukeExit.Return == (ChannelAreaLanding.IsChannelBoundTown(towns, 32) ? lukePlaced.Return : towns.ResolveRoomReturn(lukeExit.Position!, lukeExit.Position, 95, none)),
            "赛丽亚房间出门到卢克 32/0 门口，返回地区与原落点规则一致");
        That(TownTravel.LeaveRoom(lukeStayedProbe(repo, owner, towns), towns, none, luke).Position is { Town: var lowTown } && lowTown != 32, "卢克等级不足时按存档返回地区出门");
        var lukeLow = repo.Create(owner, new("Luke85", towns.Room, towns.InitialReturn) { Level = 85 });
        var lukeStayed = CharacterSelection.Prepare(repo, owner, lukeLow, towns, none, null, false, null, channel: luke);
        That(lukeStayed.Position == towns.Room, "正式选角在卢克等级不足时保持共享房间");

        var skyRole = repo.Create(owner, new("Sky", towns.Room, towns.InitialReturn));
        var stayed = CharacterSelection.Prepare(repo, owner, skyRole, towns, none, null, false, null, channel: sky);
        That(stayed.Position == towns.Room, "正式选角在条件不足时保持共享房间");
        Console.WriteLine("ASSERT level-or-quest sky_catle stays 38/1");

        var fiendRole = repo.Create(owner, new("Fiend", towns.Room, towns.InitialReturn));
        var fiendStay = CharacterSelection.Prepare(repo, owner, fiendRole, towns, none, null, false, null, channel: fiend);
        That(fiendStay.Position == towns.Room, "正式选角在超时空之战频道入场条件不足时保持共享房间");
        Console.WriteLine("ASSERT quest-or-level fiendwar stays 38/1");

        var away = repo.Save(owner, placed, towns.InitialReturn, towns.InitialReturn);
        var session = new GameSession(repo, towns, owner, entry: sky);
        session.Select(away.Slot, resumeTown: true);
        That(session.Selected!.Position == towns.InitialReturn, "换频道重连保留已提交城镇");
        Console.WriteLine("ASSERT resumeTown kept 38/0");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: elven);
        session.Select(away.Slot);
        That(session.Selected!.Position == towns.Room, "换频道非快照选角进赛丽亚房间");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: granfloris);
        session.Select(granPlaced.Slot);
        That(session.Selected!.Position == towns.Room, "选角进入主入口格兰之森频道仍出生共享房间");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: luke);
        session.Select(lukePlaced.Slot);
        That(session.Selected!.Position == towns.Room, "选角进入卢克频道先到赛丽亚房间");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: luke);
        session.Select(lukeStayed.Slot);
        That(session.Selected!.Position == towns.Room, "选角进入卢克频道时等级不足留在共享房间");

        session.ReturnToRoster();

        // 安徒恩（type 0x17）客户端 03B87FE0 起始地点为 19/0：直接落战舰门口，不经赛丽亚房间。
        var anton = new ChannelEntry(new(2, 19), 23, "[raid]");
        var antonRole = repo.Create(owner, new("Anton", towns.Room, towns.InitialReturn) { Level = 95 });
        var antonPlaced = CharacterSelection.Prepare(repo, owner, antonRole, towns, none, null, false, null, channel: anton);
        That(antonPlaced.Position is { Town: 19, Area: 0 }, "安徒恩频道选角直接落 19/0");
        That(ChannelAreaLanding.RoomExit(towns, anton, towns.Room, 95, none) is null, "安徒恩不改赛丽亚房间出门");

        // 换频道快照：按上一次选角的频道类型比较 01EB5AF0 谓词。
        var history = new ChannelHistory();
        var fiendDone = new HashSet<int> { 8672 };
        session = new GameSession(repo, towns, owner, entry: luke, channelHistory: history);
        session.Select(lukePlaced.Slot);
        That(session.Selected!.Position == towns.Room && history.Previous(owner.Account) == 32, "记录卢克频道类型");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: fiend, channelHistory: history);
        session.Select(lukePlaced.Slot, resumeTown: true);
        That(session.TownSnapshotResumed && session.Selected!.Position == towns.Room, "卢克→超时空同为团队页签谓词，客户端保留赛丽亚房间快照");
        That(TownTravel.LeaveRoom(session.Selected!, towns, fiendDone, fiend).Position is { Town: 115, Area: 0 }, "超时空频道赛丽亚房间出门到 115/0");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: elven, channelHistory: history);
        session.Select(lukePlaced.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room, "团队→普通频道谓词不同，按选角进赛丽亚房间");
        That(TownTravel.LeaveRoom(session.Selected!, towns, none, elven).Position is { Town: 38, Area: 0 }, "普通艾尔文频道出门到 38/0");
        session.ReturnToRoster();
        Console.WriteLine("ASSERT seria-room landing then channel exit");
        using var stream = typeof(ChannelAreaLanding).Assembly.GetManifestResourceStream("DfoServer.Resources.channel.txt")
            ?? throw new InvalidDataException("缺少内置频道脚本");
        using var reader = new StreamReader(stream);
        var script = ChannelCatalog.Parse(reader.ReadToEnd());
        foreach (var row in script.Channels)
        {
            var entry = new ChannelEntry(new(2, checked((byte)row.Number)), row.Type, row.Group);
            var decision = ChannelAreaLanding.Decide(towns, entry, 95, none);
            if (decision.Spot is { X: 474, Y: 234 } gateSpot)
                That(towns.Areas[(gateSpot.Town, gateSpot.Area)].Gate == gateSpot, "474,234 必须等于该区域 TownCatalog 门口 " + row.Number);
            Console.WriteLine(decision.Spot is null
                ? $"LAND {row.Number} type {row.Type} {row.Group} room reason {decision.Reason}"
                : $"LAND {row.Number} type {row.Type} {row.Group} {decision.Spot.Town}/{decision.Spot.Area} {decision.Spot.X},{decision.Spot.Y} reason {decision.Reason}");
        }
        Console.WriteLine($"PASS {Count - before} 频道落点断言");
    }

    static Character lukeStayedProbe(CharacterRepository repo, AccountOwners.Lease owner, TownCatalog towns) =>
        repo.Create(owner, new("Luke85b", towns.Room, towns.InitialReturn) { Level = 85 });
}
