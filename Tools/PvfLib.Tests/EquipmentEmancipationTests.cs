using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class EquipmentEmancipationTests
{
    public static void Run()
    {
        string[] tags = ["[emancipate]", "[/emancipate]", "[input]", "[/input]", "[output]", "[/output]", "[emancipate type]", "[success rate]"];
        PvfToken[] tokens = [new(3, 0), new(3, 2), new(0, 10308622), new(0, 2), new(3, 3),
            new(3, 4), new(0, 101040551), new(0, 1), new(3, 5), new(3, 6), new(0, 23), new(3, 1)];
        EquipmentEmancipationFile? Parse(PvfToken[] value) => EquipmentEmancipationFile.Parse(value, i => tags[i]);
        var parsed = Parse(tokens);
        That(parsed is { Type: 23 } && parsed.Inputs is [{ ItemId: 10308622, Count: 2 }] &&
            parsed.Outputs is [{ ItemId: 101040551, Count: 1 }], "转换输入输出按二元组解析，不误当group三元组");
        That(Parse([..tokens, ..tokens]) is null, "重复转换块拒绝");
        That(Parse([..tokens[..^1], new(3, 6), new(0, 23), new(3, 1)]) is null, "重复转换类型拒绝");
        var negative = tokens.ToArray(); negative[3] = new(0, -2);
        That(Parse(negative) is null, "负材料数量拒绝");
        var nonInteger = tokens.ToArray(); nonInteger[3] = new(4, 2);
        That(Parse(nonInteger) is null, "非整数材料数量拒绝");
        That(Parse([..tokens[..^1], new(3, 7), new(0, 50), new(3, 1)]) is null, "未知概率或条件不能被静默丢弃");
        That(Parse([]) is null, "没有装备转换声明不合成默认配方");
        That(Parse([..tokens[..^3], new(3, 1)]) is { Type: 0 }, "旧勇者省略类型时按原生0号类型解析");
    }
}
