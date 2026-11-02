using DfoServer.GameWorld.Raids;
using DfoServer.Game.Raids;
using PvfLib;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeCardSetTests
{
    internal static void RunCore()
    {
        var beforeReset = new DateTimeOffset(2026, 11, 2, 5, 59, 59, TimeSpan.FromHours(8));
        var reset = beforeReset.AddSeconds(1);
        That(LukeChannelCards.Day(beforeReset) == new DateOnly(2026, 11, 1).DayNumber &&
            LukeChannelCards.Day(reset) == new DateOnly(2026, 11, 2).DayNumber &&
            LukeChannelCards.Day(reset.AddHours(23).AddMinutes(59)) == LukeChannelCards.Day(reset),
            "塔罗以北京时间06:00切日，午夜与机器时区不改变当天牌组");
        That(LukeChannelCards.Day(reset.ToUniversalTime()) == LukeChannelCards.Day(reset) &&
            LukeChannelCards.Day(reset.ToOffset(TimeSpan.FromHours(-4))) == LukeChannelCards.Day(reset),
            "同一瞬间不因输入时区不同而改变全频道日键");
        var data = new PvfLukeCardSet([new(5054, 0, new("client.img", [0, 1]), [new(0.5, 1)], "原文"),
            new(5054, 1, new("client.img", [2]), [new(1, 2)], "另一声明")],
            [new(59171, new("tarot.img", [0]), "愚者")]);
        var ready = LukeCardSetCatalog.Validate(data, id => id == 5054, id => id == 59171);
        That(ready.Ready && ReferenceEquals(ready.Data, data), "只读卡牌目录保留原声明，不改顺序或选择路线");
        var missing = LukeCardSetCatalog.Validate(data, _ => false, _ => false);
        That(!missing.Ready && missing.Data == data && missing.Issues.SequenceEqual(new[] {
            "luke-cardset-dungeon-unregistered:5054", "luke-cardset-monster-unregistered:59171" }),
            "缺失引用独立诊断；重复DGN声明只诊断一次，声明仍可查看");
        That(!new LukeCardSetCatalog(null, ["luke-cardset-file-missing"]).Ready, "文件缺失不是空目录成功");
        var names = new[] { "[RAID MONSTER LIST]", "[DUNGEON]", "[MAZE]", "[NAMED]", "[BOSS]", "[PASSIVE]",
            "[/MAZE]", "[/DUNGEON]", "[/RAID MONSTER LIST]" };
        PvfToken Tag(int index) => new(3, index);
        PvfToken Value(int value) => new(0, value);
        PvfToken[] monitors = [Tag(0), Tag(1), Value(10), Tag(2), Value(0), Tag(3), Value(100), Tag(4), Value(101),
            Tag(6), Tag(2), Value(1), Tag(4), Value(102), Tag(6), Tag(7),
            Tag(1), Value(20), Tag(3), Value(103), Tag(5), Value(104), Tag(7), Tag(8)];
        var catalog = LukeMonsterMonitorCatalog.Parse(monitors, index => names[index]);
        That(catalog.Ready && catalog.Find(10, 0).Select(row => row.Template).SequenceEqual(new[] { 100, 101 }) &&
            catalog.Find(10, 1).Select(row => row.Template).SequenceEqual(new[] { 102 }) && catalog.Find(10, 2).IsEmpty &&
            catalog.Find(20, 99).SequenceEqual(new[] { new LukeMonsterMonitor(103, false), new LukeMonsterMonitor(104, true) }),
            "监控目录保留P1当前maze与P2直列的原顺序，物件类型独立，未知maze不挪用其它路线");
        Reject<InvalidDataException>(() => LukeMonsterMonitorCatalog.Parse(monitors[..^1], index => names[index]),
            "监控目录未闭合不能假成功");
        var invalid = monitors.ToArray(); invalid[8] = Value(0);
        Reject<InvalidDataException>(() => LukeMonsterMonitorCatalog.Parse(invalid, index => names[index]),
            "监控目标模板必须正数，不猜未知身份");
    }

    internal static void RunResources(PvfArchive pvf, RaidDefinition definition)
    {
        var catalog = LukeCardSetCatalog.Load(pvf, definition.Resources!.EntryCatalog!, SelfTestCatalogs.Rooms(pvf));
        That(catalog is { Ready: true, Data: not null }, "当前卡牌元数据可读且引用齐全: " + string.Join(";", catalog?.Issues ?? []));
        var data = catalog!.Data!;
        That(data.Patterns.Select(row => (row.Dungeon, row.Ordinal)).SequenceEqual(new[] {
            (5054,0), (5054,1), (5055,0), (5055,1), (5055,2), (5055,3), (5056,0), (5056,1) }),
            "当前八组pattern声明原顺序保留，不据此生成路线");
        That(data.Patterns.All(row => row.Positions.Length == 7) && data.Patterns.Any(row => row.Positions.Any(point => point.X == 0.5)),
            "七个声明坐标及半格不截断");
        That(data.Patterns.SelectMany(row => row.Image.Indices).SequenceEqual(Enumerable.Range(0, 12)), "一或两个图片索引原样保留");
        That(data.Cards.Length == 22 && data.Cards.SelectMany(row => row.Image.Indices).SequenceEqual(Enumerable.Range(0, 22)),
            "二十二个卡牌/怪物声明，不直接生成怪物");
        That(catalog.Monitors is { Ready: true } monitor && monitor.Find(5063, 0).SequenceEqual(new[] {
                new LukeMonsterMonitor(59278, false), new LukeMonsterMonitor(56063, false), new LukeMonsterMonitor(64062, true) }) &&
            monitor.Find(5066, 0).Length == 4 && monitor.Find(5054, 0).Single().Template == 56063,
            "真实buff目录独立于22张塔罗牌，能源5063三项/5066四项及P1当前maze原序可核验");
        DailyCards(pvf, definition, catalog);
    }

    static void DailyCards(PvfArchive pvf, RaidDefinition definition, LukeCardSetCatalog cards)
    {
        var entries = definition.Resources!.EntryCatalog!;
        // 调用方可能只装配显式Fixed单组诊断；日轮换独立读取全部五组，不改变该夹具的资源身份。
        var objects = LukeRaidObjectCatalog.Load(pvf, entries,
            SelfTestCatalogs.Rooms(pvf), LukeSanctumRoute.ResourceChoices(entries));
        var route = LukeSanctumRoute.Create(entries, objects);
        var start = new DateTimeOffset(1970, 1, 1, 6, 0, 0, TimeSpan.FromHours(8));
        var clock = new CardClock(start);
        var firstChannel = new LukeChannelCards(route, cards, clock);
        var secondChannel = new LukeChannelCards(route, cards, clock);
        var frozen = firstChannel.Current;
        var battle = new AntonRaidBattle(entries.Script, kind: RaidKind.Luke, clock: clock,
            cardRoute: route, lukeCards: cards, lukeChannelCards: frozen, lukeRouteRandom: new RejectRandom());
        var initial = battle.LukeCardsSnapshot(battle.View)!;
        That(initial is { AutoDisplay: false } && initial.Dungeons.All(row =>
            row.Pattern == frozen.Patterns[row.Dungeon] && row.Monsters.SequenceEqual(
                frozen.Display.Dungeons.Single(expected => expected.Dungeon == row.Dungeon).Monsters)),
            "真实Battle直接冻结频道日布局与三maze牌组，开团恢复不重复自动弹窗或随机抽组");
        var firstRoute = battle.CaptureLukeRoute(0, 5054, null, route)!;
        That(firstRoute.Group == frozen.Group && firstRoute.MazeOrdinal == route.Maze(frozen.Group, 5054, 0),
            "Battle真实入场路线使用同一频道日组");
        foreach (int day in Enumerable.Range(0, 6))
        {
            clock.Now = start.AddDays(day);
            var selected = firstChannel.Current;
            var other = secondChannel.Current;
            That(selected.Group == day % route.GroupCount && selected.Day == new DateOnly(1970, 1, 1).AddDays(day).DayNumber &&
                selected.Display is { Phase: 0, AutoDisplay: true } && selected.Display.Dungeons.Length == 3,
                "频道入场直接取得日牌组并自动展示，资源组按声明顺序每日轮换及回绕");
            That(other.Group == selected.Group && other.Patterns.OrderBy(row => row.Key).SequenceEqual(selected.Patterns.OrderBy(row => row.Key)) &&
                other.Display.Dungeons.Zip(selected.Display.Dungeons).All(pair => pair.First == pair.Second ||
                    pair.First.Dungeon == pair.Second.Dungeon && pair.First.Pattern == pair.Second.Pattern &&
                    pair.First.Monsters.SequenceEqual(pair.Second.Monsters)),
                "不同频道同一日取得相同路线、布局及模板，不按开团时刻随机");
            foreach (var dungeon in selected.Display.Dungeons)
            {
                var patterns = cards.Patterns(dungeon.Dungeon);
                var expected = Enumerable.Range(0, 3).Select(step => route.Maze(selected.Group, dungeon.Dungeon, step)!.Value)
                    .SelectMany(maze => cards.MazeCards[(dungeon.Dungeon, maze)]).Distinct();
                That(dungeon.Pattern == patterns[day % patterns.Length] &&
                    selected.Patterns[dungeon.Dungeon] == dungeon.Pattern && dungeon.Monsters.SequenceEqual(expected),
                    "布局沿本图声明顺序轮换，塔罗模板来自当天真实三次maze而非路线号或图片索引");
            }
        }
        That(frozen.Group == 0 && frozen.Day == new DateOnly(1970, 1, 1).DayNumber &&
            frozen.Display.Dungeons.All(row => row.Pattern == cards.Patterns(row.Dungeon)[0]),
            "日切只改变后续频道预览，不修改已取得的不可变开团日牌组");
        var afterReset = battle.LukeCardsSnapshot(battle.View)!;
        That(afterReset is { AutoDisplay: false } && afterReset.Dungeons.Zip(initial.Dungeons).All(pair =>
            pair.First.Pattern == pair.Second.Pattern && pair.First.Monsters.SequenceEqual(pair.Second.Monsters)) &&
            battle.CaptureLukeRoute(0, 5054, null, route) == firstRoute,
            "跨越多个06:00后既有Battle的展示、布局及真实入场lease保持原日冻结值");
        var previous = firstChannel.Select(start.AddSeconds(-1));
        That(previous.Group == route.GroupCount - 1 && previous.Display.Dungeons.All(row =>
            row.Pattern == cards.Patterns(row.Dungeon)[^1]), "纪元前一日仍沿资源尾项轮换，不产生负索引");
        Reject<InvalidDataException>(() => new LukeChannelCards(route, new(null, ["missing"])).Select(start),
            "未就绪卡牌资源不伪造频道日牌组");
    }

    sealed class CardClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class RejectRandom : Random
    {
        public override int Next(int maxValue) => throw new InvalidOperationException("Daily tarot cannot redraw route or layout");
    }
}
