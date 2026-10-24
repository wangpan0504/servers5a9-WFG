using System.Buffers.Binary;
using System.Text.RegularExpressions;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static PvfLib.PvfScript;

namespace DfoServer;

internal static class WorldMapHoverTests
{
    public static void Run()
    {
        int checks = Count;
        var empty = DungeonPackets.Permission([]);
        That(empty.SequenceEqual(new byte[] { 0, 0 }), "空开通表只有 u16 计数 0");
        var body = DungeonPackets.Permission([7, 8]);
        That(body.Length == 12 && BinaryPrimitives.ReadUInt16LittleEndian(body) == 2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)) == 7 && body[6] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(7)) == 8 && body[11] == 1,
            "开通包是 u16 计数加每项 u32 编号与 state 1");
        var synthetic = new TownCatalog(new(), new(38, 1, 0, 0), new(38, 0, 0, 0), new(0, [], []), [],
            new Dictionary<uint, uint[]>(), new Dictionary<int, TownDungeonMap>
            {
                [77] = new(77, "worldmap/operation_hope.wdm", [new(8523, 6170, false)]),
                [49] = new(49, "worldmap/harlem.wdm", [new(291100268, -1, false)])
            });
        That(WorldMapHoverDungeons.DungeonIds(synthetic).SequenceEqual([8523]),
            "合成目录只收集魔界三块地图的地下城编号，不含哈林无条件入口");
        var character = new Character(1, 0, "地图标记", 0, new(38, 1, 450, 234), null);
        byte[] history = new byte[30004];
        BinaryPrimitives.WriteUInt32LittleEndian(history, 30000);
        history[4 + 13700] = 1;
        Bytes(TownPackets.CompletedQuests(character), history, "空历史只增加月轮山显示标记，不解锁其他节点");
        var progressed = character with
        {
            CompletedQuests = [3616, 3622], AccountCompletedQuests = [100],
            QuestAnswers = character.QuestAnswers.Add(3616, 2),
            AccountQuestAnswers = character.AccountQuestAnswers.Add(100, 1)
        };
        history[4 + 3616] = 3;
        history[4 + 3622] = 1;
        history[4 + 100] = 2;
        Bytes(TownPackets.CompletedQuests(progressed), history, "显示标记保留真实角色/账号完成记录及回答+1");
        That(character.CompletedQuests.Length == 0 && character.AccountCompletedQuests.Length == 0 &&
            progressed.CompletedQuests.SequenceEqual([3616, 3622]) && progressed.AccountCompletedQuests.SequenceEqual([100]),
            "显示投影不修改任何角色或账号完成集合");
        Reject<InvalidDataException>(() => TownPackets.CompletedQuests(character with
        {
            QuestAnswers = character.QuestAnswers.Add(13700, 1)
        }), "显示标记不能让缺少真实完成记录的回答通过校验");
        That(TownPackets.CompletedQuests(character with
        {
            CompletedQuests = [13700], QuestAnswers = character.QuestAnswers.Add(13700, 4)
        })[4 + 13700] == 5, "兼容标记不覆盖已有真实回答");
        Console.WriteLine($"PASS {Count - checks} world-map hover permission layout assertions");
    }

    public static void RunResources(TownCatalog towns, PvfArchive pvf)
    {
        int checks = Count;
        var worlds = List(pvf.ReadText("worldmap/worldmap.lst"));
        var dungeonList = List(pvf.ReadText("dungeon/dungeon.lst"));
        var dungeonIds = dungeonList.Keys.ToHashSet();
        var questIds = List(pvf.ReadText("n_quest/quest.lst"));
        string shonan = pvf.ReadText("region/minimap/shonan.mm");
        foreach (var (mapId, questId) in new[] { (16, 3616), (17, 3622), (30, 13700) })
        {
            string node = Regex.Matches(shonan, @"\[world\](.*?)\[/world\]", RegexOptions.Singleline)
                .Select(match => match.Groups[1].Value)
                .Single(block => Number(block, "index", -1) == mapId);
            That(Number(Block(node, "grid map"), "quest", -1) == questId &&
                questIds.ContainsKey(questId) == (mapId != 30),
                "素喃节点任务绑定匹配构建，只有月轮山引用缺失定义 " + mapId);
        }
        That(towns.Areas[(18, 0)].DungeonWorldMap == 30 && !towns.AccessQuestIds.Contains(13700),
            "月轮山已注册且13700不是服务端入场前置，只补客户端显示");
        string mm = pvf.ReadText("region/minimap/ashy_graveyard.mm");
        var mmWorlds = Regex.Matches(mm, @"\[world\](.*?)\[/world\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(match => Regex.Match(match.Groups[1].Value, @"\[index\]\s*(\d+)"))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToHashSet();
        That(mmWorlds.IsSupersetOf(WorldMapHoverDungeons.MapIds) && mmWorlds.Contains(49),
            "魔界羊皮纸包含哈林和三块缺失悬浮节点的世界地图编号");
        That(WorldMapHoverDungeons.MapIds.SequenceEqual([77, 148, 153]),
            "悬浮开通只覆盖封锁区、夜之摩天楼、东哈林三块");

        That(worlds[77].Contains("operation_hope", StringComparison.OrdinalIgnoreCase) &&
            WdmName(pvf, worlds[77]) == "代号 : 希望",
            "世界地图 77 是 Operation_Hope，名称来自 WDM [name]");
        That(worlds[148].Contains("night_skyscraper", StringComparison.OrdinalIgnoreCase) &&
            WdmName(pvf, worlds[148]) == "夜之摩天楼",
            "世界地图 148 是 Night_Skyscraper，名称来自 WDM [name]");
        That(worlds[153].Contains("theevildomwar", StringComparison.OrdinalIgnoreCase) &&
            WdmName(pvf, worlds[153]) == "东哈林",
            "世界地图 153 是 TheEvildomWar，名称来自 WDM [name]");

        string matching = pvf.ReadText("etc/partymatchinginfo.etc");
        That(matching.Contains("`封锁区`", StringComparison.Ordinal) && matching.Contains("8523", StringComparison.Ordinal),
            "封锁区节点名来自 partymatchinginfo.etc，对应地下城 8523");

        That(towns.Areas.Values.Any(area => area.DungeonWorldMap == 77) &&
            towns.Areas.Values.Any(area => area.DungeonWorldMap == 148) &&
            towns.Areas.Values.Any(area => area.DungeonWorldMap == 153),
            "三块世界地图由城镇 dungeon gate 装入 DungeonMaps");

        foreach (int mapId in WorldMapHoverDungeons.MapIds)
        {
            var map = towns.DungeonMaps[mapId];
            That(map.Error is null && map.Options.Length > 0 && map.Options.All(option => option.QuestId != -1),
                "缺失悬浮的三块世界地图没有 -1 无条件入口 " + mapId);
            int[] fromWdm = UniqueDungeonIds(pvf, worlds[mapId]!, dungeonIds);
            int[] fromOptions = map.Options.Select(option => option.DungeonId).Where(id => id > 0).Distinct().Order().ToArray();
            That(fromOptions.SequenceEqual(fromWdm) && fromWdm.Length > 0,
                "城镇解析的地下城编号等于 WDM [dungeon] 中出现在 dungeon.lst 的编号 " + mapId);
        }

        That(towns.DungeonMaps[49].Options.Any(option => option.QuestId == -1),
            "哈林仍保留 -1 无条件入口，不靠本包显示");

        int[] ids = WorldMapHoverDungeons.DungeonIds(towns);
        That(ids.SequenceEqual(WorldMapHoverDungeons.MapIds.SelectMany(mapId =>
            UniqueDungeonIds(pvf, worlds[mapId]!, dungeonIds)).Distinct().Order()),
            "开通表按三块地图的资源编号去重排序");

        var names = new Dictionary<int, string>();
        foreach (int id in ids)
        {
            string name = DungeonName(pvf, dungeonList, id);
            That(name.Length > 0, "地下城名称来自 dungeon.lst 指向脚本的 [name] " + id);
            names[id] = name;
        }

        byte[] body = DungeonPackets.Permission(ids);
        That(BinaryPrimitives.ReadUInt16LittleEndian(body) == ids.Length &&
            body.Length == 2 + ids.Length * 5,
            "悬浮开通包长度等于资源编号数量");
        var packetIds = new int[ids.Length];
        for (int index = 0; index < ids.Length; index++)
        {
            packetIds[index] = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2 + index * 5));
            That(body[2 + index * 5 + 4] == 1 && packetIds[index] == ids[index],
                "包内地下城编号与资源顺序一致且 state 为 1");
        }

        That(packetIds.Contains(8523) && names[8523] == "代号 : 希望",
            "封锁区悬浮写入 8523 代号 : 希望");
        That(packetIds.Contains(291100373) && names[291100373] == "夜之摩天楼" &&
            packetIds.Contains(291100374) && names[291100374] == "夜之摩天楼中心",
            "夜之摩天楼悬浮写入 291100373/291100374");
        foreach (int id in new[] { 5408, 5409, 5410, 5430, 5431, 5432, 5433, 5434, 5435 })
            That(packetIds.Contains(id) && names[id].Length > 0, "东哈林悬浮写入剧情地下城 " + id + " " + names[id]);

        var harlemAlways = towns.DungeonMaps[49].Options.Where(option => option.QuestId == -1)
            .Select(option => option.DungeonId).ToHashSet();
        That(!harlemAlways.Overlaps(packetIds), "哈林无条件副本不进入魔界三块悬浮包");
        if (towns.DungeonMaps.TryGetValue(154, out var party) && party.Error is null)
            That(!party.Options.Select(option => option.DungeonId).ToHashSet().Overlaps(packetIds),
                "东哈林组队表不进入剧情节点悬浮包");
        if (towns.DungeonMaps.TryGetValue(149, out var stadium) && stadium.Error is null)
            That(!stadium.Options.Select(option => option.DungeonId).ToHashSet().Overlaps(packetIds),
                "隐藏的中央竞技场世界地图不进入悬浮包");
        That(!packetIds.Contains(7143), "格兰之森转职副本不进入魔界悬浮包");

        string projection = File.ReadAllText(Path.Combine(ServerRoot(), "Network", "Projection", "TownProjection.cs"));
        That(projection.Contains("DUNGEON_PERMISSION", StringComparison.Ordinal) &&
            projection.Contains("worldmap_hover_dungeon_permission", StringComparison.Ordinal) &&
            !projection.Contains("PARTICULAR_HELL", StringComparison.Ordinal) &&
            !projection.Contains("CRACK_OF_DAYBREAK", StringComparison.Ordinal),
            "选角入城发送世界地图悬浮开通包，不发送裂缝次数包");

        Console.WriteLine("world-map hover 77 " + names[8523] + " id 8523");
        Console.WriteLine("world-map hover 148 " + names[291100373] + " / " + names[291100374]);
        Console.WriteLine("world-map hover 153 " + string.Join(" / ",
            new[] { 5408, 5409, 5410, 5430, 5431, 5432, 5433, 5434, 5435 }.Select(id => names[id])));
        Console.WriteLine($"PASS {Count - checks} world-map hover resource packet assertions");
    }

    public static void AssertPermissionBody(byte[] body, TownCatalog towns)
    {
        ArgumentNullException.ThrowIfNull(body);
        int[] expected = WorldMapHoverDungeons.DungeonIds(towns);
        That(body.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(body) == expected.Length &&
            body.Length == 2 + expected.Length * 5, "线上 NOTI5 计数等于三块地图资源编号");
        var actual = new int[expected.Length];
        for (int index = 0; index < expected.Length; index++)
        {
            actual[index] = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2 + index * 5));
            That(body[2 + index * 5 + 4] == 1, "线上 NOTI5 每项 state 为 1");
        }
        That(actual.SequenceEqual(expected) && actual.Contains(8523) &&
            actual.Contains(291100373) && actual.Contains(5408),
            "线上 NOTI5 含封锁区、夜之摩天楼、东哈林资源地下城");
    }

    static string WdmName(PvfArchive pvf, string relative) =>
        new PvfDocument(pvf.ReadText(PvfArchive.NormalizePath("worldmap/" + relative))).Section("name").Trim().Trim('`');

    static string DungeonName(PvfArchive pvf, IReadOnlyDictionary<int, string> list, int id)
    {
        if (!list.TryGetValue(id, out var relative))
            return "";
        return new PvfDocument(pvf.ReadText(PvfArchive.NormalizePath("dungeon/" + relative))).Section("name").Trim().Trim('`');
    }

    static int[] UniqueDungeonIds(PvfArchive pvf, string relative, IReadOnlySet<int> dungeonIds)
    {
        var tokens = pvf.ReadTokens(PvfArchive.NormalizePath("worldmap/" + relative));
        int at = 0;
        while (at < tokens.Length && (tokens[at].Type != 3 || pvf.ResolveString(tokens[at].Value) != "[dungeon]"))
            at++;
        var ids = new SortedSet<int>();
        for (at++; at < tokens.Length; at++)
        {
            if (tokens[at].Type == 3 && pvf.ResolveString(tokens[at].Value) == "[/dungeon]")
                break;
            if (tokens[at].Type == 0 && dungeonIds.Contains(tokens[at].Value))
                ids.Add(tokens[at].Value);
        }
        return [.. ids];
    }

    static string ServerRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DfoServer.csproj")))
            root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("找不到 DfoServer 源码目录");
    }
}
