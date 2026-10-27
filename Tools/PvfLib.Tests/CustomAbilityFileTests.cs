using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class CustomAbilityFileTests
{
    public static void Run()
    {
        const string text="""
            [custom ability type]
            `other type`
            [group]
            [key]
            `7`
            [indexes]
            1 300
            [/indexes]
            [/group]
            [info]
            [index]
            300
            [material]
            [grade]
            `S`
            [list]
            3168 50 999 -1
            [/list]
            [/material]
            [/info]
            """;
        var file=CustomAbilityFile.Parse(text);
        That(file.Type=="other type" && file.Groups[0].Key=="7" && file.Groups[0].Indexes.SequenceEqual(new[]{1,300}),
            "原始属性类型及组编号不擅自映射玩法枚举");
        That(file.Options[0].Index==300 && file.Options[0].Materials[0].Grade=="S" &&
            file.Options[0].Materials[0].Items.SequenceEqual(new[]{(3168,50),(999,-1)}),"原始档位、材料顺序及数值留给玩法校验");
        foreach(string bad in new[]{text.Replace("[/info]",""),text.Replace("[/material]",""),
            text.Replace("3168 50 999 -1","3168 50 999"),text.Replace("3168 50","3168 bad50"),
            text.Replace("[/indexes]",""),text.Replace("[/list]",""),text.Replace("`S`","S"),text.Replace("[index]\n300","[index]\n300 301")})
            Reject<InvalidDataException>(()=>CustomAbilityFile.Parse(bad));
        var extended = CustomAbilityFile.Parse(text + "\n[upgrade info]\n[grade]\n`D`\n[assign material]\n100 11\n[/assign material]\n[/upgrade info]\n" +
            "[grade probability]\n`A` 200 `D` 9800\n[/grade probability]\n");
        That(extended.Upgrades[0].Items[0] == (100, 11) && extended.GradeWeights.SequenceEqual(new[] { ("A", 200), ("D", 9800) }),
            "原始升级档和随机权重保留声明顺序，不在PvfLib补造四档");
        var ui = CustomAbilityFile.Parse(text + "\n[ui info]\n[ability info]\n21 1 `Elemental`\n[ability info]\n1 2 ``\n[/ui info]\n");
        That(ui.OptionCategories.SequenceEqual(new[] { (21, 1), (1, 2) }), "原始UI类别与空描述保持，不解释为升级或锁定语义");
        const string setText = """
            [point info]
            [part set index]
            1
            [min part count]
            3
            [max part count]
            6
            [max point table]
            `D` 3 105 `A` 6 600
            [/max point table]
            [material rate by point]
            100 2.2
            [/material rate by point]
            [discount enchant rate]
            0.9
            [enchant max count]
            4
            [/point info]
            [spend grade point]
            1 50 252 150
            [/spend grade point]
            [group]
            [key]
            `7`
            [indexes]
            252
            [/indexes]
            [condition]
            `part count` 6
            [/condition]
            [/group]
            """;
        var set = CustomAbilitySetFile.Parse(setText);
        That(set.Points[0].MaximumPoints[1] == ("A", 6, 600) && set.Points[0].Materials[0] == (100, 2.2f) &&
            set.Groups[0].MinimumParts == 6 && set.Spend[1] == (252, 150), "套装资源保留品级点数、浮点费率及件数条件");
        foreach (string bad in new[] { setText.Replace("[/point info]", ""), setText.Replace("`D` 3 105", "`D` 3"),
            setText.Replace("100 2.2", "100"), setText.Replace("1 50 252 150", "1 50 252") })
            Reject<InvalidDataException>(() => CustomAbilitySetFile.Parse(bad));
    }
}
