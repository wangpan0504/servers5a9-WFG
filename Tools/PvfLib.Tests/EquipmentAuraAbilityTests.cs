using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class EquipmentAuraAbilityTests
{
    public static void Run()
    {
        string Block(string body) => "[aura ability]\n" + body + "\n[/aura ability]";
        foreach (var (label, value) in new (string, int?)[]
            { ("[none]", null), ("[party teleport]", 600), ("[solo teleport]", 420), ("[upgrade solo teleport]", 300), ("[upgrade solo teleport]", 0) })
            That(EquipmentAuraAbility.Read(new(Block("`" + label + "`" + (value is null ? "" : " " + value)))) == new EquipmentAuraAbility(label, value),
                "光环能力原样保留标签及冷却整数，不套用STK动作或旧版本编号");
        That(EquipmentAuraAbility.Read(new(Block(""))) == new EquipmentAuraAbility("[none]", null) &&
            EquipmentAuraAbility.Read(new("[name]\n`item`")) is null, "显式空能力和缺席声明都不授予传送能力");
        foreach (string invalid in new[] { "`[solo teleport]` 420 600", "`[solo teleport]` 2.5",
            "`[solo teleport]` `420`", "`[solo teleport]` 2147483648", "`[solo teleport]` 420\n[unexpected]\n1" })
            Reject<InvalidDataException>(() => EquipmentAuraAbility.Read(new(Block(invalid))));
        Reject<InvalidDataException>(() => EquipmentAuraAbility.Read(new("[aura ability]\n`[solo teleport]` 420")));
        Reject<InvalidDataException>(() => EquipmentAuraAbility.Read(new(Block("`[solo teleport]` 420") + "\n" + Block("`[party teleport]` 600"))));
        var scripts = new Dictionary<string, string>
        {
            ["equipment/base.equ"] = Block("`[upgrade solo teleport]` 300"),
            ["equipment/child.equ"] = "[import script]\n`base.equ`"
        };
        string Read(string path) => scripts[path];
        That(EquipmentAuraAbility.Read(EquipmentFile.ReadScript(Read, "equipment/child.equ")) == new EquipmentAuraAbility("[upgrade solo teleport]", 300),
            "光环能力沿装备正式import继承，不只扫描子文件字面标签");
        scripts["equipment/child.equ"] += "\n" + Block("`[none]`");
        That(EquipmentAuraAbility.Read(EquipmentFile.ReadScript(Read, "equipment/child.equ")) == new EquipmentAuraAbility("[none]", null),
            "子装备显式none覆盖父能力，不能借继承取得父模板权限");
    }
}
