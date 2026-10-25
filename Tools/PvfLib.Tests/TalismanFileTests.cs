using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class TalismanFileTests
{
    public static void Run()
    {
        string[] labels = ["[equipment type]", "[talisman]", "[usable job]", "[swordman]", "[/usable job]",
            "[useable growtype]", "[/useable growtype]", "[rune skillIndex]", "[stackable type]", "[rune]",
            "[usable first growtype]", "[/usable first growtype]", "[rune option]", "terracotta",
            "[equipment parameter]", "[/equipment parameter]", "[compound info]", "[/compound info]",
            "[random choose info]", "[/random choose info]", "[talisman refine material index]",
            "[talisman refine material count]", "[/talisman refine material]", "unknown"];
        string Resolve(int index) => labels[index];
        PvfToken[] talisman = [new(3, 0), new(6, 1), new(0, 16), new(3, 2), new(6, 3), new(3, 4),
            new(3, 5), new(0, 1), new(3, 6), new(3, 7), new(0, 9)];
        var stone = TalismanFile.Parse(100400002, "stone.equ", talisman, Resolve);
        That(stone.Equipment.DeclaredEquipmentType == new EquipmentTypeDeclaration("[talisman]", 16) &&
            stone.DeclaredGrowTypes is { Length: 1 } grow && grow[0] == 1 && stone.DeclaredSkillIndex == 9,
            "护石保留子类型16、原拼写首转与技能，不将16当装备槽");
        var placeholder = TalismanFile.Parse(100400000, "placeholder.equ", talisman[..6], Resolve);
        That(placeholder.DeclaredSkillIndex is null && placeholder.DeclaredGrowTypes is null,
            "占位护石缺失首转和技能时保持缺失，不生成默认值");
        Reject<InvalidDataException>(() => TalismanFile.Parse(1, "duplicate.equ",
            [.. talisman, new(3, 7), new(0, 10)], Resolve));
        Reject<InvalidDataException>(() => TalismanFile.Parse(1, "empty.equ",
            [.. talisman[..6], new(3, 5), new(3, 6)], Resolve));
        Reject<InvalidDataException>(() => TalismanFile.Parse(1, "type.equ",
            [.. talisman[..6], new(3, 7), new(6, 23)], Resolve));
        Reject<InvalidDataException>(() => TalismanFile.Parse(1, "nested.equ",
            [.. talisman, new(3, 14), new(3, 7), new(0, 9), new(3, 15)], Resolve));
        Reject<InvalidDataException>(() => TalismanFile.Parse(1, "unclosed-grow.equ",
            [.. talisman[..6], new(3, 5), new(0, 1)], Resolve));
        Reject<InvalidDataException>(() => TalismanFile.Parse(1, "unclosed-job.equ",
            [new(3, 0), new(6, 1), new(0, 16), new(3, 2), new(6, 3)], Resolve));
        Reject<InvalidDataException>(() => TalismanFile.Parse(1, "nested-type.equ",
            [new(3, 14), new(3, 0), new(6, 1), new(0, 16), new(3, 15)], Resolve));

        PvfToken[] rune = [new(3, 8), new(6, 9), new(0, 0), new(3, 2), new(6, 3), new(3, 4),
            new(3, 10), new(0, 1), new(3, 11), new(3, 7), new(0, 9),
            new(3, 12), new(6, 13), new(0, 2)];
        var parsedRune = RuneFile.Parse(10605203, "rune.stk", rune, Resolve);
        That(parsedRune.Stackable.TypeDeclaration?.Label == "[rune]" &&
            parsedRune.Stackable.DeclaredUsableFirstGrowTypes!.SequenceEqual(new[] { 1 }) &&
            parsedRune.DeclaredSkillIndex == 9 && parsedRune.Option == new RuneOptionDeclaration("terracotta", 2),
            "符文保留职业首转、技能和阵营品级，不把品级解释为颜色位");
        Reject<InvalidDataException>(() => RuneFile.Parse(1, "duplicate.stk",
            [.. rune, new(3, 12), new(6, 13), new(0, 0)], Resolve));
        Reject<InvalidDataException>(() => RuneFile.Parse(1, "wrong-grade.stk",
            [.. rune[..^1], new(6, 23)], Resolve));
        Reject<InvalidDataException>(() => RuneFile.Parse(1, "nested-grow.stk",
            [.. rune, new(3, 14), new(3, 10), new(0, 1), new(3, 11), new(3, 15)], Resolve));
        Reject<InvalidDataException>(() => RuneFile.Parse(1, "unclosed-grow.stk",
            [.. rune[..6], new(3, 10), new(0, 1)], Resolve));

        PvfToken[] system = [new(3, 16), new(0, 1), new(0, 0), new(0, 64000), new(3, 17),
            new(3, 16), new(0, 0), new(0, 1), new(0, 12000), new(3, 17),
            new(3, 18), new(0, 0), new(0, 7500), new(0, 1), new(0, 2500), new(3, 19),
            new(3, 20), new(0, 490014022), new(3, 21), new(0, 2), new(0, 1), new(0, 3), new(0, 2), new(3, 22)];
        var rules = RuneSystemFile.Parse(system, Resolve);
        That(rules.CompoundInfo.Length == 2 && rules.CompoundInfo[0].SequenceEqual(new[] { 1, 0, 64000 }) &&
            rules.CompoundInfo[1].SequenceEqual(new[] { 0, 1, 12000 }),
            "合成重复表按输入反序保留，不按模式排序或合并");
        That(rules.RandomChooseInfo.Single().SequenceEqual(new[] { 0, 7500, 1, 2500 }) &&
            rules.RefineMaterialId == 490014022 && rules.RefineMaterialCounts!.Value.SequenceEqual(new[] { 2, 1, 3, 2 }),
            "重锻材料表兼容本版少count的结束标签，概率列原值保留");
        Reject<InvalidDataException>(() => RuneSystemFile.Parse([.. system, new(3, 20), new(0, 123)], Resolve));
        Reject<InvalidDataException>(() => RuneSystemFile.Parse([new(3, 16), new(6, 23), new(3, 17)], Resolve));
        Reject<InvalidDataException>(() => RuneSystemFile.Parse(system[..^1], Resolve));
        Reject<InvalidDataException>(() => RuneSystemFile.Parse(
            [new(3, 14), new(3, 16), new(0, 1), new(3, 17), new(3, 15)], Resolve));
        Reject<InvalidDataException>(() => RuneSystemFile.Parse(
            [new(3, 14), new(3, 18), new(0, 1), new(3, 19), new(3, 15)], Resolve));
    }
}
