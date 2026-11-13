using PvfLib;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class RaidObjectGeometryTests
{
    internal static void Run()
    {
        var strings = new List<string>();
        string Resolve(int index) => strings[index];
        PvfToken Text(string text) { strings.Add(text); return new(3, strings.Count - 1); }
        PvfToken[] Tokens(params object[] values) => values.Select(value => value is int number ? new PvfToken(0, number) : Text((string)value)).ToArray();
        var objects = Tokens("[passive object]", 64249, 1, 2, 0, "[/passive object]");
        var width = Tokens("[force map width]", 1120);
        var area = Tokens("[virtual movable area]", 0, 225, 1120, 300, "[/virtual movable area]");
        bool Accept(PvfToken[] prefix)
        {
            try { return RaidObjectEvidence.UnconditionalPassiveObjects(prefix.Concat(objects).ToArray(), Resolve); }
            catch (InvalidDataException) { return false; }
        }
        That(Accept([]), "原无几何标签的根普通对象继续有效");
        That(Accept(width) && Accept(area) && Accept(width.Concat(area).ToArray()), "单值宽度及完整根矩形不改变对象无条件性");
        That(RaidObjectEvidence.UnconditionalPassiveObjects(objects.Concat(width).Concat(area).ToArray(), Resolve),
            "最终候选在原对象块后追加几何声明仍有效");
        That(Accept(Tokens("[virtual movable area]", -10, -20, 1120, 300, 0, 300, 800, 100, "[/virtual movable area]")),
            "矩形按x/y/宽/高读取，保留负原点及完整多行");
        foreach (var invalid in new[]
        {
            Tokens("[force map width]"), Tokens("[force map width]", 0), Tokens("[force map width]", -1),
            Tokens("[force map width]", 1048577), Tokens("[force map width]", 1120, 600),
            Tokens("[force map width]", 1120, "[/force map width]"), width.Concat(width).ToArray(),
            Tokens("[virtual movable area]", "[/virtual movable area]"),
            Tokens("[virtual movable area]", 0, 200, 1120, "[/virtual movable area]"),
            Tokens("[virtual movable area]", 0, 200, 0, 300, "[/virtual movable area]"),
            Tokens("[virtual movable area]", 0, 200, 1120, -1, "[/virtual movable area]"),
            Tokens("[virtual movable area]", -1048577, 200, 1120, 300, "[/virtual movable area]"),
            Tokens("[virtual movable area]", 0, 200, 1120, 300), area.Concat(area).ToArray(),
            Tokens("[unknown geometry]", 1120),
            Tokens("[condition]").Concat(width).Concat(area).Concat(Tokens("[/condition]")).ToArray(),
            Tokens("[animation]").Concat(width).Concat(area).Concat(Tokens("[/animation]")).ToArray(),
            Tokens("[virtual movable area]", 0, 200, 1120, 300, "[condition]", "[/condition]", "[/virtual movable area]"),
            Tokens("[force map width]").Concat(new[] { new PvfToken(6, 0) }).ToArray()
        })
            That(!Accept(invalid), "几何声明缺失/多值/重复/越界/非整数/嵌套/未知仍拒绝");
        var conditionalObjects = Tokens("[condition]").Concat(objects).Concat(Tokens("[/condition]")).Concat(width).Concat(area).ToArray();
        That(!RaidObjectEvidence.UnconditionalPassiveObjects(conditionalObjects, Resolve), "合法几何不能将条件对象提升为根出生");
    }
}
