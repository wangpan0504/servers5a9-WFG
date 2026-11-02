using System.Collections.Immutable;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonRewardDisplayTests
{
    internal static void Run()
    {
        int before = Count;
        var grant = new InventoryReward(0x12345678, 0x3456) { EnchantUpgrade = 0xab };
        var frozen = AntonRewardDisplayItem.FromFrozen(grant);
        That(frozen is { ItemId: 0x12345678, DisplayNumber: 0x3456, EnchantUpgrade: 0xab,
            RawCode: null, RawFlag: null, RawTail1: null }, "冻结来源只绑定物品、展示数和实际附魔升级");
        // 仅布局测试的哨兵，不是生产合法值或来源证据。
        var explicitItem = frozen with { RawCode = 0x12, RawFlag = 0x34, RawTail1 = 0x89abcdef };
        byte[] one = [1,1,0x21,0x43,2,1,0x12,0x78,0x56,0x34,0x12,0x56,0x34,0x34,
            0xef,0xcd,0xab,0x89,0xab,0,0,0];
        Bytes(Encode([explicitItem]), one, "普通团单项22B逐字节，小端两尾且附魔字节零扩展");
        byte[] three = [1,1,0x21,0x43,2,3, ..one[6..], ..one[6..], ..one[6..]];
        Bytes(Encode([explicitItem, explicitItem, explicitItem]), three, "三项54B不丢尾部或错位");
        Bytes(AntonRewardPackets.Result(1, [new(0x4321, 2, [explicitItem]), new(0x4321, 2, [explicitItem])]),
            [1,2, ..one[2..], ..one[2..]], "多行独立行头及计数");
        foreach (var incomplete in new[] { explicitItem with { RawCode = null }, explicitItem with { RawFlag = null },
            explicitItem with { RawTail1 = null } })
            Reject<InvalidDataException>(() => Encode([incomplete]), "缺任一未证列禁止编码，不能补常数");
        That(!AntonRewardPackets.TryFrozenResult(1, 0x4321, 2, [grant], out var blocked, out var missing) &&
            missing == "RawCode,RawFlag,RawTail1", "正式冻结来源门控关闭且逐列报告缺证");
        Bytes(blocked, [1,0], "门控失败仅返回空选择列表");
        Bytes(AntonRewardPackets.Choices(0), [0,0], "初始选择列表不提前写本人已选行");
        Reject<InvalidDataException>(() => AntonRewardDisplayItem.FromFrozen(grant with { Count = 65536 }), "数量溢出拒绝截断");
        Reject<InvalidDataException>(() => AntonRewardPackets.Result(2, []), "不扩展牌种");
        Console.WriteLine($"PASS {Count-before} 安徒恩普通团600字段合同断言");
    }

    static byte[] Encode(ImmutableArray<AntonRewardDisplayItem> items) =>
        AntonRewardPackets.Result(1, [new(0x4321, 2, items)]);
}
