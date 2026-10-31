using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

// 魔界大战频道（频道 41，type 54 CHANNEL_EVILDOM_WAR，分组 [evildom_war]，名称键 062）：落点、换频道快照、艾尔丁纪念馆 67 选图副本。
// 数据全部读匹配的 Script.pvf，非实机。
internal static class EvildomWarChannelTests
{
    const byte Town = 67;
    const int WorldMap = 154;

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = Count;
        var none = new HashSet<int>();
        var evildom = new ChannelEntry(new(2, 41), ChannelAreaLanding.EvildomWarType, "[evildom_war]");
        var elven = new ChannelEntry(new(2, 1), 22, "[elven_guard]");
        var sky = new ChannelEntry(new(2, 11), 22, "[sky_catle]");
        // 67/0 的 [gate] 474 234 在 twn 里是独立标签而非 `[gate]` 字符串，TownCatalog 不读它；落点走通用的相邻门口反推。
        var gate = new Location(Town, 0, 445, 377);
        // 67/0 导入 gate_seria.map，是房间式区域：落进去玩家还要再走一次门。67/0 自己的门 295,327 300x100 通往 67/1；
        // 67/1 回 67/0 的零尺寸标记 581,138 在门矩形 581,139 150x30 之外不可站，TownCatalog.Entrance 因此给不出 67/1 落点，
        // 只能取 67/1 侧门矩形中心作为从 67/0 进广场的到达点（线上出门要按两次门，Issue #833）。
        static Location DoorCenter(TownCatalog towns, byte town, byte area, byte fromArea) =>
            towns.Areas[(town, area)].Default.Links.Where(l => l.Town == town && l.Area == fromArea && l.Gate.Door)
                .Select(l => new Location(town, area, (ushort)(l.Gate.X + l.Gate.Width / 2), (ushort)(l.Gate.Y + l.Gate.Height / 2))).Single();
        var hall = DoorCenter(towns, Town, 1, 0);
        var waiting = DoorCenter(towns, Town, 2, 1);
        var hallBack = DoorCenter(towns, Town, 1, 2);

        That(towns.Areas[(Town, 0)] is { Level: 95, Quests.Length: 0, Gate: null } &&
            towns.Areas[(Town, 1)].Level == 95 && towns.Areas[(Town, 2)] is { Level: 95, DungeonWorldMap: WorldMap },
            "艾尔丁纪念馆 67/0 要求 95 级、无任务、目录无门口，67/2 选图 154");
        That(ChannelAreaLanding.IsChannelBoundTown(towns, Town), "艾尔丁纪念馆 67 只经频道进入");
        That(!ChannelAreaLanding.IsChannelBoundTown(towns, 68), "剧情版艾尔丁纪念馆 68 可从灰色墓地走入，不算频道专属城镇");
        int[] dungeons = [.. Enumerable.Range(5416, 12), .. Enumerable.Range(5436, 6)];
        var options = towns.DungeonMaps[WorldMap];
        That(options.Error is null && options.Options.Select(option => option.DungeonId).Order().SequenceEqual(dungeons) &&
            options.Options.All(option => option.QuestId == -1),
            "选图 154 列出 5416–5427、5436–5441，全部无任务条件");

        var low = ChannelAreaLanding.Decide(towns, evildom, 94, none);
        That(low.Spot is null && low.Reason.StartsWith("admission-", StringComparison.Ordinal), "94 级按 67/1 need level 留在共享房间: " + low.Reason);
        var landing = ChannelAreaLanding.Decide(towns, evildom, 95, none);
        That(landing.Spot == hall && landing.Reason == "room-door-center" && !towns.IsRoom(hall, none),
            "95 级出门一次直接落到 67/1 广场 656,154，不再先落房间式 67/0: " + landing.Spot + " " + landing.Reason);
        Console.WriteLine($"ASSERT evildom_war lands {landing.Spot} reason {landing.Reason}");

        // ACK LOGIN 正文 [结果, 年龄, 网吧, 频道类型, ...]：ARAD 0233EBEA 读第 3 字节写频道对象 +0x1010。
        byte[] plain = DfoServer.Network.Builders.ChannelPackets.Endpoint("127.0.0.1", 10011, 7);
        byte[] typed = DfoServer.Network.Builders.ChannelPackets.Endpoint("127.0.0.1", 10011, 7,
            DfoServer.Network.Builders.ChannelPackets.LoginChannelType(evildom));
        That(DfoServer.Network.Builders.ChannelPackets.LoginChannelType(evildom) == 0x36 &&
            DfoServer.Network.Builders.ChannelPackets.LoginChannelType(elven) == 22 &&
            DfoServer.Network.Builders.ChannelPackets.LoginChannelType(sky) == 22 &&
            plain[3] == 0 && typed[3] == 0x36 && typed.Length == plain.Length &&
            typed.Where((b, i) => i != 3).SequenceEqual(plain.Where((b, i) => i != 3)),
            "魔界大战频道 ACK LOGIN 第 3 字节发频道类型 0x36（RaidModeSelectWindow 下标 3），普通频道发 22，关联令牌及其余字节不变");

        That(!ChannelAreaLanding.ClientKeepsTownSnapshot(towns, evildom, new(38, 0, 1, 1)) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, elven, gate) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, evildom, gate) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, elven, new(38, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, null, gate),
            "进出 type 0x36 不保留客户端城镇快照，同类频道互换保留");
        // 与超时空之战（type 0x2D，落点最后防线 115）互换：01EB5AF0 的 vt+0x28（028FA400 集合）与 vt+0x4C 都不一致，同样不保留快照。
        var fiendwar = new ChannelEntry(new(2, 39), ChannelAreaLanding.FiendwarType, "[fiendwar]");
        That(!ChannelAreaLanding.ClientKeepsTownSnapshot(towns, fiendwar, gate) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(towns, evildom, new(115, 0, 1, 1)) &&
            ChannelAreaLanding.ClientKeepsTownSnapshot(towns, fiendwar, new(115, 0, 1, 1)),
            "魔界大战与超时空之战频道互换不保留快照，超时空之战之间互换保留");
        // 房间式区域本城门口规则不触及最后防线 115：115 没有房间式区域，且仍是频道专属城镇。
        That(towns.Areas.Values.Where(a => a.Town == 115).All(a => !towns.IsRoom(new(115, a.Area, 0, 0), none)) &&
            ChannelAreaLanding.IsChannelBoundTown(towns, 115),
            "最后防线 115 各区域都不是房间式区域，频道专属城镇判定不变");

        string path = Path.Combine(Path.GetTempPath(), "S5A9-evildom-" + Guid.NewGuid().ToString("N"), "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("evildom", () => { });
        var street = ChannelAreaLanding.Place(towns, elven, 95, none)!;
        var streetReturn = towns.ResolveRoomReturn(street, street, 95, none);
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        NewCharacter Role(string name, byte level) =>
            CharacterCreation.Prepare(name, characters.Get(0), street, streetReturn, learning.Get) with { Level = level, Inventory = InventorySnapshot.Empty };
        var role = repo.Create(owner, Role("Evildom95", 95));
        var weak = repo.Create(owner, Role("Evildom94", 94));

        // 22 -> 魔界大战 原生换频道：客户端不留快照，先进赛丽亚房间（返回地区仍是原街道），出门直接到艾尔丁纪念馆 67/1 广场。
        var history = new ChannelHistory();
        var session = new GameSession(repo, towns, owner, entry: evildom, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
            "换入魔界大战频道先进赛丽亚房间，返回地区仍是原街道");
        session.MarkReady();
        That(session.LeaveRoom() && session.Selected!.Position == hall && session.Selected.Return == streetReturn,
            "赛丽亚房间出门一次到艾尔丁纪念馆 67/1 广场，返回地区仍是原街道: " + session.TravelReason);
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: evildom, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(session.TownSnapshotResumed && session.Selected!.Position == hall, "魔界大战频道之间换频道保留艾尔丁纪念馆 67/1 快照");
        session.ReturnToRoster();
        foreach (var other in new[] { weak })
        {
            // 低等级/未完成任务：无频道落点，赛丽亚房间出门回原街道。
            session = new GameSession(repo, towns, owner, entry: evildom);
            session.Select(other.Slot, resumeTown: true);
            That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
                "94 级换入魔界大战频道按 PVF 等级留在共享房间: " + other.Name);
            session.MarkReady();
            That(session.LeaveRoom() && session.Selected!.Position.Town == street.Town, "94 级换入魔界大战频道按 PVF 等级留在共享房间，出门回原街道: " + session.TravelReason);
            session.ReturnToRoster();
        }
        // 离开艾尔丁纪念馆 67/0回普通频道：谓词不同不留快照，进赛丽亚房间，出门到普通频道落点；无落点频道出门回进入前街道。
        session = new GameSession(repo, towns, owner, entry: elven, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room, "离开魔界大战频道回艾尔文频道进赛丽亚房间");
        session.MarkReady();
        That(session.LeaveRoom() && session.Selected!.Position is { } out1 && out1.Town == street.Town && out1.Area == street.Area && out1.X == street.X && out1.Y == street.Y,
            "艾尔文频道赛丽亚房间出门到街道: " + session.TravelReason);
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: evildom, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room, "普通→魔界大战频道进赛丽亚房间");
        session.ReturnToRoster();
        session = new GameSession(repo, towns, owner, entry: sky, channelHistory: history);
        session.Select(role.Slot, resumeTown: true);
        That(!session.TownSnapshotResumed && session.Selected!.Position == towns.Room && session.Selected.Return == streetReturn,
            "离开魔界大战频道到无落点频道回共享房间，返回原街道");
        session.ReturnToRoster();

        // 67/0 -> 67/1 -> 67/2，选图 154 逐个尝试正式入场，只记录通用入场计划的结论，不改入场规则。
        That(towns.IsRoom(gate, none) && !towns.IsRoom(hall, none) && !towns.IsRoom(waiting, none),
            "67/0 是房间式门口区域，67/1、67/2 是街道");
        That(towns.Entrance(gate, new(Town, 1, 0, 0), none) is null && hall == new Location(Town, 1, 656, 154) &&
            waiting == new Location(Town, 2, 40, 249) && towns.IsPosition(hall, none) && towns.IsPosition(waiting, none) &&
            DoorCenter(towns, Town, 0, 1) == gate,
            "67/1 回 67/0 只有不可站的零尺寸标记，门矩形中心可站；67/0 门中心即落点 445,377");
        That(towns.CanTravel(gate, hall, 95, none, streetReturn) && !towns.CanTravel(gate, waiting, 95, none, streetReturn) &&
            !towns.CanTravel(towns.Room, hall, 95, none, streetReturn) && !towns.CanTravel(gate, hall, 94, none, streetReturn),
            "67/0 自己的门可去 67/1（返回地区仍是原街道）；没有门的 67/2、共享房间和 94 级仍拒绝");
        {
            var s = new GameSession(repo, towns, owner, entry: evildom);
            s.Select(role.Slot);
            s.MarkReady();
            That(s.Selected!.Position == towns.Room && s.LeaveRoom(), "魔界大战频道选角在赛丽亚房间，出门: " + s.TravelReason);
            That(s.Selected!.Position == hall, "出门一次即到 67/1 广场，不需要第二次门");
            var unchanged = s.Selected;
            That(!s.Travel(new(Town, 2, waiting.X, waiting.Y, 5, Town, 1, 0)) &&
                s.TravelReason == "evildom-war-party-required" && ReferenceEquals(s.Selected, unchanged),
                "未组队不能进入等待区，不写位置或重新传送当前地图");
            BindParty(s);
            That(s.Travel(new(Town, 2, waiting.X, waiting.Y, 5, Town, 1, 0)) &&
                s.Travel(new(Town, 1, hallBack.X, hallBack.Y, 5, Town, 2, 0)) && s.Travel(new(Town, 0, gate.X, gate.Y, 5, Town, 1, 0)) &&
                s.Travel(new(Town, 1, hall.X, hall.Y, 5, Town, 0, 0)) && s.Selected!.Position == hall && s.Selected.Return == streetReturn,
                "67/1→67/2→67/1→67/0→67/1 往返，返回地区保持进入前街道: " + s.TravelReason);
            s.ReturnToRoster();
        }
        var results = new Dictionary<int, string>();
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        foreach (int dungeon in dungeons)
        {
            var s = new GameSession(repo, towns, owner, entry: evildom);
            s.Select(role.Slot);
            s.MarkReady();
            That(s.Selected!.Position == towns.Room && s.LeaveRoom(), "魔界大战频道选角在赛丽亚房间，出门: " + s.TravelReason);
            That(s.Selected!.Position == hall, "正式选角出门一次落到艾尔丁纪念馆 67/1 广场");
            BindParty(s);
            That(s.Travel(new(Town, 2, waiting.X, waiting.Y, 5, Town, 1, 0)) && s.Selected!.Return == streetReturn,
                "67/1 走到选图区域 67/2，返回地区不变: " + s.TravelReason);
            var receipt = s.EnterFormalDungeon(checked((uint)dungeon), 0, 0, catalog, rooms, null, itemResources.Items, _ => 0, itemResources.EpicPieces);
            results[dungeon] = receipt is null ? s.LastFormalDungeonRejection ?? "null" : "entered";
            Console.WriteLine($"EVILDOM {dungeon} {results[dungeon]}");
            s.ReturnToRoster();
        }
        void BindParty(GameSession s)
        {
            var parties = new DfoServer.Game.Party.PartyService(repo);
            s.CurrentParty = () => parties.Current(s);
            That(parties.SetInformation(s, 0, DfoServer.Game.Party.PartySettings.Default with { Kind = 5, KindArgument = 0 }).Accepted,
                "等待区回归使用真实单成员魔界大战队伍");
        }
        // 67/2 的 TheEvildomWar_Town00.map 只有通往 67/1 的 movable 行，没有 -1 -1 选图门（剧情版 68/2 Waiting 图有
        // 1076 155 50 210 -1 -1，最后防线 115/1 有 1260 82 50 250 -1 -1）；可走区域只有 virtual movable area 16 154 1100 190，
        // 所以客户端在 67/2 走到 x≈1116 就停住，也不会因走路打开选图（实机 2026-10-21 日志无 ENTER_SELECT_DUNGEON）。
        // 该图的入口是 NPC：67/1 无间者伊奇 100000147 带 [create evildom war]/[enter evildom war waiting area]，
        // 67/2 归来者芮兹 100000148 带 [start evildom war]/[leave evildom war waiting area]。实机点伊奇建魔界大战时客户端发
        // C2S CREATE_RAID(0x298)只是旧泛用窗口的历史线索，不代表当前Party建队失败；当前CMD12 kind5可建队。
        // 专用1652接入后仍不能借普通门或CREATE_RAID类型6绕过开战与专用选图身份。
        var waitingArea = towns.Areas[(Town, 2)].Default;
        That(waitingArea.Bounds.Length == 1 && waitingArea.Bounds[0].Bounds is { X: 16, Y: 154, Width: 1100, Height: 190 } &&
            waitingArea.Links.Length == 1 && waitingArea.Npcs.Any(npc => npc.Id == 100000148) &&
            towns.Areas[(Town, 1)].Default.Npcs.Any(npc => npc.Id == 100000147) &&
            towns.Areas[(68, 2)].Default.DungeonGates.Length == 1 && towns.Areas[(115, 1)].Default.DungeonGates.Length == 1,
            "67/2 只有 16..1116 可走区域和回 67/1 的门；剧情版 68/2 与最后防线 115/1 各有一个选图门");
        That(pvf.ReadText("npc/100000148_returned_laygin.npc").Contains("`[start evildom war]`") &&
            pvf.ReadText("npc/100000147_ikki_the_vikki.npc").Contains("`[create evildom war]`"),
            "魔界大战由 NPC 角色开局：伊奇 [create evildom war]、芮兹 [start evildom war]");

        That(results.Count == dungeons.Length && results.Values.All(r => r == "evildom-war-start-and-selection-required") &&
            towns.Areas[(Town, 2)].Default.DungeonGates.Length == 0,
            "67/2 地图没有选图门，裸16不能绕过魔界大战开战与选图身份");
        CreateRaidRequests();
        Console.WriteLine($"PASS {Count - before} 魔界大战频道、艾尔丁纪念馆与副本断言（非实机）");
    }

    // 实机两次 C2S664（2026-10-21 00:12:12 / 00:38:27 UTC+8，频道 41 的 67/1）按 0216E156 发送点布局解码；畸形长度拒绝。
    static void CreateRaidRequests()
    {
        var first = Network.Parsers.RaidRequests.CreateRaid(Convert.FromHexString("060300000031323300000007"));
        That(first is { RaidType: 6, Name: "123", Field210: 0, Field211: 0, Field214: 0, Flags: 7 },
            "CREATE_RAID 实机包 1：类型 6、队名 123、三窗口字段 0、标志 7");
        var second = Network.Parsers.RaidRequests.CreateRaid(Convert.FromHexString("060600000031323332313300000007"));
        That(second is { RaidType: 6, Name: "123213", Flags: 7 }, "CREATE_RAID 实机包 2：队名 123213");
        That(Network.Builders.RaidPackets.CreateFailure().SequenceEqual(new byte[] { 0, 0x13, 0 }) &&
            Network.Builders.RaidPackets.CreateUnavailable == 0x13,
            "ACK664 失败体 = 公共前缀 00 + error:u16 0x13（02343F61 → dstr 0x7D3C=32060）");
        foreach (string bad in new[] { "06", "0603000000313233000000", "06030000003132330000000700", "06FFFFFFFF00000007" })
        {
            bool rejected = false;
            try { Network.Parsers.RaidRequests.CreateRaid(Convert.FromHexString(bad)); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, $"CREATE_RAID 畸形包 {bad} 拒绝");
        }
    }
}
