using DfoServer.GameWorld.Raids;
using PvfLib;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeCardSetTests
{
    internal static void RunCore()
    {
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
    }
}
