using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EnhancementTests
{
    public static void RunCore()
    {
        var body = Request(0, 0, 21, 27308, 86, new byte[8]);
        var request = EnhancementRequests.Read(body);
        That(body.Length == 28 && request.Mode == EnhancementMode.Reinforce && request.Target == new InventorySlot(InventoryListType.Main, 21) &&
            request.TargetItemId == 27308 && request.MaterialSlot == 86 && request.ProtectionSlot == -1 && request.TargetText.Length == 8 &&
            request.AuxiliarySlot0 == -1 && request.AuxiliarySlot1 == -1, "CMD80完整保留真实28B字段边界及两个末尾槽，不记录名称正文");
        var empty = EnhancementRequests.Read(Request(1, 3, 12, 27308, 86, []));
        That(empty.Mode == EnhancementMode.Amplify && empty.TargetText.IsEmpty, "CMD80空DSTR仍保留末尾两个槽");
        var malformed = new[] { body[..^1], body.Concat(new byte[1]).ToArray(), new byte[19],
            Request(2, 0, 21, 27308, 86, []), Request(0, 0, -1, 27308, 86, []), Request(0, 0, 21, 0, 86, []),
            Request(0, 0, 21, 27308, 86, new byte[257]) };
        foreach (var value in malformed) Reject<InvalidDataException>(() => EnhancementRequests.Read(value), "错误模式/目标/长度及未知尾部拒绝");
        byte[] badLength = (byte[])body.Clone();
        Array.Fill(badLength, (byte)255, 12, 4);
        Reject<InvalidDataException>(() => EnhancementRequests.Read(badLength), "负数DSTR长度不能吞掉末尾槽");
        var outcome = new EnhancementReceipt(EnhancementMode.Amplify, new(InventoryListType.Equipment, 12), 86, 4, 9, 10, true);
        Bytes(EnhancementPackets.Ack(new(true, 0), outcome),
            [1, 1, 86, 0, 4, 0, 0, 0, 255, 255, 0, 9, 0, 10, 3, 12, 0, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0],
            "本版成功ACK80完整27B，模式及源绝对余量来自实际结果，末尾双状态不截断");
        That(EnhancementPackets.Ack(new(true, 0), outcome with { Succeeded = false, NewLevel = 9 })[12] == 1,
            "概率失败是已消费券后的成功事务，结果码1保级");
        Bytes(EnhancementPackets.Ack(InventoryResult.Reject(23), null), [0, 23, 0], "拒绝分支公共u16且没有成功字段");
        CatalogAndPlans();
    }

    static void CatalogAndPlans()
    {
        const int coupon = 990201, gear = 990202;
        const long now = 2_000_000_000;
        string fixedText = "[equipment reinforcement ticket]\n11 100 `fixed` -1\n[check usable itemlevel]\n1 95\n" +
            "[action usable place]\n`[village]` `[seria room]`\n[/action usable place]\n";
        var fixedRule = EnhancementCatalog.Parse(coupon, fixedText)!;
        That(fixedRule.Available && fixedRule.TryTargetLevel(14, out byte lower) && lower == 11, "固定券覆盖已有等级且保留装备等级范围");
        That(fixedRule.TryTargetLevel(31, out byte capped) && capped == 11 &&
            !EnhancementCatalog.Parse(coupon, "[equipment reinforcement ticket]\n1 100 `additional` -1\n")!.TryTargetLevel(31, out _),
            "有效五位等级31仍能被固定券覆盖，追加券溢出保持拒绝");
        var additional = EnhancementCatalog.Parse(coupon, "[equipment reinforcement ticket]\n2 30 `additional` 12\n")!;
        That(additional.TryTargetLevel(12, out byte more) && more == 14 && !additional.TryTargetLevel(13, out _), "增量券保留源等级上限");
        That(additional.RawLevelLimit == 12 && EnhancementCatalog.Parse(coupon,
            "[equipment reinforcement ticket]\n11 100 `fixed` 12\n") is { Available: false, RawLevelLimit: 12 },
            "第四整数保留原值，固定模式非-1的未闭合限制不被忽略");
        string villageOnly = fixedText.Replace("`[village]` `[seria room]`", "`[village]`", StringComparison.Ordinal);
        string seriaOnly = fixedText.Replace("`[village]` `[seria room]`", "`[seria room]`", StringComparison.Ordinal);
        That(EnhancementCatalog.Parse(coupon, villageOnly) is { Available: true } &&
            EnhancementCatalog.Parse(coupon, seriaOnly) is { Available: false },
            "village和village加seria的既有Town入口保留，只有seria时Pending而不扩大地点资格");
        That(EnhancementCatalog.Parse(coupon, "[name]\n`[equipment amplify reinforcement ticket] 1 1`\n" + fixedText) is { Available: true },
            "名称文字中的伪标签不能覆盖真实券模式");
        foreach (string value in new[] { "0 100 `fixed` -1", "32 100 `fixed` -1", "11 101 `fixed` -1", "11 100 `random` -1", "11 100 `fixed` -2", "11 100 `fixed` -1 999" })
            That(EnhancementCatalog.Parse(coupon, "[equipment reinforcement ticket]\n" + value) is { Available: false }, "未知或溢出券参数不夹紧成成功");
        foreach (string suffix in new[] { "[enchant random]\n1\n", "[need material]\n10007110 5\n[/need material]\n",
            "[total usable count]\n1\n", "[reinforcement usable item list]\n123\n", "[equipment amplify reinforcement ticket]\n10 100 `fixed` -1\n" })
            That(EnhancementCatalog.Parse(coupon, fixedText + suffix) is { Available: false }, "额外未闭合资格、附加材料及混合模式不被忽略");
        That(EnhancementCatalog.Parse(coupon, "[lucky enchant deed]\n1\n") is { Available: false } &&
            EnhancementCatalog.Parse(coupon, "[stackable type]\n`[material]` 0\n") is null, "幸运未闭合和普通材料不能降级到券成功");
        var restricted = EnhancementCatalog.Parse(coupon, fixedText + $"[reinforcement usable item list]\n{gear}\n[/reinforcement usable item list]\n")!;
        That(restricted.AllowedItems.SetEquals([gear]), "显式目标装备清单按资源完整保留");
        var resources = new Dictionary<string, string>
        { ["stackable/coupon.stk"] = fixedText, ["equipment/gear.equ"] = "[impossible contents]\n`amplify upgrade`\n[/impossible contents]\n" };
        int reads = 0;
        var catalog = new EnhancementCatalog(path => { reads++; return resources[path]; },
            new Dictionary<int, string> { [coupon] = "coupon.stk" }, new Dictionary<int, string> { [gear] = "gear.equ" });
        That(catalog.Get(coupon)!.Available && catalog.Get(coupon)!.Available && catalog.Allows(gear, EnhancementMode.Reinforce) &&
            !catalog.Allows(gear, EnhancementMode.Amplify) && reads == 2, "券和目标延迟解析共享缓存，本版impossible contents限制按模式生效");

        var couponTemplate = new ItemTemplate(coupon, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Coupon");
        var gearTemplate = new ItemTemplate(gear, ItemKind.Equipment, 12, 10, 45, 1, 20, 1, 10, "[free]", ushort.MaxValue, 0, "Gear");
        var source = new InventoryItem(91, new(InventoryListType.Main, 65), new(coupon, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        var target = new InventoryItem(92, new(InventoryListType.Main, 9), new(gear, ItemKind.Equipment, 1234567)
        { Attr = 0xEE, Durability = 3, SealFlag = 1, GenuineUpgrade = 4, EnchantCardId = 3175, EnchantUpgrade = 2, Rune = 8,
            SortLockFlag = 1, Random0 = new(1, 2, 3), Extension114 = new(1, 333) });
        Character role = new(1, 0, "Enhance", 0, new(0, 0, 0, 0), null)
        { Level = 15, Inventory = new([source, target], Gold: 3456) { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 123) } };
        var use = new EnhancementRequest(EnhancementMode.Reinforce, target.Position, gear, source.Position.Index, -1, new byte[8], -1, -1);
        int calls = 0;
        InventoryResult Plan(Character owner, EnhancementRequest request, out EnhancementReceipt? receipt, EnhancementDefinition? definition = null, ItemTemplate? template = null) =>
            InventoryEnhancementService.Plan(owner, request, now, definition ?? fixedRule, couponTemplate, template ?? gearTemplate,
                () => { calls++; return 2999; }, out receipt);
        var applied = Plan(role, use, out var receipt);
        var after = applied.Plan!.After;
        That(applied.Accepted && receipt is { OldLevel: 14, NewLevel: 11, MaterialRemaining: 1, Succeeded: true } &&
            after.Find(target.Position) == target with { Core = target.Core with { Attr = 0xEB } } &&
            after.Find(source.Position) == source with { Core = source.Core with { Value = 1 } } &&
            after.Gold == role.Inventory.Gold && after.AccountWallet.Same(role.Inventory.AccountWallet),
            "只扣一券且覆盖Attr低五位，完整UID/强化高位/锻造/耐久/封装/附魔/随机字段与钱包保留");
        var failure = Plan(role, use, out receipt, fixedRule with { SuccessWeight = 2999 });
        That(failure.Accepted && receipt is { Succeeded: false, NewLevel: 14 } && failure.Plan!.After.Find(target.Position) == target &&
            failure.Plan.After.Find(source.Position)!.Core.Count == 1, "券概率边界失败保留完整目标，扣券与实际结果同计划");
        var amplified = role with { Inventory = new([source, target with { Core = target.Core with { Attr = 9, AmplifyType = 2, AmplifyValue = 21 } }]) };
        var ampRule = fixedRule with { Mode = EnhancementMode.Amplify, Level = 10 };
        var amp = Plan(amplified, use with { Mode = EnhancementMode.Amplify }, out receipt, ampRule);
        That(amp.Accepted && receipt!.Mode == EnhancementMode.Amplify && amp.Plan!.After.Find(target.Position)!.Core ==
            amplified.Inventory.Find(target.Position)!.Core with { Attr = 10 }, "增幅改变等级且保留基础异次元Type/Value，效果从完整核心和PVF派生");
        foreach (byte level in new byte[] { 1, 9, 10 })
        {
            var owner = amplified with { Inventory = new([source, amplified.Inventory.Find(target.Position)! with
                { Core = amplified.Inventory.Find(target.Position)!.Core with { Attr = level } }]) };
            var failed = Plan(owner, use with { Mode = EnhancementMode.Amplify }, out receipt, ampRule with { SuccessWeight = 0 });
            That(failed.Accepted && receipt!.NewLevel == level, "覆盖券失败保持原等级，不误用普通增幅高等级破坏规则");
        }
        int rolled = calls;
        foreach (var bad in new[] { use with { TargetItemId = gear + 1 }, use with { Target = source.Position }, use with { ProtectionSlot = 1 },
            use with { AuxiliarySlot0 = 1 }, use with { AuxiliarySlot1 = 1 }, use with { Mode = EnhancementMode.Amplify } })
            That(!Plan(role, bad, out _).Accepted, "错误目标、混合模式及未支持附加槽拒绝");
        foreach (var bad in new[] { role with { Job = 32 }, role with { Inventory = new([source with { Uid = 0 }, target]) },
            role with { Inventory = new([source with { Core = source.Core with { EquipmentLockId = 1 } }, target]) },
            role with { Inventory = new([source, target with { Core = target.Core with { EquipmentLockId = 1 } }]) },
            role with { Inventory = new([source with { Core = source.Core with { ExpireTime = (int)now } }, target]) } })
            That(!Plan(bad, use, out _).Accepted, "临时UID、安全锁及准确到期实例拒绝");
        That(!Plan(role, use, out _, restricted with { AllowedItems = [gear + 1] }).Accepted &&
            !Plan(role, use, out _, template: gearTemplate with { MinimumLevel = 96 }).Accepted &&
            !Plan(role, use, out _, template: gearTemplate with { EquipmentSlot = 13 }).Accepted && calls == rolled,
            "目标清单、等级及称号资格失败均在随机与消费前拒绝");
        var timedRole = role with { Inventory = new([source with { Core = source.Core with { ExpireTime = (int)now + 10 } }, target]) };
        var timed = Plan(timedRole, use, out _);
        That(timed.Accepted && timed.Plan!.CharacterPrecondition!.Matches(timedRole, now + 9) &&
            !timed.Plan.CharacterPrecondition.Matches(timedRole, now + 10), "有限券期限在最终提交前重验");
        var last = Plan(role with { Inventory = after }, use, out receipt);
        That(last.Accepted && receipt!.MaterialRemaining == 0 && last.Plan!.After.Find(source.Position) is null &&
            !Plan(role with { Inventory = last.Plan.After }, use, out _).Accepted, "最后一券清源后重复请求拒绝，固定券同等级仍真实扣券");
    }

    public static void RunResources(ItemResources resources, EnhancementCatalog enhancements)
    {
        var rule = enhancements.Get(490705402);
        That(rule is { Available: true, Mode: EnhancementMode.Reinforce, Level: 11, SuccessWeight: 10000, Additional: false,
            MinimumItemLevel: 1, MaximumItemLevel: 95 }, "真实新春+11券解析全部覆盖参数和1..95目标范围");
        var sword = resources.Items.Get(27308);
        That(sword.EquipmentSlot == 12 && sword.MinimumLevel == 10 && enhancements.Allows(sword.Id, EnhancementMode.Reinforce),
            "用户真实步兵巨剑按匹配资源取得模板资格");
        That(enhancements.Get(10007133) is { Available: false, PendingRule: not null },
            "真实+10增幅券额外need material未闭合，保留明确拒绝而非少扣材料成功");
    }

    internal static byte[] Request(byte mode, byte list, short slot, int item, short material, byte[] text)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(mode); writer.Write(list); writer.Write(slot); writer.Write(item); writer.Write(material); writer.Write((short)-1);
        writer.Write(text.Length); writer.Write(text); writer.Write((short)-1); writer.Write((short)-1);
        return stream.ToArray();
    }
}
