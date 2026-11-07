using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class EquipmentUpgradeFileTests
{
    public static void Run()
    {
        EquipmentUpgradeFile Read(string text) => EquipmentUpgradeFile.Read(new(text));
        That(Read("[name]\n`ordinary`") == default, "无改造声明的装备保持零加成");
        That(Read("[increase upgrade compound rate]\n400.0 500\n[upgrade prob increase]\n2000\n" +
            "[upgrade cost discount]\n4.5 1 0\n") == new EquipmentUpgradeFile(400, 500, 2000, 4.5f, 1, 0),
            "改造原始声明保留两列成功率、旧权重、浮点折扣及模式标志");
        That(Read("[increase upgrade compound rate]\n400 0\n[increase upgrade compound rate]\n0 -5\n") is
            { ReinforcementRate: 0, AmplificationRate: -5 }, "重复声明取最后值，保留有符号原值");
        That(Read("[upgrade cost discount]\n3 2147483647 -2147483648\n") is
            { DiscountReinforcement: int.MaxValue, DiscountAmplification: int.MinValue },
            "整数标志不经过浮点转换，避免边界溢出或被误解为启用");
        foreach (string bad in new[]
        {
            "[increase upgrade compound rate]\n400", "[increase upgrade compound rate]\n400 0 1",
            "[increase upgrade compound rate]\nNaN 0", "[increase upgrade compound rate]\nInfinity 0",
            "[upgrade prob increase]\n", "[upgrade prob increase]\n1e100",
            "[upgrade cost discount]\n4", "[upgrade cost discount]\n4 1.5 0", "[upgrade cost discount]\n4 1 0 0",
            "[upgrade cost discount]\n4 2147483648 0"
        }) Reject<InvalidDataException>(() => Read(bad));
        var scripts = new Dictionary<string, string>
        {
            ["equipment/base.equ"] = "[increase upgrade compound rate]\n400 500\n[upgrade cost discount]\n4 1 1\n",
            ["equipment/child.equ"] = "[import script]\n`base.equ`\n[increase upgrade compound rate]\n0 0\n"
        };
        That(EquipmentUpgradeFile.Read(EquipmentFile.ReadScript(path => scripts[path], "equipment/child.equ")) is
            { ReinforcementRate: 0, AmplificationRate: 0, CostDiscount: 4, DiscountReinforcement: 1 },
            "装备导入保持子声明覆盖及未声明字段继承，不把显式零替换回父值");
    }
}
