using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SharedEffectItemTests
{
    public static void RunCore()
    {
        int checks = Count;
        byte[] request = [0xe3, 0xef, 0x98, 0, 65, 0];
        That(SharedEffectItemRequests.Read(request) == new SharedEffectItemRequest(10022883, 65),
            "CMD251按原生i32物品编号和u16主背包槽解析，不沿用旧版u8槽与容器格式");
        Reject<InvalidDataException>(() => SharedEffectItemRequests.Read(request.AsSpan(0, 5)), "共享特效请求截断拒绝");
        Reject<InvalidDataException>(() => SharedEffectItemRequests.Read([.. request, 0]), "共享特效请求多余容器字节拒绝");
        Bytes(SharedEffectItemPackets.Result(65, 2), [1, 65, 0, 2], "ACK251完整携带源槽及结果序号");
        Bytes(SharedEffectItemPackets.Result(65, 0, 17), [0, 17, 0], "ACK251失败使用原生错误路径");
        Bytes(SharedEffectItemPackets.Effect(258, 10022883, 2), [2, 1, 0xe3, 0xef, 0x98, 0, 2],
            "NOTI219携带u16场景角色、物品编号及结果序号");
        string[] strings = ["[stackable type]", "[random reward item]", "[shared effect]", "[cool time]",
            "[chn random image percent]", "dodge.ani", "normal.ani", "[/chn random image percent]"];
        PvfToken[] tokens = [new(3, 0), new(6, 1), new(0, 0), new(3, 2), new(0, 1), new(3, 3), new(0, 5000),
            new(3, 4), new(0, 0), new(0, 100000), new(0, 10002868), new(6, 5), new(6, 6), new(3, 7)];
        var file = SharedEffectConsumableFile.Parse(123, "stackable/fireworks.stk", tokens, id => strings[id]);
        That(file.Errors.Count == 0 && file.Outcomes is [{ Index: 0, Weight: 100000, RewardItemId: 10002868,
                DodgeAnimation: "dodge.ani", Animation: "normal.ani" }] &&
            SharedEffectConsumableDefinition.Parse(file) is { CoolTime: 5000, CoolGroup: -123, CooltimeMaintenance: 0,
                Outcomes: [{ Index: 0, Weight: 100000, BoosterId: 10002868 }] },
            "五列随机图像配置保留结果序号、权重、礼包引用和两条动画，默认冷却绑定源编号");
        foreach (var invalid in new[] { file with { SharedEffect = 0 }, file with { CooltimeMaintenance = 8 },
            file with { Outcomes = [file.Outcomes[0] with { Index = 1 }] },
            file with { Outcomes = [file.Outcomes[0] with { Weight = 0 }] },
            file with { Outcomes = [file.Outcomes[0] with { Weight = -1 }] } })
            That(SharedEffectConsumableDefinition.Parse(invalid) is null, "无效资格、冷却、稀疏编号或权重不静默执行");
        var truncated = SharedEffectConsumableFile.Parse(123, "stackable/fireworks.stk", tokens[..^2], id => strings[id]);
        That(truncated.Errors.Count > 0 && SharedEffectConsumableDefinition.Parse(truncated) is null,
            "动画或闭合结构截断不能变成可消费配置");
        string[] threeAnimationStrings = [.. strings, "front.ani", ""];
        PvfToken[] threeAnimationTokens = [.. tokens[..^1], new(6, 8), tokens[^1]];
        var threeAnimations = SharedEffectConsumableFile.Parse(124, "stackable/three_animations.stk",
            threeAnimationTokens, id => threeAnimationStrings[id]);
        That(threeAnimations.Errors.Count == 0 && SharedEffectConsumableDefinition.Parse(threeAnimations) is
            { Outcomes: [{ Index: 0, Weight: 100000, BoosterId: 10002868 }] },
            "六列随机图像配置的三条动画不能阻断原结果序号、权重和子礼包入口");
        That(file.Outcomes[0].AdditionalAnimation is null && threeAnimations.Outcomes[0].AdditionalAnimation == "front.ani",
            "旧两动画默认无额外路径，新三动画完整保留末列");
        var (index, weight, reward, dodge, animation) = threeAnimations.Outcomes[0];
        That(new SharedEffectOutcome(index, weight, reward, dodge, animation) == file.Outcomes[0],
            "第三动画属性不改变既有五参数构造和五字段解构合同");
        PvfToken[] firstFive = tokens[8..^1];
        PvfToken[] secondFive = [new(0, 1), new(0, 7), new(0, 0), new(6, 6), new(6, 5)];
        PvfToken[] firstSix = [.. firstFive, new(6, 8)];
        PvfToken[] secondSix = [.. secondFive, new(6, 9)];
        var twoFive = ReadRows([.. firstFive, .. secondFive]);
        var twoSix = ReadRows([.. firstSix, .. secondSix]);
        foreach (var multi in new[] { twoFive, twoSix })
            That(multi.Errors.Count == 0 && SharedEffectConsumableDefinition.Parse(multi) is
                { Outcomes: [{ Index: 0, Weight: 100000, BoosterId: 10002868 }, { Index: 1, Weight: 7, BoosterId: 0 }] } &&
                multi.Outcomes[1] is { DodgeAnimation: "normal.ani", Animation: "dodge.ani" },
                "统一五列和六列多行保留第二结果的序号、权重、空奖励和独立动画");
        That(twoFive.Outcomes.All(row => row.AdditionalAnimation is null) &&
            twoSix.Outcomes[0].AdditionalAnimation == "front.ani" && twoSix.Outcomes[1].AdditionalAnimation == "",
            "六列第二行的空动画路径原样保留，不串用上一行或降成五列");
        foreach (PvfToken[] invalidRows in new PvfToken[][]
        {
            [.. firstFive, .. secondSix], [.. firstSix, .. secondFive],
            [.. firstSix[..^1], new(0, 8)],
            [.. firstSix, .. secondSix[..^1], new(0, 9)],
            [new(6, 5), .. firstSix[1..]],
            [.. firstSix[..3], new(0, 5), .. firstSix[4..]],
            firstFive[..^1], [.. firstSix, .. secondSix[..^2]]
        })
        {
            var malformed = ReadRows(invalidRows);
            That(malformed.Errors.Count > 0 && SharedEffectConsumableDefinition.Parse(malformed) is null,
                "混合行宽、整数和动画错类型、缺列不能成为可消费的部分结果表");
        }
        Console.WriteLine($"PASS {Count - checks} 共享特效物品格式、资格与原生字节断言");

        SharedEffectConsumableFile ReadRows(PvfToken[] rows) => SharedEffectConsumableFile.Parse(124,
            "stackable/three_animations.stk", [.. tokens[..8], .. rows, tokens[^1]], id => threeAnimationStrings[id]);
    }
}
