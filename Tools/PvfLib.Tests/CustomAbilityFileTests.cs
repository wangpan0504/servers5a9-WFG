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
    }
}
