using System.Globalization;
using System.Text.RegularExpressions;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ScriptTests
{
    public static void Run()
    {
        foreach (string input in new[] { "", "no numbers", "-1 +2 30.5 -0 01", "a12x-35x99", "1\r\n-2\t3", "-2147483648 2147483647" })
        {
            int[] expected = Regex.Matches(input, @"-?\d+").Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
            That(PvfScript.Integers(input).SequenceEqual(expected), "numeric token search preserves semantics");
        }
        Reject<OverflowException>(() => PvfScript.Integers("2147483648"));
        string text = "[NaMe]\n`甲A` \n[value]\n-1.50 \n[items]\n1 `Dir\\A.stk` 2 `./b.stk`\n[/items]\n";
        That(PvfScript.Section(text, "name") == "`甲A` ", "section matching and whitespace");
        That(PvfScript.Number(text, "value") == -1.5f, "number parsing");
        That(PvfScript.Number(text, "absent", 42) == 42, "number fallback");
        var list = PvfScript.List(PvfScript.Block(text, "items"));
        That(list.Count == 2 && list[1] == "dir/a.stk" && list[2] == "b.stk", "list normalization");
        Reject<InvalidDataException>(() => PvfScript.List("1 `a` 1 `b`"));
        Reject<InvalidDataException>(() => PvfScript.Block("[x]a[/x][x]b[/x]", "x"));
        That(PvfScript.Block("[x]a", "x") == "", "incomplete block is absent");
        TokenDocument();
        AiCharacter();
        MapNpcRows();
    }

    private static void MapNpcRows()
    {
        string[] strings = ["[NPC]", "[/npc]", "[left]", "[right]", "[other]", "[/other]"];
        string Resolve(int id) => strings[id];
        PvfToken[] tokens = [new(3,0),new(0,1000),new(6,2),new(0,0),new(0,0),new(0,0),
            new(0,1298),new(6,3),new(0,-485),new(0,284),new(0,1),new(3,1)];
        var rows = MapNpcs.Read(tokens,Resolve);
        That(rows is [{ Id:1000, Direction:"[left]" }, { Id:1298, Direction:"[right]", X:-485, Y:284, Z:1 }],
            "地图NPC保留完整五字段、大小写闭合、原序及负坐标");
        tokens[6] = new(0,7);
        That(rows[1].Id == 1298 && MapNpcs.Read([],Resolve).IsEmpty && MapNpcs.Read([new(3,0),new(3,1)],Resolve).IsEmpty,
            "NPC集合冻结原输入，缺失与空块不生成演员");
        Reject<InvalidDataException>(() => MapNpcs.Read(tokens[..^1],Resolve));
        Reject<InvalidDataException>(() => MapNpcs.Read([.. tokens,.. tokens],Resolve));
        Reject<InvalidDataException>(() => MapNpcs.Read([new(3,0),new(0,1),new(0,2),new(0,3),new(0,4),new(0,5),new(3,1)],Resolve));
        Reject<InvalidDataException>(() => MapNpcs.Read([new(3,0),new(0,1),new(6,2),new(0,3),new(3,4),new(3,1)],Resolve));
        Reject<InvalidDataException>(() => MapNpcs.Read([new(3,4),.. tokens,new(3,5)],Resolve));
    }

    private static void AiCharacter()
    {
        string[] strings = ["[monster]","[boss]","[minimum info]","名字","[other]"];
        string Resolve(int at) => strings[at];
        PvfToken[] row = [new(0,2004),new(0,810),new(0,264),new(0,0),new(6,0),new(6,1),new(0,0),new(0,0)];
        var parsed = PvfAiCharacterScript.MapRows([.. row,.. row],Resolve);
        That(parsed is [{ Faction: "[monster]", Rank: "[boss]" },{ Faction: "[monster]" }] &&
            parsed[0].Values.SequenceEqual([2004,810,264,0,0,0]),"APC行保留独立整数和字符串顺序");
        Reject<InvalidDataException>(() => PvfAiCharacterScript.MapRows(row[..^1],Resolve));
        row[4] = new(0,0);
        Reject<InvalidDataException>(() => PvfAiCharacterScript.MapRows(row,Resolve));
        PvfToken[] info = [new(3,2),new(6,3),new(0,0),new(0,2),new(0,1),new(0,1),new(0,32),new(3,4),new(0,999)];
        That(PvfAiCharacterScript.MinimumInfo(info,Resolve)[5] == new PvfToken(0,32),"AIC最低信息独立保留，不吞后续字段");
        Reject<InvalidDataException>(() => PvfAiCharacterScript.MinimumInfo([.. info,.. info],Resolve));
    }

    private static void TokenDocument()
    {
        string[] names = ["[level]", "[pre]", "[/pre]", "[dialog depend on job]", "[/dialog]", "[/dialog depend on job]", "[text]", "[/bad]"];
        PvfToken Tag(int at) => new(3, at);
        PvfToken Int(int value) => new(0, value);
        string Resolve(int at) => names[at];
        var document = new PvfTokenDocument([Tag(0), Int(1), Int(99), Tag(1), Int(10), Tag(2), Tag(1), Int(20), Tag(2)], Resolve);
        That(document.Sections.Length == 3 && document.Sections[1].Values.Single().Value == 10 &&
            document.Sections[2].Values.Single().Value == 20, "token document preserves repeated prerequisite alternatives");
        var aliases = new Dictionary<string, string> { ["dialog depend on job"] = "dialog" };
        document = new PvfTokenDocument([Tag(3), Tag(6), Int(1), Tag(4), Tag(3), Tag(6), Int(2), Tag(5)], Resolve, aliases);
        That(document.Sections.Length == 2 && document.Sections[1].Children.Single().Values.Single().Value == 2,
            "qualified block supports alias and qualified closing tags in the same script");
        Reject<InvalidDataException>(() => new PvfTokenDocument([Tag(7)], Resolve));
        Reject<InvalidDataException>(() => new PvfTokenDocument([Int(1)], Resolve));
        Reject<InvalidDataException>(() => new PvfTokenDocument([Tag(3), Tag(7)], Resolve, aliases));
        document = new PvfTokenDocument([Tag(7)], Resolve, preserveExtraEndTags: true);
        That(document.Diagnostics.Length == 1 && document.Sections.Single().Name == "/bad", "extra closing tag is retained as a diagnostic");
        var deep = Enumerable.Repeat(Tag(1), 34).Concat(Enumerable.Repeat(Tag(2), 34)).ToArray();
        Reject<InvalidDataException>(() => new PvfTokenDocument(deep, Resolve));
    }
}
