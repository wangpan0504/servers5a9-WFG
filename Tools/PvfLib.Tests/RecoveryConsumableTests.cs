using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class RecoveryConsumableTests
{
    public static void Run()
    {
        string[] strings = ["[cool time]", "[cooltime group]", "[hp recovery]", "+", "myself", "[need material]"];
        string Resolve(int at) => strings[at];
        PvfToken[] valid = [new(3, 0), new(0, 10000), new(3, 1), new(0, 0), new(3, 2),
            new(6, 3), new(0, 410), new(0, 1000), new(6, 4)];
        var result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid, Resolve);
        That(result.CoolTime == 10000 && result.CoolGroup == 0 && result.HpRecovery == new RecoveryDeclaration("+", 410, 1000, "myself") &&
            result.MpRecovery is null && result.Diagnostics.IsEmpty, "恢复原四元组、冷却声明和显式组0无损保存，不推断单位");
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid.Concat(new PvfToken[] { new(3, 5), new(0, 3035), new(0, 3) }).ToArray(), Resolve);
        That(result.DeclaredFields.Contains("need material"), "未实现副作用保留字段名，不能被恢复模型忽略");
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid.Concat(new PvfToken[] { new(3, 0), new(0, 20000) }).ToArray(), Resolve);
        That(result.CoolTime is null && result.Diagnostics.Length != 0, "重复冷却声明保留诊断，不猜覆盖优先级");
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid[..^1], Resolve);
        That(result.HpRecovery is null && result.Diagnostics.Length != 0, "恢复四元组缺尾不能取得药剂资格");
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid.Select((token, at) => at == 7 ? new PvfToken(6, 3) : token).ToArray(), Resolve);
        That(result.HpRecovery is null && result.Diagnostics.Length != 0, "恢复整数位置不接受字符串或未知格式");
    }
}
