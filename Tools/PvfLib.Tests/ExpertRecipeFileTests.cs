using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ExpertRecipeFileTests
{
    public static void Run()
    {
        string[] names = ["[stackable type]", "[recipe]", "[string data]", "[/string data]", "[enchant]",
            "[sub type]", "[int data]", "[/int data]", "[need skill]", "[/need skill]", "[craftmanship]"];
        PvfToken[] valid = [new(3, 0), new(6, 1), new(0, 0), new(3, 2), new(6, 4), new(3, 3),
            new(3, 5), new(0, 1), new(3, 6), new(0, 2), new(3, 7), new(3, 8), new(0, 191), new(0, 4), new(3, 9)];
        ExpertRecipeFile Parse(PvfToken[] tokens) => ExpertRecipeFile.Parse(1, tokens, i => names[i]);
        That(Parse(valid) is { Supported: true, RecipeKind: "[enchant]", Skills: [{ ItemId: 191, Count: 4 }] },
            "附魔卷轴完整声明保留技能与等级");
        foreach (var invalid in new[]
        {
            valid[..^1], valid[..^2], [..valid, new PvfToken(3, 5), new PvfToken(0, 1)],
            [..valid[..7], new PvfToken(0, 0), ..valid[8..]],
            [..valid[..9], new PvfToken(0, 3), ..valid[10..]]
        })
            That(!Parse(invalid).Supported, "残缺/重复/未知学习模式不推断成功");
        var craft = valid.ToArray();
        craft[4] = new(6, 10); craft[7] = new(0, 0);
        That(Parse(craft).Supported, "制作图与附魔卷轴使用明确的各自子类型");
        craft[7] = new(0, 2);
        craft[12] = new(0, 193);
        That(Parse(craft).Supported, "炼金晶体转换卷轴保留真实sub type2学习声明");
        craft[7] = new(0, 3);
        That(!Parse(craft).Supported, "未知制作图子类型不能泛化放行");
    }
}
