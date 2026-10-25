using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TalismanTests
{
    static readonly ItemTemplate Stone = new(100400002, ItemKind.Talisman, 34, 95, 0, 1, 0, 3, 97,
        "[trade]", 1, 0, "stone");
    static readonly ItemTemplate Rune = new(10605203, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1,
        "[trade]", 1, 0, "rune", "[rune] 0");

    static TalismanFile StoneFile(ImmutableArray<int>? growTypes = null, int? skill = 9) => new(
        new(Stone.Id, "stone.equ", 95, null, 0, 0, null, null, ["[swordman]"], new("[talisman]", 16), []),
        growTypes ?? [1], skill);

    static RuneFile RuneFile() => new(new(Rune.Id, "rune.stk", new("[rune]", [0]), null, null, null, 95, null, [])
    {
        DeclaredUsableJobs = ["[swordman]"], HasUsableJobs = true, DeclaredUsableFirstGrowTypes = [1]
    }, 9, new("terracotta", 2));

    public static void RunCore()
    {
        int start = Count;
        var original = new ItemCore(Stone.Id, ItemKind.Talisman, 1)
        {
            Rune = 0x4321,
            Extension114 = new(0xAB, 0xFE000000),
            Extension119 = 0x31
        };
        var colored = new TalismanSocketColors(1, 3, 5).Apply(original);
        That(colored.Extension114 == new ItemExtension(0xAB, 0xFE050301) &&
            colored.Rune == 0x4321 && colored.Extension119 == 0x31,
            "护石孔色只占确认的三个字节，保留第四字节、flag、原Rune与史诗交换位");
        That(TalismanSocketColors.Read(colored) == new TalismanSocketColors(1, 3, 5) &&
            TalismanSocketColors.Read(colored)[2] == 5 && TalismanSocketColors.Read(colored).IsComplete &&
            !new TalismanSocketColors(0, 3, 5).IsComplete,
            "护石三孔按原生独立字节读取，零孔位不补成有效阵营");
        Reject(() => new TalismanSocketColors(6, 1, 2).Apply(original));
        Reject(() => new TalismanSocketColors(1, 6, 2).Apply(original));
        Reject(() => new TalismanSocketColors(1, 2, 6).Apply(original));
        var runeCore = new ItemCore(Rune.Id, ItemKind.Rune, 999);
        That(runeCore.IsStackable && runeCore.Count == 999 && !original.IsStackable && original.Count == 1,
            "符文数量与护石独立实例沿统一核心模型表达");
        var stone = TalismanDefinition.Parse(StoneFile(), Stone);
        That(stone.SkillIndex == 9 && stone.AllowsUse(95, 0, 1) && stone.AllowsUse(95, 0, 0x21),
            "护石按实际职业首转判断，觉醒高位不改变首转");
        That(!stone.AllowsUse(94, 0, 1) && !stone.AllowsUse(95, 1, 1) && !stone.AllowsUse(95, 0, 2),
            "护石等级、职业和首转分别限制，不按技能编号放宽");
        var placeholder = TalismanDefinition.Parse(StoneFile() with { DeclaredGrowTypes = null, DeclaredSkillIndex = null },
            Stone with { Rarity = 0, Jobs = ushort.MaxValue });
        That(placeholder.IsPlaceholder && !placeholder.AllowsUse(95, 0, 1),
            "碎裂护石可读取但不能变成有效护石");
        Reject(() => TalismanDefinition.Parse(StoneFile(skill: null), Stone));
        Reject(() => TalismanDefinition.Parse(StoneFile() with { DeclaredGrowTypes = null }, Stone));
        Reject(() => TalismanDefinition.Parse(StoneFile([]), Stone));
        Reject(() => TalismanDefinition.Parse(StoneFile([1, 1]), Stone));
        Reject(() => TalismanDefinition.Parse(StoneFile([6]), Stone));
        Reject(() => TalismanDefinition.Parse(StoneFile([-1]), Stone));
        Reject(() => TalismanDefinition.Parse(StoneFile(), Stone with { Id = 1 }));

        var rune = RuneDefinition.Parse(RuneFile(), Rune);
        That(rune.Camp == RuneCamp.Terracotta && rune.Grade == 2 && rune.SkillIndex == 9 && RuneDefinition.StackLimit == 999,
            "符文阵营、品级、技能独立保存，持有上限固定999");
        That(rune.AllowsUse(95, 0, 0x11) && !rune.AllowsUse(94, 0, 1) && !rune.AllowsUse(95, 1, 1) &&
            !rune.AllowsUse(95, 0, 2), "符文同时保留等级、职业和首转限制");
        Reject(() => RuneDefinition.Parse(RuneFile() with { Option = new("unknown", 0) }, Rune));
        Reject(() => RuneDefinition.Parse(RuneFile() with { Option = new("terracotta", -1) }, Rune));
        Reject(() => RuneDefinition.Parse(RuneFile() with { DeclaredSkillIndex = 0 }, Rune));
        Reject(() => RuneDefinition.Parse(RuneFile() with { Option = null }, Rune));
        Reject(() => RuneDefinition.Parse(RuneFile() with
        { Stackable = RuneFile().Stackable with { DeclaredUsableFirstGrowTypes = null } }, Rune));
        Reject(() => RuneDefinition.Parse(RuneFile() with
        { Stackable = RuneFile().Stackable with { DeclaredUsableJobs = [] } }, Rune));

        var costs = TalismanRefineCost.Parse(new([], [], 490014022, [2, 1, 3, 2]));
        That(costs.MaterialId == 490014022 && costs.CountsByRarity[2] == 1 && costs.CountsByRarity[3] == 2 &&
            !costs.CountsByRarity.ContainsKey(1), "护石重锻按资源品级数量表读取，未知品级不补费用");
        foreach (var invalid in new RuneSystemFile[]
        {
            new([], [], null, [2, 1]), new([], [], 0, [2, 1]), new([], [], 490014022, null),
            new([], [], 490014022, []), new([], [], 490014022, [2, 1, 3]),
            new([], [], 490014022, [2, 1, 2, 2]), new([], [], 490014022, [2, 0])
        })
            Reject(() => TalismanRefineCost.Parse(invalid));
        Console.WriteLine($"PASS {Count - start} talisman resource core checks");
    }

    static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException) { rejected = true; }
        That(rejected, "无效护石资源声明不得生成可用定义");
    }
}
