using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Raids;
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
        var pvp = new ChannelEntry(new(2, 101), 24, "[pvp]");
        foreach (var (group, area) in new[] { ("[impossible]", (byte)2), ("[ancient]", (byte)0) })
        {
            var channel = new ChannelEntry(new(2, 24), 11, group);
            if (!towns.Areas.TryGetValue((17, area), out var destination))
            {
                That(ChannelAreaLanding.Place(towns, channel, 95, none) is null, "缺大厅资源不能假装已有可用远古落点");
                continue;
            }
            var completed = destination.Quests.Concat(destination.AnyQuests).ToHashSet();
            That(ChannelAreaLanding.Place(towns, channel, 95, completed) is { Town: 17 } spot && spot.Area == area,
                "异界频道到船尾、远古频道先进赛丽亚房间");
            // 17/0 是圣者之鸣号的赛丽亚房间，本身没有等级/任务准入；等级核对仍由船尾 17/2 这一档覆盖。
            if (area == 2)
                That(ChannelAreaLanding.Place(towns, channel, 1, completed) is null, "特殊入口仍核对地区等级");
        }

        // 远古入口在船头 17/3 的镇魂曲门后：17/5 自身只有回船头与本地 -1 -1 副本门两行，直接落 17/5 会跳过赛丽亚房间，
        // 而从门口回甲板时又正好踩在船头的门口矩形上。落点必须与圣者之鸣号一致，17/0 → 17/1 → 17/3 → 17/5 步行链完整。
        var ancientChannel = new ChannelEntry(new(2, 24), 11, "[ancient]");
        var sainthornChannel = new ChannelEntry(new(2, 26), 26, "[sainthorn]");
        var ancientSpot = ChannelAreaLanding.Place(towns, ancientChannel, 95, none);
        That(ancientSpot is { Town: 17, Area: 0 } && ancientSpot == ChannelAreaLanding.Place(towns, sainthornChannel, 95, none),
            "远古频道与圣者之鸣号同落赛丽亚房间 17/0，实际=" + ancientSpot);
        // 步行链依赖镇17 Area5 与船头门的资源增量；未安装时只报告落点，不把资源缺失算成代码回归。
        if (towns.Areas.ContainsKey((17, 1)) && towns.Areas.ContainsKey((17, 3)) && towns.Areas.ContainsKey((17, 5)))
            That(towns.Areas[(17, 0)].Map(none).Links.Any(link => link.Town == 17 && link.Area == 1) &&
                towns.Areas[(17, 1)].Map(none).Links.Any(link => link.Town == 17 && link.Area == 3) &&
                towns.Areas[(17, 3)].Map(none).Links.Any(link => link.Town == 17 && link.Area == 5),
                "赛丽亚房间经甲板、船头步行可达镇魂曲大厅 17/5");

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
        foreach (string group in new[] { "[harlem]", "[east harlem]" })
        {
            var entry = harlem with { Group = group };
            That(ChannelAreaLanding.Place(towns, entry, 95, none) is null &&
                ChannelAreaLanding.Place(towns, entry, 89, new HashSet<int> { 5936 }) is null,
                "黑市落点仍要求5936与90级，不因修正区域绕过准入");
            foreach (var done in new[] { new HashSet<int> { 5936 }, new HashSet<int> { 5936, 6045, 6356 } })
            {
                var landing = ChannelAreaLanding.Place(towns, entry, 95, done);
                That(landing is { Town: 54, Area: 1 } && towns.IsPosition(landing, done), "两个哈林分组均使用正式黑市街道及其当前阶段门口");
                That(towns.Areas[(54, 1)].Map(done).Npcs.Any(npc => npc.Id == 100000175) == done.Contains(6045),
                    "解放后地图放置6356变身的乔斯拉，Before阶段不提前放置");
            }
        }
        That(!towns.Areas[(54, 0)].Map(new HashSet<int> { 5936, 6045, 6356 }).Npcs.Any(npc => npc.Id == 100000175),
            "旧黑市0区固定Before地图，不能承接乔斯拉的解放后身份");
        That(ChannelAreaLanding.Place(towns, fiend, 95, none) is null, "未完成入场任务 8672 的超时空之战频道留在共享房间");
        That(ChannelAreaLanding.Place(towns, fiend, 95, new HashSet<int> { 8672 }) is { Town: 115, Area: 0 }, "完成 8672 的超时空之战频道落到最后防线 115/0");
        That(towns.Areas.ContainsKey((10, 0)) && towns.Areas[(10, 0)].Gate is not null, "决斗场城镇 10 在目录里且写有门口");
        That(ChannelAreaLanding.Place(towns, pvp, 1, none) is null, "决斗场等级不足时留在共享房间");
        That(ChannelAreaLanding.Place(towns, pvp, 95, none) is { Town: 10, Area: 0, X: 474, Y: 234 }, "决斗场等级达到后落到目录门口");
        var unmatched = new ChannelEntry(new(2, 47), 8, "[pvp]");
        That(ChannelAreaLanding.Place(towns, unmatched, 1, none) is null &&
            ChannelAreaLanding.Place(towns, unmatched, 95, none) == ChannelAreaLanding.Place(towns, pvp, 95, none),
            "无双频道沿原决斗地区落点与等级门禁，不能按普通返回地区出门");
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
        var lukeHigh = repo.Create(owner, new("Luke95", towns.Room, towns.InitialReturn) { Level = 95, CompletedQuests = [2903, 4872] });
        var lukePlaced = CharacterSelection.Prepare(repo, owner, lukeHigh, towns, none, null, false, null, channel: luke);
        That(lukePlaced.Position == towns.Room, "卢克频道选角先进赛丽亚房间");
        var lukeExit = TownTravel.LeaveRoom(lukePlaced, towns, none, luke);
        That(lukeExit.Position is { Town: 32, Area: 0, X: 474, Y: 234 } && lukeExit.Return == (ChannelAreaLanding.IsChannelBoundTown(towns, 32) ? lukePlaced.Return : towns.ResolveRoomReturn(lukeExit.Position!, lukeExit.Position, 95, none)),
            "赛丽亚房间出门到卢克 32/0 门口，返回地区与原落点规则一致");
        That(TownTravel.LeaveRoom(lukeStayedProbe(repo, owner, towns), towns, none, luke).Position is { Town: var lowTown } && lowTown != 32, "卢克等级不足时按存档返回地区出门");
        var lukeLow = repo.Create(owner, new("Luke85", towns.Room, towns.InitialReturn) { Level = 85 });
        var lukeStayed = CharacterSelection.Prepare(repo, owner, lukeLow, towns, none, null, false, null, channel: luke);
        That(lukeStayed.Position == towns.Room, "正式选角在卢克等级不足时保持共享房间");

        // #740：普通频道的赛丽亚房间出口只认角色记住的返回地区。天空之城频道原先把房间门改到 40/3 西海岸，
        // 与 Docs/模块/城镇.md 第 4 条“普通房间出口必须匹配持久返回地区”冲突。
        var skyArea = towns.Areas[(40, 3)];
        var skyDone = new HashSet<int>(skyArea.Quests);
        if (skyArea.AnyQuests.Length > 0) skyDone.Add(skyArea.AnyQuests[0]);
        That(ChannelAreaLanding.Place(towns, sky, 95, skyDone) is { Town: 40, Area: 3 }, "天空之城频道在等级与任务满足时确有 40/3 落点（夹具有效）");
        // 用户场景是“在诺斯玛尔下线”：真实 95 级存档的 Return 是走进该地区时解析出的持久点（线上库为 39/0 596,144），不是 null。
        // 39/0 的准入要求任务 3156（LV17），空任务集会在 DestinationAdmission 处被挡、退回初始艾尔文，夹具必须带上该地区自身要求。
        // 39/2 子区域没有回连房间的门，只靠这个持久点回退到 39/0 的门口。
        var northArea = towns.Areas[(39, 0)];
        var northDone = new HashSet<int>(skyDone);
        northDone.UnionWith(northArea.Quests);
        if (northArea.AnyQuests.Length > 0) northDone.Add(northArea.AnyQuests[0]);
        var northSaved = towns.ResolveRoomReturn(new(39, 0, 0, 0), null, 95, northDone);
        That(northSaved is { Town: 39 } northStreet && towns.IsPosition(northStreet, northDone),
            "诺斯玛尔 39/0 街道可解析为房间返回地区，实际=" + northSaved);
        var northRole = repo.Create(owner, new("North", new(39, 2, 552, 218), northSaved) { Level = 95 });
        var northPlaced = CharacterSelection.Prepare(repo, owner, northRole, towns, northDone, null, false, null, channel: sky);
        That(northPlaced.Position == towns.Room, "天空之城频道选角进共享房间");
        That(northPlaced.Return is { Town: 39 } northReturn && towns.IsPosition(northReturn, northDone),
            "天空之城频道选角记住诺斯玛尔的返回地区，实际=" + northPlaced.Return);
        var northExit = northPlaced.Return is { } keptReturn ? towns.Entrance(towns.Room, keptReturn, northDone) : null;
        var skyLeave = TownTravel.LeaveRoom(northPlaced, towns, northDone, sky);
        That(skyLeave is { Reason: "ready", Position: var skyDoor } && skyDoor == northExit,
            "天空之城频道出门按记住的诺斯玛尔地区，而不是频道落点 40/3，实际=" + skyLeave.Position);
        var elvenLeave = TownTravel.LeaveRoom(northPlaced, towns, northDone, elven);
        That(elvenLeave is { Reason: "ready", Position: var elvenDoor } && elvenDoor == northExit,
            "艾尔文频道出门同样按记住的诺斯玛尔地区，实际=" + elvenLeave.Position);
        // 1379 房间返回城镇投影与 1426 出房共用 RoomExit，普通频道不再取频道落点。
        That(ChannelAreaLanding.RoomExit(towns, sky, towns.Room, 95, northDone) is null, "普通频道的房间返回地区投影不再取频道落点");

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
        bool lukeRejected = false;
        try { session.Select(lukeStayed.Slot); }
        catch (RaidAdmissionException ex) when (ex.Message == "luke-level-required") { lukeRejected = true; }
        var lukeUnchanged = repo.LoadCharacter(owner, lukeStayed.Slot);
        That(lukeRejected && session.Selected is null && lukeUnchanged.Position == lukeStayed.Position &&
            lukeUnchanged.Return == lukeStayed.Return, "卢克等级不足在正式选角前拒绝，不能靠共享房间落点绕过准入或改写位置");

        session.ReturnToRoster();

        // 安徒恩（type 0x17）客户端 03B87FE0 起始地点为 19/0：直接落战舰门口，不经赛丽亚房间。
        var anton = new ChannelEntry(new(2, 19), 23, "[raid]");
        var antonRole = repo.Create(owner, new("Anton", towns.Room, towns.InitialReturn) { Level = 95 });
        var antonPlaced = CharacterSelection.Prepare(repo, owner, antonRole, towns, none, null, false, null, channel: anton);
        That(antonPlaced.Position is { Town: 19, Area: 0 }, "安徒恩频道选角直接落 19/0");
        That(ChannelAreaLanding.RoomExit(towns, anton, towns.Room, 95, none) is null, "安徒恩不改赛丽亚房间出门");
        // #740 回归：战舰是频道起始城镇，不是普通城镇的一站。落点沿用进入前的返回地区，
        // 不能把 19/1（819,172 战舰甲板门）记成共享房间的返回地区，否则普通频道房间的 1379 与出门都会落到战舰。
        That(antonPlaced.Return == towns.InitialReturn,
            "安徒恩落点保留普通返回地区，不记战舰街区，实际=" + antonPlaced.Return);
        That(ChannelAreaLanding.PreservesRoomReturn(towns, ChannelAreaLanding.AntonTown) &&
            ChannelAreaLanding.PreservesRoomReturn(towns, 67) && !ChannelAreaLanding.PreservesRoomReturn(towns, 38),
            "频道起始城镇 19 与只能经频道进入的 67 保留返回地区，艾尔文 38 不保留");

        // 旧存档可能把战舰街区记成返回地区（修复前 19/1 819,172）。普通频道选角与频道重连都要按共享房间初始地区修复，
        // 否则房间 1379 与 1426 出门继续指向战舰。
        var stale = repo.Create(owner, new("StaleShip", towns.Room, new Location(19, 1, 819, 172)) { Level = 95 });
        var healed = CharacterSelection.Prepare(repo, owner, stale, towns, none, null, false, null, channel: elven);
        That(healed.Position == towns.Room && healed.Return == towns.InitialReturn,
            "旧存档的战舰返回地区在普通频道选角时按初始地区修复，实际=" + healed.Return);
        var healedExit = TownTravel.LeaveRoom(healed, towns, none, elven);
        That(healedExit is { Reason: "ready", Position.Town: 38 },
            "战舰返回地区修复后普通房间出门回艾尔文 38，实际=" + healedExit.Position);
        var staleSwap = repo.Create(owner, new("StaleSwap", towns.Room, new Location(19, 1, 819, 172)) { Level = 95 });
        var resumed = CharacterSelection.Prepare(repo, owner, staleSwap, towns, none, null, true, null, channel: elven);
        That(resumed.Position == towns.Room && resumed.Return == towns.InitialReturn,
            "换频道保留快照时同样修复战舰返回地区，实际=" + resumed.Return);
        // 战舰内走动（19/0 → 19/1 甲板门）不覆盖进入前的普通返回地区。
        var aboard = repo.Save(owner, healed, new(19, 0, 474, 234), towns.InitialReturn);
        var toDeck = TownTravel.Plan(aboard, new(19, 1, 819, 172, 5, 19, 0, 0), towns, none);
        That(toDeck.Accepted && toDeck.Position is { Town: 19, Area: 1 } && toDeck.Return == towns.InitialReturn,
            "战舰内走到 19/1 不覆盖普通返回地区，实际=" + toDeck.Return);

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
        // #740 房间出口按显式频道清单核对；再战魔界46沿已接入的type53特殊落点，不借用普通返回地区。
        // 47=决斗场无双频道（type 8，33f95e72 接入）、48=帝国竞技场（type 39）同属按频道落点出门，不随普通频道回记住地区。
        // 决斗场普通入口按客户端 etc/pvpchannellist.etc 登记为 101（原 40 已不存在），特殊出口清单随目录取号。
        var specialChannels = new HashSet<int> { 19, 20, 23, 24, 26, 31, 32, 35, 36, 39, 101, 41, 42, 43, 44, 45, 46, 47, 48, 49 };
        var beastChannel = script.Channels.Single(row => row.Number == 49);
        var beastEntry = new ChannelEntry(new(2, 49), beastChannel.Type, beastChannel.Group);
        That(beastChannel.Type == 40 && beastChannel.Group == "[beast]",
            "魔兽48号频道使用独立type40及资源分组");
        That(ChannelAreaLanding.Decide(towns, beastEntry, 95, none).Reason == "channel-quest-required",
            "魔兽落点不能绕过5927引导任务");
        // 魔兽城镇51（中央公园）只有区域2带 `[dungeon gate]` 46 与地图里的 -1/-1 选图区域；落在 51/0（赛丽亚门厅）
        // 或 51/1（中央公园广场）开图都只有底图没有气球。区域2没有 [gate]、也不与共享房间相连，
        // 落点由地图首块可走矩形中心给出（用户 2026-10-28 实测“走进中央公园，一张地图都没有”）。
        var beastDone = new HashSet<int> { 5927 };
        var beastLanding = ChannelAreaLanding.RoomExit(towns, beastEntry, towns.Room, 95, beastDone);
        That(beastLanding is { Town: 51, Area: 2 } && towns.IsPosition(beastLanding, beastDone),
            "完成魔兽引导后共享房间出口落到带选图区域的等待区51/2，实际=" + beastLanding);
        That(towns.Areas[(51, 2)].DungeonWorldMap == 46,
            "魔兽等待区51/2声明 [dungeon gate] 46，五个气球5335–5339挂在该世界地图上");
        That(towns.Areas[(51, 2)].Default.DungeonGates.Length > 0 && towns.Areas[(51, 1)].Default.DungeonGates.Length == 0,
            "只有等待区51/2的地图含 -1/-1 选图区域，中央公园广场51/1没有");
        var tagChannel = script.Channels.Single(row => row.Number == 46);
        That(tagChannel.Type == 53 && tagChannel.Group == "[evildom_gathering]",
            "再战魔界46号频道保持独立type53与资源分组，不按频道编号猜普通语义");
        var ordinaryChannel = script.Channels.Single(row => row.Number == 37);
        That(ordinaryChannel.Type == 22 && ordinaryChannel.Group == "[harlem]" &&
            ChannelAreaLanding.RoomExitUsesRememberedDistrict(new ChannelEntry(new(2, 37), ordinaryChannel.Type, ordinaryChannel.Group)),
            "非TAG普通哈林仍使用角色记住的返回地区");
        foreach (var row in script.Channels)
            That(ChannelAreaLanding.RoomExitUsesRememberedDistrict(new ChannelEntry(new(2, checked((byte)row.Number)), row.Type, row.Group)) ==
                !specialChannels.Contains(row.Number), "房间出口分档 " + row.Number + " type " + row.Type + " " + row.Group);
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
