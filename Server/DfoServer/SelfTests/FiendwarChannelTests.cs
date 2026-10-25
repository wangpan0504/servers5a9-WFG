using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

// 超时空之战入口（卡名超时空漩涡，频道 39，type 45 CHANNEL_FIEND_WAR [fiendwar]）：落点、换频道快照、最后防线选图副本。数据全部读匹配的 Script.pvf，非实机。
internal static class FiendwarChannelTests
{
    const int EnterQuest = 8672;
    const uint Dungeon = 400000016;
    const int RequiredItem = 490007844;
    // chn_fiendwar_normal.dgn [boss map] 6 0 对应 map 400000015，其 [monster] 唯一 [boss] 为 110994（宇宙恶魔:罗什）。
    const int BossRoom = 400000015;
    const int BossMonster = 110994;

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = Count;
        var none = new HashSet<int>();
        var cleared = new HashSet<int> { EnterQuest };
        var fiend = new ChannelEntry(new(2, 39), ChannelAreaLanding.FiendwarType, "[fiendwar]");
        var elven = new ChannelEntry(new(2, 1), 22, "[elven_guard]");
        var sky = new ChannelEntry(new(2, 11), 22, "[sky_catle]");

        That(towns.SpecialChannelQuests.TryGetValue("fiendwar", out int quest) && quest == EnterQuest &&
            towns.SpecialChannelQuests.TryGetValue("anton", out int anton) && anton == 12829,
            "etc/enterablespecialchannel.etc 读出 fiendwar=8672、anton=12829");
        That(towns.Areas[(115, 0)] is { Level: 90, Gate: null } && towns.Areas[(115, 1)].DungeonWorldMap == 201,
            "最后防线 115/0 要求 90 级且无 [gate]，115/1 选图 201");
        That(ChannelAreaLanding.IsChannelBoundTown(towns, 115), "最后防线只经频道进入");
        That(!ChannelAreaLanding.IsChannelBoundTown(towns, 38) && !ChannelAreaLanding.IsChannelBoundTown(towns, 19),
            "普通城镇和安徒恩战舰不算频道专属城镇");

        var missing = ChannelAreaLanding.Decide(towns, fiend, 95, none);
        That(missing.Spot is null && missing.Reason == "channel-quest-required", "未完成 8672 的超时空之战频道留在共享房间");
        var low = ChannelAreaLanding.Decide(towns, fiend, 89, cleared);
        That(low.Spot is null && low.Reason.StartsWith("admission-", StringComparison.Ordinal), "89 级按 115/0 的 need level 留在共享房间: " + low.Reason);
        var landing = ChannelAreaLanding.Decide(towns, fiend, 90, cleared);
        That(landing.Spot == new Location(115, 0, 1301, 428) && landing.Reason == "neighbor-door" && towns.IsPosition(landing.Spot!, cleared),
            "90 级完成 8672 落到主城通往 115/1 的门口中心 1301,428: " + landing.Spot + " " + landing.Reason);
        Console.WriteLine($"ASSERT fiendwar lands {landing.Spot} reason {landing.Reason}");
        var spot = landing.Spot!;

        That(!ChannelAreaLanding.ClientKeepsTownSnapshot(towns, fiend, new(38, 0, 1, 1)) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, elven, spot) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, fiend, spot) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, elven, new(38, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, null, spot),
            "进出 type 0x2D 不保留客户端城镇快照，同类频道互换保留");
        // ACK LOGIN 第 3 字节（ARAD 0233EBEA → 028FA920 → 频道对象 +0x1010）发真实频道类型 0x2D，与 0x36 魔界大战同规则；普通频道为 22 以启用冒险团页。
        That(DfoServer.Network.Builders.ChannelPackets.LoginChannelType(fiend) == 0x2D && DfoServer.Network.Builders.ChannelPackets.LoginChannelType(elven) == 22 &&
            DfoServer.Network.Builders.ChannelPackets.Endpoint("127.0.0.1", 10011, 7, DfoServer.Network.Builders.ChannelPackets.LoginChannelType(fiend))[3] == 0x2D,
            "超时空之战频道 ACK LOGIN 第 3 字节 0x2D");

        string path = Path.Combine(Path.GetTempPath(), "S5A9-fiendwar-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("fiendwar", () => { });
        var street = ChannelAreaLanding.Place(towns, elven, 90, cleared)!;
        var streetReturn = towns.ResolveRoomReturn(street, street, 90, cleared);
        var item = InventorySnapshot.Empty with { Items = [new(0, new(InventoryListType.Main, 121), new(RequiredItem, ItemKind.Material, 1))] };
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        NewCharacter Role(string name, byte level) =>
            CharacterCreation.Prepare(name, characters.Get(0), street, streetReturn, learning.Get) with { Level = level, CompletedQuests = [EnterQuest] };
        var role = repo.Create(owner, Role("Fiend90", 90) with { Inventory = item });
        var bare = repo.Create(owner, Role("Fiend90Bare", 90) with { Inventory = InventorySnapshot.Empty });
        var weak = repo.Create(owner, Role("Fiend89", 89));

        // 22 -> 超时空之战 原生换频道：客户端不留快照，先进赛丽亚房间（返回地区仍是原街道），出门到最后防线 115/0。
        var history = new ChannelHistory();
        var session = new GameSession(repo, towns, owner, entry: fiend, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
            "换入超时空之战频道先进赛丽亚房间，返回地区仍是原街道");
        session.MarkReady();
        That(session.LeaveRoom() && session.Selected!.Position == spot && session.Selected.Return == streetReturn,
            "赛丽亚房间出门到最后防线 115/0，返回地区仍是原街道: " + session.TravelReason);
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: fiend, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(session.TownSnapshotResumed && session.Selected!.Position == spot, "超时空之战频道之间换频道保留最后防线 115/0快照");
        session.ReturnToRoster();
        foreach (var other in new[] { weak })
        {
            // 低等级/未完成任务：无频道落点，赛丽亚房间出门回原街道。
            session = new GameSession(repo, towns, owner, entry: fiend);
            session.Select(other.Slot, resumeTown: true);
            That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
                "89 级换入超时空之战频道按 PVF 等级留在共享房间: " + other.Name);
            session.MarkReady();
            That(session.LeaveRoom() && session.Selected!.Position.Town == street.Town, "89 级换入超时空之战频道按 PVF 等级留在共享房间，出门回原街道: " + session.TravelReason);
            session.ReturnToRoster();
        }
        // 离开最后防线 115/0回普通频道：谓词不同不留快照，进赛丽亚房间，出门到普通频道落点；无落点频道出门回进入前街道。
        session = new GameSession(repo, towns, owner, entry: elven, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room, "离开超时空之战频道回艾尔文频道进赛丽亚房间");
        session.MarkReady();
        That(session.LeaveRoom() && session.Selected!.Position is { } out1 && out1.Town == street.Town && out1.Area == street.Area && out1.X == street.X && out1.Y == street.Y,
            "艾尔文频道赛丽亚房间出门到街道: " + session.TravelReason);
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: fiend, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room, "普通→超时空之战频道进赛丽亚房间");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: sky, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
            "离开超时空之战频道到无落点频道回共享房间，返回原街道");
        session.ReturnToRoster();

        // 最后防线 115/1 选图 201 -> 400000016：90 级、持有 490007844 x1，不扣除。
        var definition = SelfTestCatalogs.Dungeons(pvf).Get(checked((int)Dungeon));
        That(definition.MinimumLevel == 90 && definition.RequiredItem == new DungeonRequiredItem(RequiredItem, 1, 1) && definition.StartFatigue == 30,
            "超时空之战 400000016 写 90 级、[required item] 490007844 1 1 与 [use fatigue only start dungeon] 30");
        That(ChannelTownDungeonRequirement.Check(definition, InventorySnapshot.Empty, itemResources.EpicPieces, DateTimeOffset.UtcNow) == "channel-town-required-item:" + RequiredItem &&
            ChannelTownDungeonRequirement.Check(definition, item, itemResources.EpicPieces, DateTimeOffset.UtcNow) is null &&
            ChannelTownDungeonRequirement.Check(definition, item, null, DateTimeOffset.UtcNow) == "channel-town-required-item-unverifiable",
            "频道城镇入场物品只核对持有");
        Enter(bare, false);
        Enter(role, true);
        // 世界地图“每日进入次数 %d/%d”（字符串 48010）：上限读 DungeonIncountInfo.etc，剩余数靠 NOTI 550 写入。
        var incounts = DungeonIncountCatalog.Load(pvf);
        That(incounts.Find((int)Dungeon) is { ResetType: 0, Limit: 1, Flag: false, Second: 2 } fiendRow && fiendRow.Dungeons.SequenceEqual(new[] { (int)Dungeon }),
            "DungeonIncountInfo 中 400000016 为 重置0、上限1、标志0、第二值2");
        That(incounts.Find(5349) is { ResetType: 0, Limit: 5, Flag: false, Second: 10 } shared && shared.Dungeons.SequenceEqual(new[] { 291100320, 291100322, 5349 }),
            "同一行多副本：291100320/291100322/5349 共用 上限5、第二值10");
        That(incounts.Find(11006) is { Limit: 3, Flag: true, Second: -1 } && incounts.Find(400000015) is null, "11006 上限3标志1第二值-1；Boss 图 400000015 不在表中");
        That(DungeonEnterCountPackets.Info(Dungeon, 1, 1).SequenceEqual(new byte[] { 0x10, 0x84, 0xD7, 0x17, 0x01, 0x01 }),
            "NOTI 550 包体：u32 副本 + u8 剩余 + u8 第二表值");
        int[] limited = [.. incounts.Rows.Where(row => row.Limit > 0).SelectMany(row => row.Dungeons)];
        That(incounts.Rows.Length == 11 && limited.Length == 18 && limited.Distinct().Count() == 18 &&
            limited.Order().SequenceEqual(new[] { 122, 4000, 5097, 5098, 5349, 5428, 8500, 8503, 8504, 8505, 8507, 8508, 8523, 11006, 11007,
                291100320, 291100322, 400000016 }.Order()),
            $"DungeonIncountInfo {incounts.Rows.Length} 行 18 个限次副本全部计数: " + string.Join(',', limited));
        That(incounts.Rows.All(row => row.ResetType == 0) && incounts.Find(8500)!.Dungeons.Length == 6 && incounts.Find(8508)!.CountKey == 8500 &&
            incounts.Find(5349)!.CountKey == 291100320, "全部行重置列为 0；多副本行共用首个副本为次数键");
        // 第二值 -1（无周上限）：客户端 018EC6C6 jle 跳过周显示，日显示取 min(+0x6C68,+0x6C7C)，所以第二字节回填日剩余。
        var tower = incounts.Find(11006)!;
        That(DungeonIncountPolicy.View(11006, tower, new(1, 1)) == new DungeonEnterCount(11006, 2, 2) &&
            DungeonIncountPolicy.CanEnter(tower, new(2, 50)) && !DungeonIncountPolicy.CanEnter(tower, new(3, 3)),
            "无周上限行：周剩余=日剩余，只受日上限 3 约束");
        var group = incounts.Find(8504)!;
        That(DungeonIncountPolicy.Views(group, new(1, 2)).SequenceEqual(new DungeonEnterCount[] { new(8500, 2, 4), new(8503, 2, 4), new(8504, 2, 4),
            new(8505, 2, 4), new(8507, 2, 4), new(8508, 2, 4) }), "同行 6 个公会副本各下发一条 550、剩余相同");
        That(incounts.Find((int)Dungeon)!.WeeklyLimit == 2, "第二值是每周上限（018EC6BD 67086 每周入场次数 %d/%d 的分母）");

        // 其余段（客户端无读者，服务端约定）：[reset info] 0→-1、1→4；账号表 72 条；[sub incount on clear dungeon] 只有 7184；[special weekly reset day] 8523→1。
        var mainDungeons = incounts.Rows.SelectMany(row => row.Dungeons).ToHashSet();
        That(incounts.ResetInfo.Count == 2 && incounts.ResetInfo[0] == -1 && incounts.ResetInfo[1] == 4 &&
            incounts.SubOnClear.SetEquals(new[] { 7184 }) && incounts.SpecialWeeklyResetDay.Count == 1 && incounts.SpecialWeeklyResetDay[8523] == 1,
            "reset info / 通关扣次 / 特殊周重置日按 PVF 读出");
        That(incounts.AccountRows.Length == 72 && incounts.AccountRows.All(row => row.ResetType is 0 or 1 && row.Limit > 0 && !row.Flag) &&
            incounts.AccountRows.Where(row => row.ResetType == 1).Select(row => row.Dungeon).Order()
                .SequenceEqual(new[] { 410, 411, 412, 5340, 5341, 5342, 5343, 5344, 5345, 400000096 }) &&
            incounts.FindAccount(7184) is { ResetType: 0, Limit: 1 } && incounts.FindAccount(400000017) is { ResetType: 0, Limit: 20 } &&
            incounts.FindAccount(5340) is { ResetType: 1, Limit: 10 } && !incounts.AccountRows.Any(row => mainDungeons.Contains(row.Dungeon)),
            "账号表 72 条：重置键 0/1、标志全 0，与角色表副本不重叠");
        // 连带表里只有 6013 不在账号表（无上限）：进入 6013 只给 6006..6012 记次，连带到 6013 的记次忽略。
        That(incounts.EnterAdd.SelectMany(pair => new[] { pair.Entered, pair.Linked }).Where(id => incounts.FindAccount(id) is null).Distinct()
                .SequenceEqual(new[] { 6013 }) && incounts.EnterAdd.Length == 424 &&
            incounts.Linked(400000017).Order().SequenceEqual(Enumerable.Range(400000018, 12)) &&
            incounts.Linked(8014).Order().SequenceEqual(new[] { 8015, 8016 }) && !incounts.Linked(400000016).Any(),
            "连带记次 424 对，除 6013 外都在账号表：400000017 连带 400000018..29");
        // 通关扣次只落在账号表副本；角色表副本全部入场扣次，所以不存在“通关时补发 NOTI 550”的情形。
        That(!incounts.SubOnClear.Any(mainDungeons.Contains) && !incounts.ChargesOnClear((int)Dungeon), "通关扣次副本不在角色表，400000016 入场扣次");
        string dungeonList = pvf.ReadText("dungeon/dungeon.lst");
        var listed7184 = System.Text.RegularExpressions.Regex.IsMatch(dungeonList, @"(?<!\d)7184\s+`");
        Console.WriteLine($"INFO incount 7184 in dungeon.lst: {listed7184}; enter-add pairs {incounts.EnterAdd.Length}");
        // 周期：8523 每周按 [special weekly reset day] 1（按 0=周日 编码即周一，假设）；账号键 0 每日、键 1 周四。
        That(incounts.WeeklyResetDay(8523) == 1 && incounts.WeeklyResetDay((int)Dungeon) == 4 &&
            DungeonIncountPolicy.Period(new DateTimeOffset(2026, 10, 26, 5, 59, 0, TimeSpan.FromHours(8)), 1) == new DungeonIncountPeriod(20261025, 20261019) &&
            DungeonIncountPolicy.Period(new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.FromHours(8)), 1) == new DungeonIncountPeriod(20261026, 20261026),
            "8523 周界为周一 06:00");
        var thursday = new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.FromHours(8));
        That(DungeonIncountPolicy.AccountPeriod(incounts, incounts.FindAccount(400000017)!, thursday) == 20261022 &&
            DungeonIncountPolicy.AccountPeriod(incounts, incounts.FindAccount(5340)!, thursday) == 20261022 &&
            DungeonIncountPolicy.AccountPeriod(incounts, incounts.FindAccount(5340)!, thursday.AddDays(6)) == 20261022 &&
            DungeonIncountPolicy.AccountPeriod(incounts, incounts.FindAccount(400000017)!, thursday.AddDays(1)) == 20261023,
            "账号键 0 每日、键 1 每周四");

        // [required item] 普查：只在频道专属城镇启用，这里记录全表规模以评估泛化影响。
        var dungeonCatalog = SelfTestCatalogs.Dungeons(pvf);
        var selectable = towns.DungeonMaps.Values.Where(map => map.Error is null).SelectMany(map => map.Options).Select(option => (int)option.DungeonId).ToHashSet();
        int declared = 0, consumeFlag = 0, consumeSelectable = 0, holdSelectable = 0;
        var consumeSamples = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(dungeonList, @"(\d+)\s+`"))
        {
            int id = int.Parse(match.Groups[1].Value);
            DungeonDefinition dgn;
            try { dgn = dungeonCatalog.Get(id); }
            catch (Exception) { continue; }
            if (!dgn.DeclaresRequiredItem) continue;
            declared++;
            if (dgn.RequiredItem is { Flag: not 0 } consumed)
            {
                consumeFlag++;
                if (selectable.Contains(id)) { consumeSelectable++; if (consumeSamples.Count < 12) consumeSamples.Add($"{id}:{consumed.ItemId}x{consumed.Count}"); }
            }
            else if (selectable.Contains(id)) holdSelectable++;
        }
        Console.WriteLine($"INFO required item census: declared {declared}, consume-flag {consumeFlag}, consume&selectable {consumeSelectable}, hold&selectable {holdSelectable}; samples {string.Join(',', consumeSamples)}");

        // 日/周边界：UTC+8 06:00 为日界，周四 06:00 为周界。
        var wedLate = DungeonIncountPolicy.Period(new DateTimeOffset(2026, 10, 22, 5, 59, 0, TimeSpan.FromHours(8)));
        var thuOpen = DungeonIncountPolicy.Period(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.FromHours(8)));
        var nextWed = DungeonIncountPolicy.Period(new DateTimeOffset(2026, 10, 28, 23, 0, 0, TimeSpan.FromHours(8)));
        That(wedLate == new DungeonIncountPeriod(20261021, 20261015) && thuOpen == new DungeonIncountPeriod(20261022, 20261022) &&
            nextWed == new DungeonIncountPeriod(20261028, 20261022), $"日周键: {wedLate} {thuOpen} {nextWed}");
        var fiendCount = incounts.Find((int)Dungeon)!;
        That(DungeonIncountPolicy.Current(new(1, 1), thuOpen, nextWed) == new DungeonIncountUsage(0, 1) &&
            DungeonIncountPolicy.Current(new(1, 2), wedLate, thuOpen) == default &&
            DungeonIncountPolicy.View(Dungeon, fiendCount, new(0, 0)) == new DungeonEnterCount(Dungeon, 1, 2) &&
            DungeonIncountPolicy.View(Dungeon, fiendCount, new(1, 1)) == new DungeonEnterCount(Dungeon, 0, 1) &&
            !DungeonIncountPolicy.CanEnter(fiendCount, new(1, 1)) && DungeonIncountPolicy.CanEnter(fiendCount, new(0, 1)) &&
            !DungeonIncountPolicy.CanEnter(fiendCount, new(0, 2)),
            "跨日只清日次数、跨周清两者；日 1 次、周 2 次都要有剩余才可入场");

        // 计数会话：入场扣 1 次日/周次数并消耗 1 个 490007844（[required item] 第三列 1 → 客户端 47702“消耗”），次数用尽后拒绝。
        var counted = repo.Create(owner, Role("Fiend90Cnt", 90) with { Inventory = InventorySnapshot.Empty with
            { Items = [new(0, new(InventoryListType.Main, 121), new(RequiredItem, ItemKind.Material, 2))] } });
        EnterCounted(counted, true, 2, new(Dungeon, 1, 2), new(Dungeon, 0, 1));
        EnterCounted(counted, false, 1, new(Dungeon, 0, 1), null);
        var period = DungeonIncountPolicy.Period(DateTimeOffset.UtcNow);
        // 共用次数：按行首 291100320 记一次后，同行 291100322/5349 同时少 1；其它行不受影响。
        var shared5349 = incounts.Find(5349)!;
        That(repo.CommitDungeonIncountEntry(owner, counted, shared5349.CountKey, period, shared5349, null) is { Usage: { DailyUsed: 1, WeeklyUsed: 1 } },
            "按行首记次");
        var allCounts = new GameSession(repo, towns, owner, entry: fiend, incounts: incounts);
        allCounts.Select(counted.Slot);
        allCounts.MarkReady();
        var listed = allCounts.EnterCounts();
        That(listed.Count == 16 && listed.All(c => c.Dungeon is not (11006 or 11007)) &&
            listed.Where(c => c.Dungeon is 291100320 or 291100322 or 5349).All(c => c is { Daily: 4, Weekly: 9 }) &&
            listed.Single(c => c.Dungeon == Dungeon) == new DungeonEnterCount(Dungeon, 0, 1) &&
            listed.Single(c => c.Dungeon == 8523) == new DungeonEnterCount(8523, 3, 3),
            "通用投影下发16个副本，祭坛专用账本另行投影: " + string.Join(',', listed));
        allCounts.ReturnToRoster();
        // 非频道城镇：普通角色同样下发全部限次副本（次数不再限定最后防线）。
        var plain = repo.Create(owner, Role("Fiend90Pln", 90));
        var plainSession = new GameSession(repo, towns, owner, incounts: incounts);
        plainSession.Select(plain.Slot);
        plainSession.MarkReady();
        That(!ChannelAreaLanding.IsChannelBoundTown(towns, plainSession.Selected!.Position.Town) && plainSession.EnterCounts().Count == 16 &&
            plainSession.EnterCounts().All(c => c.Daily > 0), $"普通城镇 {plainSession.Selected!.Position.Town} 也下发16条通用次数");
        plainSession.ReturnToRoster();
        // 账号次数：进入 400000017 记自身并连带 400000018..29；上限 20 用满后拒绝；同账号其它角色共用；跨日清零。
        var accountRow = incounts.FindAccount(400000017)!;
        int accountDay = DungeonIncountPolicy.AccountPeriod(incounts, accountRow, DateTimeOffset.UtcNow);
        System.Collections.Immutable.ImmutableArray<AccountIncountCharge> accountCharges =
            [new(400000017, accountDay, 20, true), .. incounts.Linked(400000017).Select(linked => new AccountIncountCharge(linked, accountDay, 20, false))];
        bool accountOk = true;
        for (int run = 0; run < 20; run++)
            accountOk &= repo.CommitDungeonIncountEntry(owner, run % 2 == 0 ? counted : plain, 400000017, period, null, null, accountCharges) is not null;
        That(accountOk && repo.LoadAccountDungeonIncount(owner, 400000017, accountDay) == 20 && repo.LoadAccountDungeonIncount(owner, 400000029, accountDay) == 20 &&
            repo.CommitDungeonIncountEntry(owner, counted, 400000017, period, null, null, accountCharges) is null &&
            repo.LoadAccountDungeonIncount(owner, 400000017, accountDay + 1) == 0 && repo.LoadAccountDungeonIncount(owner, 5340, accountDay) == 0,
            "账号次数两角色共用 20 次，连带副本同步，用满拒绝，跨周期清零");
        // 通关扣次：increment=false 只核对；结算时 check=false 补记。
        var clearRow = incounts.FindAccount(7184)!;
        int clearDay = DungeonIncountPolicy.AccountPeriod(incounts, clearRow, DateTimeOffset.UtcNow);
        System.Collections.Immutable.ImmutableArray<AccountIncountCharge> clearCharge = [new(7184, clearDay, 1, true)];
        That(repo.CommitDungeonIncountEntry(owner, counted, 7184, period, null, null, clearCharge, increment: false) is not null &&
            repo.LoadAccountDungeonIncount(owner, 7184, clearDay) == 0 &&
            repo.CommitDungeonIncountEntry(owner, counted, 7184, period, null, null, clearCharge, increment: true, check: false) is not null &&
            repo.LoadAccountDungeonIncount(owner, 7184, clearDay) == 1 &&
            repo.CommitDungeonIncountEntry(owner, counted, 7184, period, null, null, clearCharge, increment: false) is null,
            "7184 入场不扣、通关扣 1，之后入场拒绝");
        That(repo.LoadDungeonIncount(owner, counted.Id, (int)Dungeon, period) == new DungeonIncountUsage(1, 1) &&
            repo.LoadDungeonIncount(owner, counted.Id, (int)Dungeon, period with { Day = period.Day + 1 }) == new DungeonIncountUsage(0, 1) &&
            repo.LoadDungeonIncount(owner, counted.Id, (int)Dungeon, new(period.Day + 1, period.Week + 7)) == default,
            "次数写入 game.db：同日 1/1，次日只剩周次数，跨周清零");

        // 掉落：etc/independent_drop.etc 为 110994 写 [list] 掉落，概率 40000/1000000，次数按难度 1 2 3 4 1，无等级与难度限制。
        var special = DungeonSpecialDropCatalog.Load(pvf);
        That(special.IndependentByMonster.TryGetValue(BossMonster, out var bossDrops) && bossDrops.Length == 1 &&
            bossDrops[0] is { EnemyType: 0, ItemId: 0, MinimumLevel: 0, MaximumLevel: 0, Difficulty: -1, ListFlag: 1 } bossRule &&
            bossRule.Probabilities.All(value => value == 40000) && bossRule.Attempts.SequenceEqual(new[] { 1, 2, 3, 4, 1 }) &&
            bossRule.Items.Length > 0 && bossRule.Items.Any(item => item.ItemId == 401000048 && item.Weight == 900),
            "independent_drop 110994：列表掉落 4%，难度2 抽 3 次，含 401000048 权重900");
        // 精英外观怪：地图行全是 [fixed] [normal]（只有 400000015 的 110994 是 [boss]，见 evidence/fiendwar-rooms），dgn [champion] 0 0 0 0 0 不升精英；
        // 怪物文件写 [is named monster] 1 但 [common champion drop item] 为空。服务端按生成类型 0 取 monster type drop bonusrate。
        That(DungeonChampionRates.For(definition).Length == 0, "400000016 不做精英升级（[champion] 全 0）");
        var monsterDrops = DungeonMonsterDropCatalog.Load(pvf);
        int[] namedLooking = [110979, 110982, 110987, 110973, 110974, 110980, 110977, 110981, 110984, 110709, 110978, 110992];
        That(namedLooking.All(id => monsterDrops.Get(id) is { Items: null or { Count: 0 }, CommonChampionItems: null or { Count: 0 } }),
            "12 个 [is named monster] 怪物没有 [item] 掉落，[common champion drop item] 为空");
        var resourcePolicy = new DungeonResourceDropPolicy(DungeonDropRuleCatalog.Load(pvf), itemResources.DungeonDropItems, monsterDrops.Get,
            itemResources.Items.Get, special);
        var normalPlan = resourcePolicy.Prepare(110979, 120, 0, 2, 120);
        var championPlan = resourcePolicy.Prepare(110979, 120, 1, 2, 120);
        var equipmentPool = normalPlan.Trials[2].RarityPools.SelectMany(pool => pool.Items.Entries).ToArray();
        That(normalPlan.Trials.Length >= 4 && normalPlan.Trials[0].Threshold is 1049 or 1050 && normalPlan.Trials[2].Threshold == 37 &&
            championPlan.Trials[2].Threshold is 377 or 378,
            $"120 级普通类型掉落：金币 {normalPlan.Trials[0].Threshold}/10000、装备 {normalPlan.Trials[2].Threshold}/10000（135×0.2×1.4）；生成类型 1 装备 {championPlan.Trials[2].Threshold}/10000");
        Console.WriteLine($"INFO fiendwar 110979 trials {normalPlan.Trials.Length}: " + string.Join(" | ", normalPlan.Trials.Select(trial =>
            $"{trial.Threshold}/{trial.ProbabilityDenominator}x{trial.Attempts} items {trial.Items?.Entries.Length ?? -1} pools {(trial.RarityPools.IsDefault ? 0 : trial.RarityPools.Sum(pool => pool.Items.Entries.Length))}")));
        Console.WriteLine($"INFO fiendwar world drop 120 {(special.WorldByLevel.TryGetValue(120, out var world120) ? world120.Items.Length + " items" : "none")}");
        Console.WriteLine($"INFO fiendwar normal equipment pool entries {equipmentPool.Length} grades {(equipmentPool.Length == 0 ? "-" : equipmentPool.Min(e => e.Template.Grade) + ".." + equipmentPool.Max(e => e.Template.Grade))}");
        Console.WriteLine($"PASS {Count - before} 超时空之战频道、最后防线与副本断言（非实机）");

        void EnterCounted(Character character, bool accepted, int itemsBefore, DungeonEnterCount before, DungeonEnterCount? after)
        {
            var s = new GameSession(repo, towns, owner, entry: fiend, incounts: incounts);
            s.Select(character.Slot);
            s.MarkReady();
            That(s.Selected!.Position == towns.Room && s.LeaveRoom(), "超时空之战频道选角在赛丽亚房间，出门: " + s.TravelReason);
            var door = towns.Entrance(spot, new(115, 1, 0, 0), cleared)!;
            That(s.Selected!.Position == spot && s.EnterCounts().Single(c => c.Dungeon == Dungeon) == before && s.EnterCounts().Count == 16,
                $"入城下发次数 {string.Join(',', s.EnterCounts())}");
            That(s.Travel(new(115, 1, door.X, door.Y, 5, 115, 0, 0)), "计数会话走到 115/1: " + s.TravelReason);
            int held() => s.Selected!.Inventory.Items.Where(row => row.Core.ItemId == RequiredItem).Sum(row => row.Core.Value);
            That(held() == itemsBefore, "入场前持有 " + held());
            var receipt = s.EnterFormalDungeon(Dungeon, 0, 2, SelfTestCatalogs.Dungeons(pvf), SelfTestCatalogs.Rooms(pvf),
                null, null, _ => 0, itemResources.EpicPieces);
            if (accepted)
                That(receipt is { } r && r.EnterCounts.SequenceEqual(new[] { after!.Value }) && r.ChannelConsumedSlots.IsDefaultOrEmpty && !r.EntryItemConsumedSlots.IsDefaultOrEmpty && held() == itemsBefore - 1,
                    $"计数入场：普通入场票扣 1 个 490007844（频道消耗不重复）、次数 {(receipt is null ? "-" : string.Join(',', receipt.EnterCounts))}: " + s.LastFormalDungeonRejection);
            else
                That(receipt is null && s.LastFormalDungeonRejection == "dungeon-incount-exhausted" && held() == itemsBefore,
                    "次数用尽拒绝且不扣物品: " + s.LastFormalDungeonRejection);
            s.ReturnToRoster();
        }

        void Enter(Character character, bool accepted)
        {
            var s = new GameSession(repo, towns, owner, entry: fiend);
            s.Select(character.Slot);
            s.MarkReady();
            That(s.Selected!.Position == towns.Room && s.LeaveRoom(), "超时空之战频道选角在赛丽亚房间，出门: " + s.TravelReason);
            That(s.Selected!.Position == spot, "正式选角落到最后防线");
            var door = towns.Entrance(spot, new(115, 1, 0, 0), cleared)!;
            That(s.Travel(new(115, 1, door.X, door.Y, 5, 115, 0, 0)) && s.Selected!.Return == streetReturn,
                "最后防线主城走到 115/1，返回地区不变: " + s.TravelReason);
            int before = s.Selected!.Inventory.Items.Where(row => row.Core.ItemId == RequiredItem).Sum(row => row.Core.Value);
            var receipt = s.EnterFormalDungeon(Dungeon, 0, 2, SelfTestCatalogs.Dungeons(pvf), SelfTestCatalogs.Rooms(pvf),
                null, null, _ => 0, itemResources.EpicPieces); // 不传掉落资源：这里只验入场与房间，不生成掉落
            if (accepted)
            {
                // [boss map] 6 0 格在 [map specification] 只写 `map 6 0 400000015`；客户端 02A17ABB 在 Boss 坐标格 boss 列表为空时取 map 列表，
                // 频道专属城镇入口按同一规则把该格投影为 Boss 图。[required item]「物品 1 1」按主线 #510/#511 作普通入场票扣 1 个，不生成任何掉落。
                That(receipt is { } r && r.Entry.BossMap.Id == BossRoom && r.Entry.BossMap.Kind == "boss" &&
                    r.Entry.BossMap.Position == r.Entry.Maze.Bosses.Single() && r.Entry.StartMap.Id == r.Monsters.Room.Map.Id && r.Entry.StartFatigue == 30 &&
                    r.PassiveItems.IsEmpty && r.AssaultConsumedSlots.IsEmpty && r.HellConsumedSlots.IsEmpty &&
                    !r.EntryItemConsumedSlots.IsDefaultOrEmpty && s.Selected!.Inventory.Items.Where(row => row.Core.ItemId == RequiredItem).Sum(row => row.Core.Value) == before - 1,
                    "持有 490007844 进入 400000016，Boss 图 400000015、入场一次扣疲劳 30、按主线 #510/#511 普通入场票扣 1 个、无掉落: " + s.LastFormalDungeonRejection);
                if (receipt is { } entered)
                {
                    var rooms = SelfTestCatalogs.Rooms(pvf);
                    // 入场流程已为每格冻结一个候选图（1 0 / 2 0 / 3 0 / 4 0 有多候选），这里逐格按 PVF 载入房间。
                    var cells = entered.Entry.Maze.MapSpecification!.ToArray();
                    var loaded = cells.Select(map => rooms.Get(map)).ToArray();
                    That(loaded.Length == 7 && loaded.Select(room => room.Map.Position).Distinct().Count() == 7 &&
                        loaded.Single(room => room.Map.Id == BossRoom).Monsters.Single(m => m.MonsterId == BossMonster).RankKind == "[boss]" &&
                        loaded.Single(room => room.Map.Id == BossRoom).Monsters.Count(m => m.MonsterId == BossMonster) == 1 &&
                        loaded.SelectMany(room => room.Monsters).Count(m => m.MonsterId == BossMonster) == 1,
                        $"400000016 入场 {loaded.Length} 格房间全部按 PVF 载入（{string.Join(',', loaded.Select(room => room.Map.Id + ":" + room.Monsters.Count))}），唯一 [boss] 110994 宇宙恶魔:罗什在 400000015");
                }
            }
            else
                That(receipt is null && s.LastFormalDungeonRejection == "channel-town-required-item:" + RequiredItem,
                    "缺 490007844 时拒绝超时空之战: " + s.LastFormalDungeonRejection);
            s.ReturnToRoster();
        }
    }
}
