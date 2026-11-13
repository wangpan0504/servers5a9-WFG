using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class EquipmentQuestDropBonusTests
{
    public static void Run()
    {
        int Read(string text) => EquipmentQuestDropBonusFile.Read(new(text));
        That(Read("[name] `ordinary`") == 0 && Read("[quest item drop rate bonus] 0") == 0,
            "缺少声明与显式0均保留零加成");
        That(Read("[quest item drop rate bonus] 50") == 50 &&
            Read("[quest item drop rate bonus] 7\n[quest item drop rate bonus] -5") == -5,
            "保留有符号整数和重复标量的最后声明");
        foreach (string bad in new[] { "", "7 8", "7.0", "text", "2147483648" })
            Reject<InvalidDataException>(() => Read("[quest item drop rate bonus] " + bad));
        var scripts = new Dictionary<string, string>
        {
            ["equipment/base.equ"] = "[quest item drop rate bonus] 7",
            ["equipment/child.equ"] = "[import script] `base.equ`\n[quest item drop rate bonus] 0"
        };
        That(EquipmentQuestDropBonusFile.Read(EquipmentFile.ReadScript(path => scripts[path], "equipment/child.equ")) == 0,
            "装备导入沿现有合并规则，子层显式0覆盖父层加成");

        string[] tags = ["[piece set ability]", "[/piece set ability]", "[quest item drop rate bonus]", "[HP MAX]"];
        PvfTokenDocument Parse(PvfToken[] tokens) => new(tokens, index => tags[index]);
        var tiers = EquipmentQuestDropBonusFile.ReadSet(Parse([new(3, 0), new(0, 2), new(3, 3), new(0, 10), new(3, 1),
            new(3, 0), new(0, 3), new(3, 2), new(0, 9), new(3, 1),
            new(3, 0), new(0, 5), new(3, 2), new(0, -2), new(3, 1)]));
        That(tiers.SequenceEqual(new[] { new EquipmentQuestDropTier(3, 9), new EquipmentQuestDropTier(5, -2) }),
            "只返回本属性的套装阶梯，不把其它属性当作任务加成");
        foreach (var bad in new PvfToken[][]
        {
            [new(3, 0), new(0, 0), new(3, 2), new(0, 9), new(3, 1)],
            [new(3, 0), new(0, 3), new(3, 2), new(0, 9), new(0, 1), new(3, 1)],
            [new(3, 0), new(0, 3), new(3, 2), new(4, 9), new(3, 1)],
            [new(3, 0), new(0, 3), new(3, 2), new(0, 9), new(3, 1),
                new(3, 0), new(0, 3), new(3, 2), new(0, 8), new(3, 1)]
        }) Reject<InvalidDataException>(() => EquipmentQuestDropBonusFile.ReadSet(Parse(bad)));
    }
}
