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
        That(AntonRewardPackets.TryFrozenResult(1, 0x4321, 2, [grant], out var plain, out var missing) &&
            missing == "", "已授予冻结物品显式使用普通可见展示策略");
        Bytes(plain, [1,1,0x21,0x43,2,1,0,0x78,0x56,0x34,0x12,0x56,0x34,0,0,0,0,0,0xab,0,0,0],
            "普通团16B包含完整两个u32尾，实际附魔不被普通策略覆盖");
        That(!AntonRewardPackets.TryFrozenResult(1, 0x4321, 2, [], out var blocked, out missing) &&
            missing == "empty-grants", "空资产不能伪造成非空结果");
        Bytes(blocked, [1,0], "空资产保留空选择列表");
        Bytes(AntonRewardPackets.Choices(0), [0,0], "初始选择列表不提前写本人已选行");
        Reject<InvalidDataException>(() => AntonRewardDisplayItem.FromFrozen(grant with { Count = 65536 }), "数量溢出拒绝截断");
        Bytes(AntonRewardPackets.Result(2, []), [2,0], "PC房结果沿公共16B读取器");
        Bytes(AntonRewardPackets.Result(3, []), [3,0], "全团结果沿公共16B读取器");
        Reject<InvalidDataException>(() => AntonRewardPackets.Result(4, []), "拒绝本实现外牌种");
        Console.WriteLine($"PASS {Count-before} 安徒恩普通团600字段合同断言");
    }

    static byte[] Encode(ImmutableArray<AntonRewardDisplayItem> items) =>
        AntonRewardPackets.Result(1, [new(0x4321, 2, items)]);
}
