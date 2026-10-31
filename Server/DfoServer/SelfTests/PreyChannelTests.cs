using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

// 普雷·伊希斯攻坚战频道（频道 42，type 50 CHANNEL_PREY_RAID，分组 [prey]，名称键 054）：候选 59/0、出房 59/1、任务 8935 + Lv95、ACK LOGIN 类型、换频道快照。
// 数据全部读匹配的 Script.pvf，非实机。
internal static class PreyChannelTests
{
    const byte Town = 59;
    const int EnterQuest = 8935;

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = Count;
        var none = new HashSet<int>();
        var cleared = new HashSet<int> { EnterQuest };
        var prey = new ChannelEntry(new(2, 42), ChannelAreaLanding.PreyRaidType, "[prey]");
        var elven = new ChannelEntry(new(2, 1), 22, "[elven_guard]");
        var sky = new ChannelEntry(new(2, 11), 22, "[sky_catle]");
        var fiendwar = new ChannelEntry(new(2, 39), ChannelAreaLanding.FiendwarType, "[fiendwar]");
        var evildom = new ChannelEntry(new(2, 41), ChannelAreaLanding.EvildomWarType, "[evildom_war]");

        That(ChannelAreaLanding.PreyRaidType == 0x32, "CHANNEL_PREY_RAID = 0x32");
        That(towns.SpecialChannelQuests.TryGetValue("prey", out int quest) && quest == EnterQuest,
            "etc/enterablespecialchannel.etc 读出 prey=8935");
        string raid = pvf.ReadText("etc/raid/prey/prey.etc");
        That(raid.Contains("[WAITING ROOM]") && System.Text.RegularExpressions.Regex.IsMatch(raid, @"\[WAITING ROOM\]\s+59\s+2\b"),
            "Prey.etc 等待室 [WAITING ROOM] 59 2");
        That(Enumerable.Range(0, 6).All(area => towns.Areas.TryGetValue((Town, (byte)area), out var a) && a.Level == 95),
            "灰色墓地 59/0..59/5 都在目录且 need level 95");

        // 频道目录候选仍取 59/0；候选可站与共享房间实际出门到街区分别验证。
        var area0 = ChannelAreaLanding.DecideArea(towns, Town, 0, 95, cleared);
        var area2 = ChannelAreaLanding.DecideArea(towns, Town, 2, 95, cleared);
        Console.WriteLine($"ASSERT prey 59/0 {area0.Spot} {area0.Reason}; 59/2 {area2.Spot} {area2.Reason}; channel-bound {ChannelAreaLanding.IsChannelBoundTown(towns, Town)}");
        That(area0.Spot == new Location(Town, 0, 445, 377) && area0.Reason == "neighbor-door" && towns.IsPosition(area0.Spot, cleared),
            "59/0 有可站门口 445,377: " + area0.Spot + " " + area0.Reason);
        That(area2.Spot == new Location(Town, 2, 27, 378) && area2.Reason == "neighbor-door" && towns.IsPosition(area2.Spot, cleared),
            "59/2 有可站门口 27,378: " + area2.Spot + " " + area2.Reason);
        That(ChannelAreaLanding.IsChannelBoundTown(towns, Town), "灰色墓地 59 只经频道进入");
        var spot = area0.Spot!;

        var missing = ChannelAreaLanding.Decide(towns, prey, 95, none);
        That(missing.Spot is null && missing.Reason == "channel-quest-required", "未完成 8935 的普雷频道留在共享房间");
        var low = ChannelAreaLanding.Decide(towns, prey, 94, cleared);
        That(low.Spot is null && low.Reason.StartsWith("admission-", StringComparison.Ordinal), "94 级按 59/0 need level 留在共享房间: " + low.Reason);
        var landing = ChannelAreaLanding.Decide(towns, prey, 95, cleared);
        That(landing.Spot == spot && landing.Reason == area0.Reason, "95 级完成 8935 落到 59/0: " + landing.Spot + " " + landing.Reason);
        Console.WriteLine($"ASSERT prey lands {landing.Spot} reason {landing.Reason}");
        var streetExit = new Location(Town, 1, 1217, 98);
        That(towns.IsRoom(spot, cleared) && towns.DefaultReturn(spot, cleared) == streetExit &&
            ChannelAreaLanding.RoomExit(towns, prey, towns.Room, 95, cleared) == streetExit &&
            !towns.IsRoom(streetExit, cleared) && towns.IsPosition(streetExit, cleared),
            "普雷目录候选 59/0 是地区房间，共享房间一次出门直达可站街区 59/1(1217,98)");

        // ACK LOGIN 第 3 字节：选角前 03B55580 读 +0x1010 判 0x32 → tail[41]（+0x494）。
        byte[] plain = ChannelPackets.Endpoint("127.0.0.1", 10011, 7);
        byte[] typed = ChannelPackets.Endpoint("127.0.0.1", 10011, 7, ChannelPackets.LoginChannelType(prey));
        That(ChannelPackets.LoginChannelType(prey) == 0x32 && typed[3] == 0x32 && plain[3] == 0 && typed.Length == plain.Length &&
            typed.Where((b, i) => i != 3).SequenceEqual(plain.Where((b, i) => i != 3)),
            "普雷频道 ACK LOGIN 第 3 字节发频道类型 0x32，其余字节不变");

        That(!ChannelAreaLanding.ClientKeepsTownSnapshot(towns, prey, new(38, 0, 1, 1)) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, elven, spot) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, prey, spot) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, fiendwar, spot) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, evildom, spot) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, prey, new(115, 0, 1, 1)) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, prey, new(67, 0, 1, 1)),
            "普通↔0x32 不保留客户端城镇快照；0x32 与超时空之战 0x2D 同属 vt+0x28 团队页签谓词，互换保留；与魔界大战 0x36 互换不保留");

        // 卢克 0x20 / 安徒恩 0x17 与普通频道 vt+0x28 不同：普通→团本不记快照，必须按服务端落点入城；团本页签之间互换保留快照。
        var luke = new ChannelEntry(new(2, 20), 0x20, "[luke_raid]");
        var anton = new ChannelEntry(new(2, 19), 0x17, "[raid]");
        That(!ChannelAreaLanding.ClientKeepsTownSnapshot(towns, luke, new(38, 0, 1, 1)) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, anton, new(38, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, fiendwar, new(32, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, prey, new(32, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, luke, new(115, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, luke, new(59, 0, 1, 1)) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, evildom, new(32, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, anton, new(19, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, elven, new(19, 0, 1, 1)),
            "卢克/安徒恩频道按 01EB5AF0 谓词：普通→团本落点，团本页签互换与安徒恩城镇（非专属城镇，旧频道可能是普通）保留快照");

        string path = Path.Combine(Path.GetTempPath(), "S5A9-prey-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("prey", () => { });
        var street = ChannelAreaLanding.Place(towns, elven, 95, cleared)!;
        var streetReturn = new Location(38, 0, 622, 196);
        That(towns.ResolveRoomReturn(street, street, 95, cleared) == streetReturn &&
            !towns.IsRoom(streetReturn, cleared) && towns.IsPosition(streetReturn, cleared),
            "初始普通返回门口固定为可站的艾尔文38/0(622,196)，不从出房结果推导期望");
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        NewCharacter Role(string name, byte level, int[] quests) =>
            CharacterCreation.Prepare(name, characters.Get(0), street, streetReturn, learning.Get) with
            { Level = level, CompletedQuests = quests, Inventory = InventorySnapshot.Empty };
        var role = repo.Create(owner, Role("Prey95", 95, [EnterQuest]));
        var noQuest = repo.Create(owner, Role("Prey95NoQ", 95, []));
        var weak = repo.Create(owner, Role("Prey94", 94, [EnterQuest]));

        // 22 -> 普雷 原生换频道：客户端不留快照，先进赛丽亚房间（返回地区仍是原街道），出门到灰色墓地街区 59/1。
        var history = new ChannelHistory();
        var session = new GameSession(repo, towns, owner, entry: prey, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
            "换入普雷频道先进赛丽亚房间，返回地区仍是原街道");
        session.MarkReady();
        That(session.LeaveRoom() && session.Selected!.Position == streetExit && session.Selected.Return == streetReturn,
            "赛丽亚房间一次出门到灰色墓地 59/1(1217,98)，返回地区全部字段仍是原街道: " + session.TravelReason);
        var exited = session.Selected;
        That(!session.LeaveRoom() && session.TravelReason == "not-in-ready-room" && ReferenceEquals(session.Selected, exited),
            "已到普雷街区后重复出房必须拒绝，位置与返回地区快照不变");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: prey, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(session.TownSnapshotResumed && session.Selected!.Position == streetExit && session.Selected.Return == streetReturn,
            "普雷频道之间换频道保留灰色墓地 59/1 完整位置及原街道返回快照");
        session.ReturnToRoster();
        foreach (var other in new[] { noQuest, weak })
        {
            // 低等级/未完成任务：无频道落点，赛丽亚房间出门回原街道。
            session = new GameSession(repo, towns, owner, entry: prey);
            session.Select(other.Slot, resumeTown: true);
            That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
                "未完成 8935 或 94 级换入普雷频道留在共享房间: " + other.Name);
            session.MarkReady();
            That(session.LeaveRoom() && session.Selected!.Position == streetReturn && session.Selected.Return == streetReturn, "未完成 8935 或 94 级换入普雷频道留在共享房间，出门回原街道: " + session.TravelReason);
            session.ReturnToRoster();
        }
        // 离开灰色墓地 59/1回普通频道：谓词不同不留快照，进赛丽亚房间，普通频道出门均回持久返回地区，不按频道分组覆盖。
        session = new GameSession(repo, towns, owner, entry: elven, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
            "离开普雷街区回普通频道时入共享房间，不能把59/1覆盖为普通返回地区");
        session.MarkReady();
        That(session.LeaveRoom() && session.Selected!.Position == streetReturn && session.Selected.Return == streetReturn,
            "普通频道出门恢复艾尔文38/0(622,196)，完整位置与返回地区均保持: " + session.TravelReason);
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: prey, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
            "普通→普雷频道进赛丽亚房间且保留完整普通返回地区");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: sky, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
            "离开普雷频道到天空之城普通频道回共享房间，返回原街道");
        session.MarkReady();
        That(session.LeaveRoom() && session.Selected!.Position == streetReturn && session.Selected.Return == streetReturn,
            "天空之城普通频道出门也按持久普通返回门口，不覆盖为频道候选");
        session.ReturnToRoster();
        Console.WriteLine($"PASS {Count - before} 普雷频道、灰色墓地落点与准入断言（非实机）");
    }
}