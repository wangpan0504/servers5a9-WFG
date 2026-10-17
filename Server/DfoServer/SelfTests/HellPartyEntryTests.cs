using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using System.Buffers.Binary;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 实验性（#256，深渊派对入场）：依据参考项目 dungeon_hell_party_entry.go / dungeon_hell_room.go / dungeon_info.go，未在95客户端实证。
// 这里只锁定本服的假设行为：请求体解析、PVF 深渊房间/区域声明解析、入场资格、DUNGEON_INFO/START_MAP 的深渊字段、拒绝包形状。
internal static class HellPartyEntryTests
{
    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);

        // 实机样本（server.jsonl 里被拒绝的深渊入场请求）：副本 291100308，[4]=4，[7]=1，[16:20]=6114。
        byte[] sample = Convert.FromHexString("94D6591104000001" + "00FFFF" + "0000000000" + "E2170000" + "00" + "00000000");
        That(sample.Length == 25 && DungeonRequests.TryParseSelection(sample, out var request) &&
            request == new DungeonSelectionRequest(291100308, 4, 0, 1, 0, ushort.MaxValue, 0, 0, 6114, 0, 0),
            "深渊派对实机请求样本：25 字节，[7]=1，[4]=4，[16:20]=6114");
        That(DungeonPackets.HellPartyRejected(0xAD).SequenceEqual(new byte[] { 0, 0xAD, 0, 0 }) &&
            DungeonPackets.HellPartyRejected(0x01).SequenceEqual(new byte[] { 0, 0x01, 0, 0 }) &&
            DungeonPackets.SelectionRejected().SequenceEqual(new byte[] { 0, 9, 0, 0 }),
            "深渊派对拒绝包保持 4 字节形状，普通拒绝包不变");

        // 黑暗都市(291100308)：只有无任务连接的普通 maze 带 [hell dungeon]，seal door 地图 313163、房间坐标 (2,2)；任务 maze 不属于深渊派对。
        var definition = dungeons.Get(291100308);
        var hellMazes = definition.Mazes.Where(maze => maze.HellParty is not null).ToArray();
        That(hellMazes.Length == 1 && hellMazes[0].QuestConnectionTokens.Count == 0 &&
            hellMazes[0].HellParty == new DungeonHellPartyRoom(313163, new(2, 2)) &&
            definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count != 0).All(maze => maze.HellParty is null),
            "291100308 的深渊房间来自普通 maze 的 [seal door map index]/[seal door pos]，任务 maze 不带深渊房间");
        That(DungeonEntryPlan.TryCreate(dungeons, Character(towns, characters, skills, growth, quests, [], out var probe1), 291100308, 0, 0, out var ordinary, out _,
                quests: quests) && ordinary!.HellParty is null && ordinary.HellPartyMode == 0,
            "普通入场不受影响：HellParty 为空");
        probe1.Dispose();

        // 哈林选图区域：[hell dungeon] 1，[hell quest] 6080。
        var area = towns.Areas.Values.First(row => row.Level <= 95 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100308));
        var map = towns.DungeonMaps[area.DungeonWorldMap!.Value];
        That(map.HellDungeon && map.HellQuests.SequenceEqual([6080]),
            "哈林选图区域声明 [hell dungeon] 1 与 [hell quest] 6080");
        That(towns.DungeonMaps.Values.Any(other => !other.HellDungeon) && !towns.DungeonMaps.Values.All(other => other.HellDungeon),
            "并非所有区域都声明深渊派对");

        var done = new HashSet<int>(area.Quests);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(walk.X + walk.Width / 2)), checked((ushort)(walk.Y + walk.Height / 2)));

        // 未完成 [hell quest] 6080：拒绝，原因带 hell-party-quest-required，不改变会话。
        using (var blocked = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, done.Order().ToArray(), position: position))
        {
            var before = blocked.Read();
            That(blocked.Session.EnterFormalDungeon(291100308, 0, 4, dungeons, rooms, hellParty: true) is null &&
                blocked.Session.LastFormalDungeonRejection == "hell-party-quest-required:6080" && blocked.Read().Revision == before.Revision,
                "未完成区域 [hell quest] 时拒绝深渊派对入场（原因 hell-party-quest-required:6080）");
        }

        // 已完成 6080：放行。
        done.Add(6080);
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, done.Order().ToArray(), position: position);
        var character = fixture.Read();
        That(DungeonEntryPlan.TryCreate(dungeons, character, 291100308, 0, 4, out var plan, out var reason, quests: quests, hellParty: true) &&
            plan!.HellParty == new DungeonHellPartyRoom(313163, new(2, 2)) && plan.HellPartyMode == 1 && plan.QuestId == 0 && plan.RouteQuestId == 0 &&
            plan.Maze.HellParty == plan.HellParty && plan.Difficulty == 4,
            "已完成 6080 的角色可规划深渊派对入场：走带深渊房间的普通 maze，questId=0，难度 4 原样保留：" + reason);
        That(!DungeonEntryPlan.TryCreate(dungeons, character, 291100308, 6114, 4, out _, out _, quests: quests, hellParty: true),
            "深渊派对入场不接受任务号（questId 必须为 0）");
        That(!DungeonEntryPlan.TryCreate(dungeons, character, 7142, 0, 0, out _, out _, quests: quests, hellParty: true),
            "没有深渊房间声明的副本无法按深渊派对规划（已知映射才放行，未知拒绝）");

        byte[] information = DungeonPackets.Information(plan!);
        That(information.Length == 36 && BinaryPrimitives.ReadUInt32LittleEndian(information) == 291100308 && information[4] == 4 &&
            information[10] == 2 && information[11] == 2 && BinaryPrimitives.ReadUInt16LittleEndian(information.AsSpan(14)) == 1 &&
            information[18] == 0x0B && BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(20)) == uint.MaxValue,
            "深渊派对 DUNGEON_INFO：[10,11]=深渊房间坐标，[14..15]=1，[18]=0x0B，seed=FFFFFFFF");
        byte[] normalInfo = DungeonPackets.Information(ordinary!);
        That(normalInfo[10] == byte.MaxValue && normalInfo[11] == byte.MaxValue && normalInfo[14] == 0 && normalInfo[18] == 0,
            "普通入场 DUNGEON_INFO 的深渊字段保持原值");
        byte[] start = DungeonPackets.StartMap(plan!, 123u);
        byte[] normalStart = DungeonPackets.StartMap(ordinary!, 123u);
        That(start[7] == 1 && start[22] == 1 && normalStart[7] == 0 && normalStart[22] == byte.MaxValue &&
            start.Length == normalStart.Length,
            "深渊派对 START_MAP：[7]=模式1、fog=1；普通入场保持 0 / FF");

        // 会话级：放行后真正进入副本，收据里保留深渊房间。
        var receipt = fixture.Session.EnterFormalDungeon(291100308, 0, 4, dungeons, rooms, hellParty: true);
        That(receipt is not null && receipt.Entry.HellParty == new DungeonHellPartyRoom(313163, new(2, 2)) && receipt.Entry.IsOrdinary &&
            fixture.Session.LastFormalDungeonRejection is null,
            "已完成 [hell quest] 时会话放行深渊派对入场，收据带深渊房间");
        // ===== 实验性（#256）深渊房间本体 =====
        // 深渊房间 = [seal door pos] 处的普通房间改用 [seal door map index] 深渊图（柱子 + 固定小怪），不是新增房间。
        var swapped = plan!.Maze.MapSpecification!.Single(row => row.Position == new DungeonPoint(2, 2));
        var hellMapPath = definition.Mazes.Single(maze => maze.HellParty is not null).HellPartyPath;
        That(hellMapPath is not null && hellMapPath.EndsWith("313163_a_hell.map", StringComparison.Ordinal) &&
            swapped is { Id: 313163, Kind: "map" } && swapped.Path == hellMapPath &&
            ordinary!.Maze.MapSpecification!.Single(row => row.Position == new DungeonPoint(2, 2)).Id != 313163 &&
            plan!.Maze.MapSpecification!.Count == ordinary!.Maze.MapSpecification!.Count,
            "深渊入场计划把 (2,2) 的普通房间换成 313163 深渊图，普通入场不换，房间数不变");
        var hellRoom = rooms.Get(swapped, requirePassiveDefinitions: false);
        That(hellRoom.Monsters.Count == 10 && hellRoom.Monsters.All(row => row.MonsterId == 107002740) &&
            hellRoom.SpecialObjects.Single() is { ObjectId: 30528 } pillar && pillar.Children.Single(child => child.Kind == "[hellparty]").RawValues.Count == 33,
            "313163 深渊图：10 只固定小怪 107002740 + 柱子 30528（[hellparty] 11 组三元组）");
        var waves = rooms.ResolveHellWaves(hellRoom, 1, plan!.BasisLevel, _ => 0);
        That(waves.Count == 4 && waves.Where(row => row.Order == 1).Select(row => (row.GroupId, row.MonsterId)).SequenceEqual([(125, 1050)]) &&
            waves.Where(row => row.Order == 2).Select(row => (row.GroupId, row.MonsterId)).SequenceEqual([(216, 107010514), (216, 107010515), (216, 107010516)]) &&
            waves.All(row => row.Type == 0 && row.Level == plan!.BasisLevel),
            "313163 波次（抽签 0）：第 1 波组 125=怪 1050，第 2 波组 216=三只，普通怪等级=副本基准等级");
        var lastWaves = rooms.ResolveHellWaves(hellRoom, 1, plan!.BasisLevel, total => total - 1);
        That(lastWaves.Where(row => row.Order == 1).All(row => row.GroupId == 128) && lastWaves.Where(row => row.Order == 2).All(row => row.GroupId == 218),
            "波次权重抽签：最大抽签落在每波最后一个候选（组 128 / 218）");
        That(rooms.ResolveHellWaves(rooms.Get(plan.Maze.MapSpecification![0], requirePassiveDefinitions: false), 1, plan!.BasisLevel, _ => 0).Count == 0 &&
            rooms.ResolveHellWaves(hellRoom, 1, null, _ => 0).Count == 0,
            "没有柱子的房间或没有基准等级时不产生波次");
        var hellPlan = DungeonMonsterPlan.FromRoom(hellRoom, character.Id, 1, plan!.BasisLevel).WithHellWaves(waves, character.Id);
        That(hellPlan.HellWaves.Count == 4 && hellPlan.HellWaves.Select(row => (int)row.ObjectKey).Distinct().Count() == 4 &&
            hellPlan.HellWaves.All(row => row.ObjectKey > hellPlan.Monsters.Max(monster => monster.ObjectKey) && row.ObjectKey != character.Id) &&
            hellPlan.HellWaves.Select(row => row.PacketIndex).SequenceEqual([10, 11, 12, 13]) && hellPlan.NextObjectKey == hellPlan.HellWaves.Max(row => row.ObjectKey) + 1,
            "波次怪 object key 接在既有 actor 之后且不撞角色 key，行序号接着普通序号往后排");
        var hellStart = DungeonPackets.StartMap(plan! with { StartMap = swapped }, hellPlan, 5u);
        var noWaveStart = DungeonPackets.StartMap(plan! with { StartMap = swapped }, hellPlan with { HellWaves = [] }, 5u);
        int rowSize = 22, firstWaveRow = 19 + (hellPlan.Monsters.Count + hellPlan.SpecialObjects.Count) * rowSize;
        That(hellStart[18] == noWaveStart[18] + 4 && hellStart.Length == noWaveStart.Length + 4 * rowSize &&
            BinaryPrimitives.ReadUInt16LittleEndian(hellStart.AsSpan(firstWaveRow)) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(hellStart.AsSpan(firstWaveRow + 2)) == 10 &&
            BinaryPrimitives.ReadUInt16LittleEndian(hellStart.AsSpan(firstWaveRow + 6)) == hellPlan.HellWaves[0].ObjectKey &&
            BinaryPrimitives.ReadUInt32LittleEndian(hellStart.AsSpan(firstWaveRow + 8)) == 1050 && hellStart[firstWaveRow + 14] == 1 &&
            hellStart[firstWaveRow + 15] == 0xFF && hellStart[firstWaveRow + 16] == 0xFF && hellStart[firstWaveRow + 21] == 0,
            "深渊房间 START_MAP 追加 4 条隐藏模板行（波次号/序号/key/code，flag0=1 flag1=FF）");
        var hellInfo = DungeonPackets.HellPartyMonsterInfo(hellPlan);
        That(hellInfo is not null && hellInfo.Length == 4 + 4 * 8 && BinaryPrimitives.ReadInt32LittleEndian(hellInfo) == 4 &&
            BinaryPrimitives.ReadInt32LittleEndian(hellInfo.AsSpan(4)) == 1050 && BinaryPrimitives.ReadInt32LittleEndian(hellInfo.AsSpan(8)) == plan!.BasisLevel &&
            DungeonPackets.HellPartyMonsterInfo(hellPlan with { HellWaves = [] }) is null,
            "NOTI 679：count + (code, level)，无波次不发");
        var catalogText = DungeonHellPartyCatalog.FromText("[hellparty monster group]\n[group index]\n7\n[group]\n`A` 100 0 101 1 \n[/group]\n[group]\n`B` 200 0 \n[/group]\n[/hellparty monster group]");
        That(catalogText.Entries(7, 'A').SequenceEqual([(100, 0), (101, 1)]) && catalogText.Entries(7, 'B').SequenceEqual([(200, 0)]) && catalogText.Entries(7, 'C').Count == 0 &&
            DungeonHellPartyCatalog.DifficultyKey(1) == 'A' && DungeonHellPartyCatalog.DifficultyKey(2) == 'B' &&
            catalogText.PickWaves([[7, 10, 1, 9, 10, 1, 7, 10, 2]], 1, total => total - 1).SequenceEqual([(1, 7), (2, 7)]) &&
            catalogText.PickWaves([[7, 10, 1]], 3, _ => 0).Count == 1 && catalogText.PickWaves([[7, 10, 1]], 2, _ => 0).Count == 1,
            "hellparty.etc 解析：组号/难度档/entityType；波次只保留有该难度条目的候选");

        // ===== 覆盖枚举：所有选图区域 [hell dungeon] 1 的副本，逐个检查是否有深渊房间且波次可展开 =====
        int hellAreas = 0, optionTotal = 0, covered = 0, uncovered = 0, dungeonsWithHellMaze = 0;
        var coveredMaps = new SortedSet<string>();
        foreach (var hellWorld in towns.DungeonMaps.Values.Where(row => row.HellDungeon && row.Error is null))
        {
            hellAreas++;
            foreach (var option in hellWorld.Options)
            {
                optionTotal++;
                DungeonDefinition? dungeon;
                try { dungeon = dungeons.Get(option.DungeonId); } catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException) { dungeon = null; }
                var eligible = dungeon?.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0 && maze.HellParty is not null).ToArray() ?? [];
                if (eligible.Length == 0) { uncovered++; Console.WriteLine($"HELLENUM uncovered area-map={hellWorld.Id} dungeon={option.DungeonId} reason={(dungeon is null ? "dungeon-unavailable" : "no-eligible-maze")}"); continue; }
                dungeonsWithHellMaze++;
                foreach (var maze in eligible)
                {
                    var hell = maze.HellParty!;
                    var spec = maze.MapSpecification!.Single(row => row.Position == hell.Room);
                    var swappedMap = spec with { Id = hell.MapId, Path = maze.HellPartyPath! };
                    var hellMapRoom = rooms.Get(swappedMap, requirePassiveDefinitions: false);
                    var mazeWaves = rooms.ResolveHellWaves(hellMapRoom, 1, dungeon!.BasisLevel, _ => 0);
                    bool conflict = maze.Starts[0] == hell.Room || maze.Bosses.Any(boss => boss == hell.Room);
                    if (!conflict && mazeWaves.Count > 0) { covered++; coveredMaps.Add($"{option.DungeonId}:{hell.MapId}@{hell.Room.X},{hell.Room.Y}"); }
                    else { uncovered++; Console.WriteLine($"HELLENUM uncovered dungeon={option.DungeonId} map={hell.MapId} conflict={conflict} waves={mazeWaves.Count}"); }
                }
            }
        }
        Console.WriteLine($"HELLENUM areas={hellAreas} options={optionTotal} dungeons-with-hell-maze={dungeonsWithHellMaze} covered-mazes={covered} uncovered={uncovered}");
        Console.WriteLine("HELLENUM covered: " + string.Join(" ", coveredMaps));
        That(covered > 0 && coveredMaps.Contains("291100308:313163@2,2"), "枚举：至少覆盖黑暗都市 291100308 的深渊房间");

        // 同一副本有多个深渊 maze（291100319：313166 / 313173，同在 (3,0)）：按抽签选其一，不再按“有歧义”拒绝。
        var multi = dungeons.Get(291100319).Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0 && maze.HellParty is not null).ToArray();
        That(multi.Length >= 2 && multi.Select(maze => maze.HellParty!.MapId).Distinct().Order().SequenceEqual([313166, 313173]), "291100319 有多个带深渊房间的普通 maze，深渊图为 313166 / 313173（" + multi.Length + " 个）");
        var multiMaps = new List<int>();
        foreach (int pick in new[] { 0, 1 })
        {
            bool made = DungeonEntryPlan.TryCreate(dungeons, character, 291100319, 0, 0, out var multiPlan, out var multiReason, quests: quests, draw: _ => pick, hellParty: true);
            That(made, "291100319 深渊入场计划（抽签 " + pick + "）：" + multiReason);
            multiMaps.Add(multiPlan!.HellParty!.MapId);
            That(multiPlan.Maze.MapSpecification!.Single(row => row.Position == multiPlan.HellParty.Room).Id == multiPlan.HellParty.MapId, "291100319 深渊房间已替换");
        }
        That(multiMaps.Distinct().Count() == 2 || multi.Length > 2, "291100319 抽签 0/1 选中不同的深渊 maze（" + string.Join(",", multiMaps) + "）");

        // ===== 波次怪死亡只登记：不进 Monsters，不影响清房/存活列表 =====
        var runMap = new DungeonMap("boss", new(0, 0), 99, "map/hell-wave.map");
        var runMaze = new DungeonMaze(0, 1, 1, "fixture", [runMap.Position], [runMap.Position], [runMap], []);
        var runRoom = new DungeonRoomDefinition(runMap, [new(1, [1, 0, 20, 100, 100, 0, 1, 1], "[fixed]", "[normal]", "monster/fixture.mob")], [], []);
        var wavePlan = DungeonMonsterPlan.FromRoom(runRoom, 99, 10).WithHellWaves([new(1, 125, 1050, 20, 0, 0, 0), new(2, 216, 107010514, 20, 0, 0, 0)], 99);
        var waveEntry = new DungeonEntryPlan(99, 0, 3, 3145, 0, 0, runMaze, runMap, runMap);
        That(SinglePlayerDungeonRun.TryCreate(waveEntry, 1, 100, new([wavePlan], []), new HellWaveAuthority(), out var waveRun, out var waveReason),
            "带波次的房间计划可建 Run（入场重校验只比较 Monsters）：" + waveReason);
        waveRun!.CompleteLoading(waveRun.Snapshot.Identity, Guid.NewGuid());
        ushort waveKey = wavePlan.HellWaves[0].ObjectKey;
        That(waveRun.RetireHellWave(waveKey) == DungeonRunOutcome.Accepted && waveRun.RetireHellWave(waveKey) == DungeonRunOutcome.Duplicate &&
            waveRun.RetireHellWave(5000) == DungeonRunOutcome.Rejected && waveRun.RetireHellWave(10) == DungeonRunOutcome.Rejected &&
            waveRun.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 10 }) && waveRun.Phase == SinglePlayerDungeonPhase.Dungeon,
            "波次怪死亡：首次 Accepted、重复 Duplicate、未知 key / 普通怪 key Rejected；存活列表和阶段不变");
        Console.WriteLine("HellPartyEntryTests: " + (Count - checks) + " checks");
    }

    sealed class HellWaveAuthority : DungeonCombatAuthority { }

    static Character Character(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog skills,
        CharacterGrowthCatalog growth, QuestCatalog quests, int[] completed, out QuestSceneTests.Fixture fixture)
    {
        fixture = new(towns, characters, skills, growth, quests, false, 95, completed);
        return fixture.Read();
    }
}