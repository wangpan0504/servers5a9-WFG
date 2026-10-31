using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class RecipeFileTests
{
    public static void Run()
    {
        string[] strings = ["[stackable type]", "[recipe]", "[input item]", "[/input item]", "[output item]", "[/output item]",
            "[minimum itemmaking level]", "leather", "[success rate]", "[maintain ability]", "normal", "[need material]",
            "[need skill]", "[/need skill]", "[itemmaking exp limit]", "[/itemmaking exp limit]", "[itemmaking exp]",
            "[itemmaking level limit]", "[upgrade with compound]", "[/upgrade with compound]", "[bead item]", "[/bead item]",
            "[fail output]", "[/fail output]", "[int data]", "[/int data]",
            "[set disjoint instance type]", "[/set disjoint instance type]", "[test wrapper]", "[/test wrapper]"];
        string Resolve(int index) => strings[index];
        PvfToken[] recipe = [new(3, 0), new(6, 1), new(0, 1), new(3, 2), new(0, 3037), new(0, 36), new(0, 0), new(0, 200), new(3, 3),
            new(3, 4), new(0, 100060157), new(0, 1), new(3, 5), new(3, 6), new(6, 7), new(0, 1),
            new(3, 8), new(0, 7000), new(3, 12), new(0, 12), new(0, 2), new(3, 13), new(3, 11), new(0, 500), new(0, 8)];
        ItemRecipeFile Parse(PvfToken[] tokens) => ItemRecipeFile.Parse(10087066, "stackable/test.stk", tokens, Resolve);
        var parsed = Parse(recipe);
        That(parsed.Diagnostics.IsEmpty && parsed.SourceType == 1 && parsed.Inputs is [{ ItemId: 3037, Count: 36 }, { ItemId: 0, Count: 200 }] &&
            parsed.Outputs is [{ ItemId: 100060157, Count: 1 }] && parsed.MakingType == "leather" && parsed.MakingLevel == 1 &&
            parsed.Skills is [{ ItemId: 12, Count: 2 }] && parsed.SuccessRate == 7000,
            "设计图完整保留首项材料、金币0、输出、熟练度、万分制概率与技能；商店兑换价不作输入");
        var restrictedRecipe = Parse([..recipe, new(3, 26)]);
        That(restrictedRecipe.Diagnostics.IsEmpty && restrictedRecipe.SetsDisjointInstanceType && !parsed.SetsDisjointInstanceType,
            "根级空分解限制声明独立保存，不从普通图推断");
        foreach (PvfToken[] invalid in new PvfToken[][]
        {
            [..recipe, new(3, 26), new(0, 1)],
            [..recipe, new(3, 26), new(3, 26)],
            [..recipe, new(3, 26), new(3, 27)],
            [..recipe, new(3, 28), new(3, 26), new(3, 29)]
        }) That(!Parse(invalid).Diagnostics.IsEmpty, "分解限制的非空、重复、闭合块或嵌套声明拒绝，不静默丢弃限制");
        var bead = Parse([..recipe, new(3, 20), new(0, 2610039), new(0, 3), new(3, 21)]);
        That(bead.Diagnostics.IsEmpty && bead.BeadInputs is [{ ItemId: 2610039, Count: 3 }] && bead.Inputs.SequenceEqual(parsed.Inputs),
            "宝珠额外成本单独解析，不混入普通卡片附魔input item");
        That(!Parse([..recipe, new(3, 20), new(0, 2610039), new(3, 21)]).Diagnostics.IsEmpty,
            "宝珠额外材料缺数量不得静默放行");
        That(Parse([..recipe, new(3, 9), new(6, 10)]).MaintainAbility == "normal", "属性保留声明独立于普通材料配方");
        That(Parse([..recipe, new(3, 22), new(0, 100060157), new(0, 1), new(0, 3037), new(0, 1), new(3, 23)])
            .FailureOutputs is [{ ItemId: 100060157, Count: 1 }, { ItemId: 3037, Count: 1 }], "失败产物独立完整解析，不能静默丢弃返还装备");
        PvfToken[] packed = [new(3, 0), new(6, 1), new(0, 1), new(3, 24), new(0, 1), new(0, 3037), new(0, 2),
            new(0, 1), new(0, 40000), new(0, 1), new(0, 0), new(3, 25)];
        That(Parse(packed) is { Diagnostics.IsEmpty: true, Inputs: [{ ItemId: 3037, Count: 2 }], Outputs: [{ ItemId: 40000, Count: 1 }] },
            "旧打包设计图按输入与输出计数完整还原");
        var restricted = packed.ToArray(); restricted[^2] = new(0, 1);
        That(!Parse(restricted).Diagnostics.IsEmpty, "未知旧配方后缀不以无条件制造兜底");
        That(!Parse(packed[..^3]).Diagnostics.IsEmpty, "截断旧配方保留诊断，不能获得制作资格");
        foreach (PvfToken[] invalid in new PvfToken[][]
        {
            [..recipe, new(3, 2), new(0, 1), new(0, 1), new(3, 3)],
            [new(3, 0), new(6, 1)],
            [new(3, 0), new(6, 1), new(0, 1), new(3, 2), new(0, 10), new(3, 3)],
            [new(3, 0), new(6, 1), new(0, 1), new(3, 2), new(6, 7), new(0, 1), new(3, 3)],
            [..recipe, new(3, 6), new(6, 7), new(0, 1)],
            [new(3, 0), new(6, 1), new(0, 1), new(3, 4), new(0, 0), new(0, 1), new(3, 5)],
            [new(3, 0), new(6, 1), new(0, 1), new(3, 2), new(0, 1), new(0, -1), new(3, 3)]
        }) That(!Parse(invalid).Diagnostics.IsEmpty, "畸形或重复配方字段保留诊断，不能静默获得制作资格");

        PvfToken[] making = [new(3, 14), new(0, 0), new(0, 1), new(0, 20), new(0, 20), new(3, 15),
            new(3, 16), new(0, 1), new(0, 3), new(3, 17), new(0, 2), new(3, 18),
            new(0, 1), new(0, 4), new(0, 6), new(0, 9700), new(0, 6), new(0, 12), new(0, 300), new(3, 19)];
        var rules = ItemMakingSkillFile.Parse(making, Resolve);
        That(rules.Limits is [{ Points: 0, CharacterLevel: 1 }, { Points: 20, CharacterLevel: 20 }] &&
            rules.ExperienceBase == 1 && rules.ExperienceRange == 3 && rules.RecipeLevelGap == 2 &&
            rules.Upgrades is [{ Level: 1, CommonMinimum: 4, CommonMaximum: 6, CommonWeight: 9700, RareMinimum: 6, RareMaximum: 12, RareWeight: 300 }],
            "制作熟练表按2列阈值、2列经验、7列强化完整读取");
        Reject<InvalidDataException>(() => ItemMakingSkillFile.Parse(making[..^2], Resolve));
        Reject<InvalidDataException>(() => ItemMakingSkillFile.Parse([..making, new(3, 16), new(0, 1), new(0, 3)], Resolve));
    }
}
