using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class RecoveryConsumableTests
{
    public static void Run()
    {
        string[] strings = ["[cool time]", "[cooltime group]", "[hp recovery]", "+", "myself", "[need material]", "[sub type]",
            "[stat change duration]", "party", "[effect maintenance]", "[cooltime maintenance]", "[quest exp bonus rate]", "[exp bonus rate]",
            "[total usable count]", "[attach type]", "[trade]", "[account]"];
        string Resolve(int at) => strings[at];
        PvfToken[] valid = [new(3, 0), new(0, 10000), new(3, 1), new(0, 0), new(3, 2),
            new(6, 3), new(0, 410), new(0, 1000), new(6, 4)];
        var questPotion = RecoveryConsumableFile.Parse(1, "stackable/quest.stk", [new(3,11),new(2,BitConverter.SingleToInt32Bits(0.5f)),
            new(3,12),new(0,1),new(3,7),new(0,7200000),new(6,4)], Resolve);
        That(questPotion.QuestExpBonusPermille == 500 && questPotion.ExpBonusPermille == 1000 &&
            questPotion.ExpBonusMilliseconds == 7200000, "任务和普通经验倍率独立保留，持续时间来自自身效果声明");
        var result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid, Resolve);
        That(result.CoolTime == 10000 && result.CoolGroup == 0 && result.HpRecovery == new RecoveryDeclaration("+", 410, 1000, "myself") &&
            result.MpRecovery is null && result.Diagnostics.IsEmpty, "恢复原四元组、冷却声明和显式组0无损保存，不推断单位");
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid.Concat(new PvfToken[] { new(3, 5), new(0, 3035), new(0, 3) }).ToArray(), Resolve);
        That(result.DeclaredFields.Contains("need material"), "未实现副作用保留字段名，不能被恢复模型忽略");
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid.Concat(new PvfToken[] { new(3, 0), new(0, 20000) }).ToArray(), Resolve);
        That(result.CoolTime == 20000 && result.Diagnostics.IsEmpty, "重复冷却标量按原客户端顺序由最后一次完整声明覆盖");
        result = RecoveryConsumableFile.Parse(2600021, "stackable/example.stk",
            [..valid,new(3,0),new(0,10000),new(3,1),new(0,0)], Resolve);
        That(result.CoolTime == 10000 && result.CoolGroup == 0 && result.Diagnostics.IsEmpty,
            "相同冷却时间与显式组0重复声明幂等，不误拒绝精神刺激等原资源");
        result = RecoveryConsumableFile.Parse(2680383, "stackable/example.stk", [..valid,new(3,1),new(0,3),new(3,1),new(0,2)], Resolve);
        That(result.CoolGroup == 2 && result.Diagnostics.IsEmpty, "活动消耗品多个冷却组取最后声明，不误用第一组");
        foreach (PvfToken[] tail in new PvfToken[][] { [new(3,1)],
            [new(3,1),new(6,3)], [new(3,1),new(0,0),new(0,0)] })
        {
            result = RecoveryConsumableFile.Parse(2600021, "stackable/example.stk", [..valid,..tail], Resolve);
            That(result.CoolGroup is null && !result.Diagnostics.IsEmpty,
                "空字段、错类型与多值冷却声明仍拒绝，不借重复兼容放行");
        }
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid[..^1], Resolve);
        That(result.HpRecovery is null && result.Diagnostics.Length != 0, "恢复四元组缺尾不能取得药剂资格");
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid.Select((token, at) => at == 7 ? new PvfToken(6, 3) : token).ToArray(), Resolve);
        That(result.HpRecovery is null && result.Diagnostics.Length != 0, "恢复整数位置不接受字符串或未知格式");
        That(RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid, Resolve).SubType is null,
            "未声明子类型保持缺失，不由解析库决定应答时机");
        result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", [..valid,new(3,6),new(0,2)], Resolve);
        That(result.SubType == 2 && result.DeclaredFields.Contains("sub type"), "子类型独立于stackable type的整数参数读取");
        foreach (PvfToken[] tail in new PvfToken[][] { [new(3,6),new(6,3)], [new(3,6)],
            [new(3,6),new(0,2),new(0,0)], [new(3,6),new(0,2),new(3,6),new(0,0)] })
        {
            result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", [..valid,..tail], Resolve);
            That(result.SubType is null && !result.Diagnostics.IsEmpty, "错误或重复子类型保留诊断，不降级成即时效果");
        }
        result = RecoveryConsumableFile.Parse(10087825, "stackable/example.stk", [..valid,
            new(3,7),new(0,1800000),new(6,4),new(3,9),new(0,1),new(0,1),new(0,1),new(0,0),
            new(3,10),new(0,1),new(0,1),new(0,1)], Resolve);
        That(result.StatChangeDuration == new StatChangeDurationDeclaration(1800000, "myself", null) &&
            result.EffectMaintenance == 7 && result.CooltimeMaintenance == 7 && result.Diagnostics.IsEmpty,
            "持续时间和保留位独立于经验倍率解析，毫秒不误当秒");
        result = RecoveryConsumableFile.Parse(1, "stackable/example.stk", [..valid,new(3,7),new(0,604800000),new(6,8)], Resolve);
        That(result.StatChangeDuration == new StatChangeDurationDeclaration(604800000, "party", null) && result.ExpBonusMilliseconds is null,
            "通用持续时间不限制24小时或自身目标，保留七天及party声明，不冒充自身经验秘药");
        result = RecoveryConsumableFile.Parse(1, "stackable/example.stk", [..valid,new(3,7),new(0,1500),new(6,4),new(0,5)], Resolve);
        That(result.StatChangeDuration == new StatChangeDurationDeclaration(1500, "myself", 5), "原版三参数形状保留附加参数，不截断或猜含义");
        foreach (PvfToken[] tail in new PvfToken[][] { [new(3,7),new(0,0),new(6,4)],
            [new(3,9),new(0,1),new(0,1)], [new(3,10),new(0,1),new(0,2),new(0,1)] })
        {
            result = RecoveryConsumableFile.Parse(1, "stackable/example.stk", [..valid,..tail], Resolve);
            That(!result.Diagnostics.IsEmpty, "畸形效果时间和保留位显式诊断，不扩大保留范围");
        }
        result = RecoveryConsumableFile.Parse(1, "stackable/example.stk", [..valid,new(3,13),new(0,2),
            new(3,14),new(6,15),new(3,14),new(6,16)], Resolve);
        That(result.TotalUsableCount == 2 && result.StructureDiagnostics.IsEmpty && !result.Diagnostics.IsEmpty,
            "日次数按原始整数保留，重复交易元数据诊断不等于使用声明结构损坏");
        foreach (PvfToken[] tail in new PvfToken[][] { [new(3,13),new(6,3)], [new(3,13),new(0,1),new(3,13),new(0,2)] })
        {
            result = RecoveryConsumableFile.Parse(1, "stackable/example.stk", [..valid,..tail], Resolve);
            That(result.TotalUsableCount is null && !result.Diagnostics.IsEmpty, "畸形或重复日次数不能被解释为无限使用");
        }
    }
}
