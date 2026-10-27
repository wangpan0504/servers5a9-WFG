using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnhancementTests
{
    static void RandomCoupons()
    {
        const int coupon = 990211, gear = 990212;
        const string text = "[enchant random]\n[er_grade]\n0 1 3\n[er_slot]\n1\n[er_seal]\n1\n" +
            "[er_enchant]\n3 7 2 9 0 13 3\n[/er_enchant]\n[/enchant random]\n";
        var rule = EnhancementCatalog.Parse(coupon, text)!;
        That(rule is { Available: true, RandomWeight: 5, SlotClass: 1, SealedOnly: true } &&
            EnhancementRequests.Read(Request(2, 0, 9, gear, 65, [])).Mode == EnhancementMode.RandomReinforce,
            "随机强化请求2和独立权重表可用，保留零权重、部位、稀有度及封装条件");
        foreach (string invalid in new[] { text + text, text.Replace("3 7 2", "2 7 2"), text.Replace("13 3", "32 3"),
            text.Replace("13 3", "13 -1"), text.Replace("3 7 2 9 0 13 3", "1 7 0"), text.Replace("[/er_enchant]", ""),
            text.Replace("[er_seal]", "[unknown]"), text + "[action type]\n`[random upgrade separate]`\n[/action type]\n" })
            That(EnhancementCatalog.Parse(coupon, invalid) is { Available: false }, "重复、缺字段、溢出、负权重及独立锻造动作拒绝");
        var sourceTemplate = new ItemTemplate(coupon, ItemKind.Consumable, 0, 1, 0, 10, 0, 3, 1, "[free]", ushort.MaxValue, 0, "随机券");
        var targetTemplate = new ItemTemplate(gear, ItemKind.Equipment, 12, 10, 40, 1, 0, 3, 1, "[free]", ushort.MaxValue, 0, "武器");
        var source = new InventoryItem(1, new(InventoryListType.Main, 65), new(coupon, ItemKind.Consumable, 2));
        var target = new InventoryItem(2, new(InventoryListType.Main, 9), new(gear, ItemKind.Equipment, 123456)
            { Attr = 0xEC, SealFlag = 1, Durability = 20, EnchantCardId = 3175, GenuineUpgrade = 4, SortLockFlag = 1 });
        Character role = new(1, 0, "RandomCoupon", 0, new(0, 0, 0, 0), null)
            { Level = 20, Inventory = new([source, target], Gold: 1234) };
        var request = new EnhancementRequest(EnhancementMode.RandomReinforce, target.Position, gear, 65, -1, ReadOnlyMemory<byte>.Empty, -1, -1);
        InventoryResult Plan(Character character, ItemTemplate template, Func<int> roll, out EnhancementReceipt? receipt) =>
            InventoryEnhancementService.Plan(character, request, 2_000_000_000, rule, sourceTemplate, template, roll, out receipt);
        foreach (var (rolled, expected) in new[] { (0, 7), (1, 7), (2, 13), (4, 13) })
        {
            var result = Plan(role, targetTemplate, () => rolled, out var receipt);
            That(result.Accepted && receipt is { Succeeded: true, Path: 1, InPlace: true, MaterialRemaining: 1 } && receipt.NewLevel == expected &&
                result.Plan!.After.Find(target.Position) == target with { Core = target.Core with { Attr = (byte)(0xE0 | expected) } } &&
                result.Plan.After.Gold == 1234 && result.Plan.After.Find(source.Position)!.Core.Count == 1,
                "随机覆盖可降级或升级，原始区间边界排除零权重，完整UID和核心保留且只扣一券");
            byte[] ack = EnhancementPackets.Ack(result, receipt);
            That(ack.Length == 27 && ack[1] == 2 && ack[10] == 1 && ack[12] == 0 && ack[13] == expected,
                "随机回包携带独立模式及绝对覆盖结果，降到低等级仍为已提交的成功抽取");
        }
        int NeverRoll() => throw new InvalidOperationException("资格拒绝前不得随机");
        foreach (var template in new[] { targetTemplate with { EquipmentSlot = 19 }, targetTemplate with { Rarity = 4 } })
            That(!Plan(role, template, NeverRoll, out _).Accepted, "武器及稀有度限制在消费前生效");
        foreach (var core in new[] { target.Core with { SealFlag = 0 }, target.Core with { AmplifyType = 3, AmplifyValue = 6 },
            target.Core with { EquipmentLockId = 1 }, target.Core with { ExpireTime = 2_000_000_000 } })
            That(!Plan(role with { Inventory = role.Inventory with { Items = [source, target with { Core = core }] } },
                targetTemplate, NeverRoll, out _).Accepted, "封装、异次元状态、锁及期限不能被随机强化绕过");
        Reject<InvalidOperationException>(() => Plan(role, targetTemplate, () => 5, out _), "权重随机源越界拒绝");
    }
}
