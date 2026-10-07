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
    }
}
