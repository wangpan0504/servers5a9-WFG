using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class RecoveryConsumableTests
{
    public static void Run()
    {
        UseRestrictionDeclarations();
        string[] strings = ["[cool time]", "[cooltime group]", "[hp recovery]", "+", "myself", "[need material]", "[sub type]",
            "[stat change duration]", "party", "[effect maintenance]", "[cooltime maintenance]", "[quest exp bonus rate]", "[exp bonus rate]",
            "[total usable count]", "[attach type]", "[trade]", "[account]", "[action usable dungeon]", "[/action usable dungeon]",
            "[stat change]", "[/stat change]", "physical attack", "%", "physical attack bonus",
            "[stackable type]", "[disguise]", "[string data]", "[/string data]", "monster", "[int data]", "[/int data]"];
        string Resolve(int at) => strings[at];
        PvfToken[] valid = [new(3, 0), new(0, 10000), new(3, 1), new(0, 0), new(3, 2),
            new(6, 3), new(0, 410), new(0, 1000), new(6, 4)];
        var questPotion = RecoveryConsumableFile.Parse(1, "stackable/quest.stk", [new(3,11),new(2,BitConverter.SingleToInt32Bits(0.5f)),
            new(3,12),new(0,1),new(3,7),new(0,7200000),new(6,4)], Resolve);
        That(questPotion.QuestExpBonusPermille == 500 && questPotion.ExpBonusPermille == 1000 &&
            questPotion.ExpBonusMilliseconds == 7200000, "任务和普通经验倍率独立保留，持续时间来自自身效果声明");
        var result = RecoveryConsumableFile.Parse(1108, "stackable/example.stk", valid, Resolve);
        PvfToken[] disguiseHeader = [new(3,24),new(6,25),new(0,0)];
        PvfToken[] disguiseTail = [new(3,26),new(6,28),new(3,27),new(3,29),new(0,63039),new(0,60),new(3,30)];
        var disguise = RecoveryConsumableFile.Parse(10312386, "stackable/disguise.stk", [..disguiseHeader,..disguiseTail], Resolve);
        That(disguise.Disguise == new DisguiseDeclaration("monster",63039,60) && disguise.Diagnostics.IsEmpty,
            "变身保留完整形态及秒数，源模板ID与形态ID分离");
        foreach (PvfToken[] tail in new PvfToken[][] {
            [new(3,26),new(6,28),new(3,27)],
            [new(3,26),new(6,28),new(3,27),new(3,29),new(0,63039),new(3,30)],
            [..disguiseTail,new(3,29),new(0,63039),new(0,60),new(3,30)],
            [new(3,26),new(6,28),new(3,27),new(3,29),new(0,63039),new(0,0),new(3,30)] })
        {
            var invalidDisguise = RecoveryConsumableFile.Parse(10312386,"stackable/disguise.stk",[..disguiseHeader,..tail],Resolve);
            That(invalidDisguise.Disguise is null && !invalidDisguise.Diagnostics.IsEmpty,
                "缺失、截断、重复及非正时长不产生可兑现形态");
        }
        var statPotion = RecoveryConsumableFile.Parse(1, "stackable/stat.stk", [..valid,
            new(3,19),new(6,3),new(0,175),new(6,21),new(6,22),new(0,12),new(6,23),new(3,20),
            new(3,7),new(0,60000),new(6,4)], Resolve);
        That(statPotion.StatChanges.SequenceEqual(new StatChangeDeclaration[]
            { new("+",175,"physical attack"), new("%",12,"physical attack bonus") }) &&
            statPotion.StatChangeDuration is { Milliseconds: 60000, Target: "myself" } && statPotion.Diagnostics.IsEmpty,
            "持续属性保留原操作、整数值、属性名及持续时间，不把属性数值当服务端攻击状态");
        foreach (PvfToken[] tail in new PvfToken[][] { [new(3,19),new(3,20)],
            [new(3,19),new(6,3),new(0,175),new(3,20)],
            [new(3,19),new(6,3),new(6,3),new(6,21),new(3,20)],
            [new(3,19),new(6,3),new(0,175),new(6,21),new(3,20),new(3,19),new(6,3),new(0,12),new(6,23),new(3,20)] })
        {
            var malformed = RecoveryConsumableFile.Parse(1, "stackable/stat.stk", [..valid,..tail], Resolve);
            That(malformed.StatChanges.IsEmpty && !malformed.Diagnostics.IsEmpty,
                "空、缺尾、错误类型或重复属性声明不产生部分属性向量");
        }
        That(result.ActionUsableDungeons is null, "未声明可用地下城时保持缺失，不添加漩涡专属限制");
        var restricted = RecoveryConsumableFile.Parse(10312384, "stackable/fiendwar.stk",
            [..valid, new(3,17), new(0,291100339), new(0,5350), new(3,18)], Resolve);
        That(restricted.ActionUsableDungeons is { } allowed && allowed.SequenceEqual(new[] {291100339,5350}) && restricted.Diagnostics.IsEmpty,
            "团队与引导允许名单按原声明保留，不把DGN编号当地图编号");
        foreach (PvfToken[] tail in new PvfToken[][] { [new(3,17),new(3,18)], [new(3,17),new(0,0),new(3,18)],
            [new(3,17),new(0,-1),new(3,18)], [new(3,17),new(6,3),new(3,18)],
            [new(3,17),new(0,5350),new(3,18),new(3,17),new(0,5351),new(3,18)] })
        {
            var malformed = RecoveryConsumableFile.Parse(10312384, "stackable/fiendwar.stk", [..valid,..tail], Resolve);
            That(malformed.ActionUsableDungeons is null && !malformed.Diagnostics.IsEmpty,
                "空、非正整数、错误类型和重复可用地下城声明不能降级成无限制");
        }
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
        result = RecoveryConsumableFile.Parse(490004967, "stackable/example.stk",
            [..valid,new(3,13),new(0,1),new(3,13),new(0,1)], Resolve);
        That(result.TotalUsableCount == 1 && result.Diagnostics.IsEmpty,
            "相同完整日次数重复声明保持一次限制，不误拒绝疲劳灵药");
        foreach (PvfToken[] tail in new PvfToken[][] { [new(3,13),new(6,3)], [new(3,13),new(0,1),new(3,13),new(0,2)],
            [new(3,13),new(0,1),new(3,13)], [new(3,13),new(0,1),new(3,13),new(6,3)] })
        {
            result = RecoveryConsumableFile.Parse(1, "stackable/example.stk", [..valid,..tail], Resolve);
            That(result.TotalUsableCount is null && !result.Diagnostics.IsEmpty, "畸形或重复日次数不能被解释为无限使用");
        }
    }

    static void UseRestrictionDeclarations()
    {
        string[] strings = ["[cool time]", "[cooltime group]", "[limit dungeon]", "[/limit dungeon]",
            "[impossible contents]", "[/impossible contents]", "gift", "raid dungeon"];
        string Resolve(int at) => strings[at];
        PvfToken[] header = [new(3, 0), new(0, 30000), new(3, 1), new(0, 865)];
        RecoveryConsumableFile Read(params PvfToken[] restrictions) => RecoveryConsumableFile.Parse(490013727,
            "stackable/restriction-fixture.stk", [.. header, .. restrictions], Resolve);
        var absent = Read();
        That(absent.LimitDungeons.IsEmpty && absent.ImpossibleContents.IsEmpty && absent.Diagnostics.IsEmpty,
            "未声明使用限制保持空集合，不给普通药补禁图或禁止用途");
        That(Read(new(3, 2), new(3, 3)).LimitDungeons is { IsDefault: false, IsEmpty: true },
            "完整空禁图块保持原有无限制含义，不因本次名单传递收窄普通药资格");
        var complete = Read(new(3, 2), new(0, 210), new(0, 5313), new(3, 3),
            new(3, 4), new(6, 6), new(3, 5), new(3, 4), new(6, 7), new(6, 6), new(3, 5));
        That(complete.LimitDungeons.SequenceEqual(new[] { 210, 5313 }) &&
            complete.ImpossibleContents.SequenceEqual(new[] { "gift", "raid dungeon" }) && complete.Diagnostics.IsEmpty,
            "显式禁图完整保留，多个完整禁止用途块按原序并集读取，不丢第二块raid声明");
        foreach (PvfToken[] tail in new PvfToken[][] {
            [new(3, 2), new(0, 0), new(3, 3)],
            [new(3, 2), new(0, -1), new(3, 3)], [new(3, 2), new(0, 210), new(6, 6), new(3, 3)],
            [new(3, 2), new(0, 210), new(3, 3), new(3, 2), new(0, 5313), new(3, 3)] })
        {
            var malformed = Read(tail);
            That(malformed.LimitDungeons.IsDefault && !malformed.Diagnostics.IsEmpty,
                "非正、混合类型或重复禁图声明明确无效，不过滤坏项成为部分或空名单");
        }
        foreach (PvfToken[] tail in new PvfToken[][] {
            [new(3, 4), new(0, 1), new(3, 5)], [new(3, 4), new(6, 7)],
            [new(3, 4), new(3, 2), new(0, 210), new(3, 3), new(3, 5)] })
        {
            var malformed = Read(tail);
            That(malformed.ImpossibleContents.IsDefault && !malformed.Diagnostics.IsEmpty,
                "错误类型、未闭合或嵌套禁止用途声明保留失败状态，不能默认为无限制");
        }
    }
}
