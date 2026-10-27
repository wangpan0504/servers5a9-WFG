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
    }
}
