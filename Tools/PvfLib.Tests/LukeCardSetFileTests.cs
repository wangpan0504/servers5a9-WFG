using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class LukeCardSetFileTests
{
    public static void Run()
    {
        var strings = new List<string>();
        PvfToken Text(byte type, string value) { strings.Add(value); return new(type, strings.Count - 1); }
        PvfToken Tag(string value) => Text(3, "[" + value + "]");
        PvfToken Str(string value) => Text(6, value);
        string Resolve(int index) => strings[index];
        PvfToken[] Pattern(int id = 5054, int ordinal = 0, PvfToken[]? points = null) =>
        [Tag("PATTERN"), new(0,id), new(0,ordinal), Tag("IMAGE"), Str("Interface2\\raid\\icon.img"), new(0,2), new(0,3),
            Tag("CARD POS"), .. (points ?? [new(0,0),new(2,BitConverter.SingleToInt32Bits(0.5f)),new(0,int.MaxValue),new(0,2)]),
            Tag("/CARD POS"), Tag("TOOLTIP TEXT"), Str(" 原始提示 "), Tag("/PATTERN")];
        PvfToken[] Card(int id = 59171) => [Tag("CARD"), new(0,id), Tag("IMAGE"), Str("Interface2/raid/card.img"), new(0,0),
            Tag("TOOLTIP TEXT"), Str("愚者"), Tag("/CARD")];
        PvfToken[] pattern = Pattern(), card = Card();
        var parsed = LukeCardSetFile.Read([..pattern, ..card], Resolve);
        That(parsed.Patterns is [{ Dungeon:5054, Ordinal:0 }] && parsed.Cards is [{ Template:59171 }], "PATTERN和CARD身份与顺序独立");
        That(parsed.Patterns[0].Positions[0] == new PvfCardPoint(0,0.5) && parsed.Patterns[0].Positions[1].X == int.MaxValue,
            "整型与小数坐标无截断，不当作迷宫格");
        That(parsed.Patterns[0].Image.Indices.SequenceEqual(new[]{2,3}) && parsed.Patterns[0].Tooltip == " 原始提示 ",
            "多图片索引和提示原文保留，不把图片索引当权重");
        foreach (var invalid in new PvfToken[][]
        {
            [..pattern], [..card], [..pattern[..^1], ..card], [..pattern, ..pattern, ..card], [..pattern, ..card, ..card],
            [..Pattern(0), ..card], [..Pattern(5054,-1), ..card], [..pattern, ..Card(-1)],
            [..Pattern(points:[new(0,1)]), ..card], [..Pattern(points:[new(2,BitConverter.SingleToInt32Bits(float.NaN)),new(0,0)]), ..card],
            [..Pattern(points:[Str("0"),new(0,0)]), ..card],
            [..Pattern(points:[new(2,BitConverter.SingleToInt32Bits(float.PositiveInfinity)),new(0,0)]), ..card],
            [..Pattern(points:Enumerable.Repeat(new PvfToken(0,0),258).ToArray()), ..card],
            [Tag("unknown"), ..pattern, ..card]
        }) Reject<InvalidDataException>(() => LukeCardSetFile.Read(invalid, Resolve));
        Reject<InvalidDataException>(() => LukeCardSetFile.Read(new PvfToken[262145], Resolve));
        var badImage = pattern.ToArray(); badImage[5] = new(0,-1);
        Reject<InvalidDataException>(() => LukeCardSetFile.Read([..badImage, ..card], Resolve));
        var extra = pattern[..^1].Concat(new[]{Tag("IMAGE"),Str("second.img"),new PvfToken(0,1),Tag("/PATTERN")}).ToArray();
        Reject<InvalidDataException>(() => LukeCardSetFile.Read([..extra, ..card], Resolve));
        var two = LukeCardSetFile.Read([..pattern, ..Pattern(5054,1), ..card], Resolve);
        That(two.Patterns.Length == 2 && two.Patterns[1].Ordinal == 1, "不同pattern序号不被覆盖或自行随机");
    }
}
