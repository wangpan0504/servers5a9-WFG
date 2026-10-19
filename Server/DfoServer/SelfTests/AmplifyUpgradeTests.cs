using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AmplifyUpgradeTests
{
    public static void RunCore()
    {
        int before = Count;
        string ResolvePortable(int index) => index switch { -1 => "[action type]", -2 => "[new portable amplify]",
            -3 => "[/action type]", -4 => "[portable amplify]", _ => throw new InvalidOperationException() };
        AmplifyUse? Portable(bool boosted, params int[] values) => AmplifyUpgradeCatalog.ParseUse(new(
            [new(3,-1), new(6,boosted ? -2 : -4), .. values.Select(value => new PvfLib.PvfToken(0,value)), new(3,-3)],
            ResolvePortable), ResolvePortable);
        That(Portable(true,0,14,30000) is { Kind:AmplifyUseKind.Portable, MinimumUpgrade:0, MaximumUpgrade:14, SuccessBonusWeight:30000 } &&
            Portable(false,0,50) is { Kind:AmplifyUseKind.Portable, MaximumUpgrade:50, SuccessBonusWeight:0 } &&
            Portable(true,0,14,0) is { SuccessBonusWeight:0 }, "新便携动作保留三个整数，旧动作及零加成不扩大基础成功率");
        foreach (int[] values in new int[][] { [], [0,14], [-1,14,30000], [14,0,30000], [0,14,-1], [0,14,30000,1] })
            Reject<InvalidDataException>(() => Portable(true,values), "新便携参数缺失、负值、倒置及多余参数拒绝");
        Reject<InvalidDataException>(() => Portable(false,0,14,30000), "旧便携动作不能借新动作长度获得加成");
        Reject<InvalidDataException>(() => AmplifyUpgradeCatalog.ParseUse(new(
            [new(3,-1),new(6,-2),new(0,0),new(0,14),new(2,BitConverter.SingleToInt32Bits(30000)),new(3,-3)],
            ResolvePortable), ResolvePortable), "新便携第三参数必须是整数权重，不把浮点原码当作加成");
        That(InventoryAmplifyUpgradeService.AdjustedFailureWeight(60000,30000) == 48000 &&
            InventoryAmplifyUpgradeService.AdjustedFailureWeight(66667,30000) == 56668,
            "基础40%乘1.3得52%，非整权重向下取整，不按增加30个百分点计算");
        That(InventoryAmplifyUpgradeService.AdjustedFailureWeight(10000,30000) == 0 &&
            InventoryAmplifyUpgradeService.AdjustedFailureWeight(100000,int.MaxValue) == 100000 &&
            InventoryAmplifyUpgradeService.AdjustedFailureWeight(0,int.MaxValue) == 0 &&
            InventoryAmplifyUpgradeService.AdjustedFailureWeight(60000,int.MaxValue) == 0,
            "成功权重封顶100%，基础零概率不变，宽整数计算不溢出");
        foreach (int failure in new[] { 0,1,40000,99999,100000 })
            That(InventoryAmplifyUpgradeService.AdjustedFailureWeight(failure,0) == failure, "无加成的普通及旧便携概率不变");
        foreach (var (failure,bonus) in new[] { (-1,0),(100001,0),(0,-1) })
            Reject<InvalidDataException>(() => InventoryAmplifyUpgradeService.AdjustedFailureWeight(failure,bonus));
        var parsed = EnhancementRequests.Read(EnhancementTests.Request(5, 0, 19, 31440, 84, new byte[17]));
        That(parsed.Mode == EnhancementMode.LuckyAmplify && parsed.MaterialSlot == 84, "幸运增幅沿原生mode5及同一20B固定骨架，不判成畸形");
        foreach (byte mode in new byte[] { 2, 3, 4, 6, 255 })
            Reject<InvalidDataException>(() => EnhancementRequests.Read(EnhancementTests.Request(mode, 0, 19, 31440, 84, [])));
        for (byte level = 0; level < 31; level++)
        {
            That(InventoryAmplifyUpgradeService.Outcome(level, true, 3, 0) == (0, level + 1), "成功只升一级且不改异次元基础值");
            That(InventoryAmplifyUpgradeService.Outcome(level, false, 1, 0) == (1, level), "普通低级失败保级回执");
        }
        var lucky = new LuckyAmplifyStep(9, 50, 10, 40);
        That(InventoryAmplifyUpgradeService.LuckyOutcome(lucky, 49999) == (2, 8) &&
            InventoryAmplifyUpgradeService.LuckyOutcome(lucky, 50000) == (1, 9) &&
            InventoryAmplifyUpgradeService.LuckyOutcome(lucky, 59999) == (1, 9) &&
            InventoryAmplifyUpgradeService.LuckyOutcome(lucky, 60000) == (0, 10), "幸运券三段闭开区间边界，不借用普通保护政策");
        var receipt = new EnhancementReceipt(EnhancementMode.Amplify, MainSlot(9), 121, 89, 10, 0, false)
        { OutcomeCode = 3, InPlace = true, Refunds = [new(358, 3037, 777), new(122, 3244, 2)] };
        var destroyed = EnhancementPackets.Ack(new(true, 0), receipt);
        That(destroyed.Length == 48 && destroyed[12] == 3 && destroyed[19] == 2 &&
            BitConverter.ToInt16(destroyed, 20) == 358 && BitConverter.ToInt32(destroyed, 22) == 3037 &&
            BitConverter.ToInt32(destroyed, 26) == 777 && BitConverter.ToInt16(destroyed, 40) == -1,
            "销毁回执在可选槽后插入count及10B补偿增量行，保留必读尾槽和双状态");
        var protectedAck = EnhancementPackets.Ack(new(true, 0), receipt with { OutcomeCode = 2, Refunds = [], TerminalSlot = 80, TerminalRemaining = 4 });
        That(protectedAck.Length == 27 && protectedAck[12] == 2 && BitConverter.ToInt16(protectedAck, 19) == 80 &&
            BitConverter.ToInt32(protectedAck, 21) == 4, "保护券使用独立末端槽与绝对余量，不混入每次都消费的附加券槽");
        var luckyAck = EnhancementPackets.Ack(new(true, 0), receipt with { OutcomeCode = 2, Path = 4, NewLevel = 9, Refunds = [] });
        That(luckyAck[1] == 1 && luckyAck[10] == 4 && luckyAck[13] == 9, "幸运请求5回包还原增幅模式1与幸运路径4，允许原生降级消费者");
        Console.WriteLine($"PASS {Count - before} 普通增幅与幸运券纯协议、概率区间及销毁回执断言");
    }

    public static void RunResources(TcpTestResources resources)
    {
        EnhancementTests.RunResources(resources.ItemResources, resources.ItemResources.Enhancements);
        int before = Count;
        var r = resources.ItemResources;
        var rules = r.AmplifyUpgrades.Rules;
        That(rules.Steps.Length == 255 && rules.Steps[0] is { FailureWeight: 0, MaterialId: 3242, MaterialCount: 1 } &&
            rules.Steps[10] is { FailureWeight: 60000, PenaltyType: 3, MaterialCount: 11 }, "当前PVF普通amplify表按24列读取，不误选new amplify表");
        That(r.AmplifyUpgrades.Lucky.Length == 15 && r.AmplifyUpgrades.Lucky[14] == new LuckyAmplifyStep(14, 82, 0, 18),
            "当前PVF独立幸运增幅概率覆盖+0至+14，不使用活动旧fortune表");
        That(r.AmplifyUpgrades.Get(10005313) is { Kind: AmplifyUseKind.Portable, MinimumUpgrade: 0, MaximumUpgrade: 50, MaximumItemLevel: 85 } &&
            r.AmplifyUpgrades.Get(10007839) is { Kind: AmplifyUseKind.Portable, MinimumUpgrade: 0, MaximumUpgrade: 14,
                MaximumItemLevel:85, SuccessBonusWeight:30000, PendingRule:null } &&
            r.AmplifyUpgrades.Get(10007407) is { Kind: AmplifyUseKind.Lucky, SlotClass: 0, SealedOnly: false } &&
            r.AmplifyUpgrades.Get(3242) is null, "真实一次性增幅器、幸运券与普通材料按资源分流");
        That(r.AmplifyUpgrades.Get(10007370) is { Kind: AmplifyUseKind.Protection, RetainLevel: 0, PendingRule: null } &&
            r.AmplifyUpgrades.Get(10008386) is { Kind: AmplifyUseKind.Protection, PendingRule: null } &&
            r.Items.Get(10008386).ShopPrice.Materials is [{ ItemId: 10008362, Count: 3 }],
            "真实保护券按action识别，兑换价格不阻止保护生效也不作为使用费用");
        var template = r.Items.Get(31440);
        That(rules.Gold(template with { Grade = 1, Rarity = 3, EquipmentSlot = 12 }) == 3000 &&
            rules.Gold(template with { Grade = 1, Rarity = 3, EquipmentSlot = 25 }) == 1050, "金币按品级、部位和稀有度逐次取整，耳环使用第12项");
        var core = InventoryRewardGrantService.Create(template) with { Attr = 0xE0, AmplifyType = 3, AmplifyValue = 6,
            EnchantCardId = 3175, EnchantUpgrade = 2, GenuineUpgrade = 4, Rune = 8, Random0 = new(1, 2, 3), Extension114 = new(1, 333) };
        var source = new InventoryItem(2, MainSlot(121), InventoryRewardGrantService.Create(r.Items.Get(3242), 1000));
        var target = new InventoryItem(1, MainSlot(9), core);
        Character role = new(1, 10000, "AmplifyPlan", 0, resources.Towns.Room, null)
        { Job = 2, Level = 75, Inventory = new([target, source], Gold: 100_000_000) };
        var request = new EnhancementRequest(EnhancementMode.Amplify, MainSlot(9), 31440, 121, -1, ReadOnlyMemory<byte>.Empty, -1, -1);
        var rng = new FixedRandom(99999);
        var service = new InventoryAmplifyUpgradeService(r.Items, r.AmplifyUpgrades, r.Enhancements, r.Disjoint, r.GoldLimits, r.EpicPieces, resources.Towns, rng);
        var result = service.Plan(role, request, out var receipt);
        That(result.Accepted && receipt is { OldLevel: 0, NewLevel: 1, MaterialRemaining: 999, OutcomeCode: 0 } &&
            result.Plan!.After.Find(MainSlot(9)) == target with { Core = core with { Attr = 0xE1 } } &&
            result.Plan.After.Gold == role.Inventory.Gold - rules.Gold(template), "真实普通+1扣一矛盾及实际金币，完整核心与UID保留");
        for (byte level = 4; level <= 9; level++)
        {
            rng.Value = 0;
            var current = role with { Inventory = role.Inventory with { Items = [target with { Core = core with { Attr = (byte)(0xE0 | level) } }, source] } };
            result = service.Plan(current, request, out receipt);
            That(result.Accepted && receipt is { OutcomeCode: 1 } && receipt.NewLevel == level &&
                result.Plan!.After.Find(MainSlot(9)) == current.Inventory.Find(MainSlot(9)) &&
                result.Plan.After.Find(MainSlot(121))!.Core.Count == 1000 - rules.Steps[level].MaterialCount,
                "普通+4至+9失败仍保级但正常扣材料与金币，不误用参考端降一级");
        }
        rng.Value = 99999;
        int calls = rng.Calls;
        foreach (var invalid in new[] { role with { Inventory = role.Inventory with { Gold = 0 } },
            role with { Inventory = new([target with { Core = core with { AmplifyType = 0x83 } }, source], Gold: role.Inventory.Gold) },
            role with { Inventory = new([target with { Core = core with { Durability = 1 } }, source], Gold: role.Inventory.Gold) },
            role with { Inventory = new([target with { Core = core with { EquipmentLockId = 1 } }, source], Gold: role.Inventory.Gold) },
            role with { Inventory = new([target, source with { Core = source.Core with { Value = 1 } }], Gold: 0) } })
            That(!service.Plan(invalid, request, out _).Accepted && rng.Calls == calls, "资格、余额、满耐久与未净化在随机前拒绝");

        foreach (int id in new[] { 10005313, 10007407, 10007839 })
        {
            var consumable = source with { Position = MainSlot(65), Core = InventoryRewardGrantService.Create(r.Items.Get(id), 2) with { ExpireTime = 0 } };
            var current = role with { Level = 1, Job = 15, Inventory = new([target, consumable], Gold: 0) };
            var use = request with { MaterialSlot = 65, Mode = id == 10007407 ? EnhancementMode.LuckyAmplify : EnhancementMode.Amplify };
            result = service.Plan(current, use, out receipt);
            That(result.Accepted && receipt is { GoldCost: 0, MaterialRemaining: 1, NewLevel: 1 } && result.Plan!.After.Gold == 0,
                "便携及幸运券各扣一件，不收金币或矛盾且不检查源消耗品角色等级");
            if (id == 10007407)
            {
                rng.Value = 0;
                current = current with { Inventory = current.Inventory with { Items = [target with { Core = core with { Attr = 0xE9 } }, consumable] } };
                result = service.Plan(current, use, out receipt);
                That(result.Accepted && receipt is { OutcomeCode: 2, Path: 4, NewLevel: 8 }, "幸运+9失败按独立表降到+8，不借用普通保级");
                current = current with { Inventory = current.Inventory with { Items = [target with { Core = core with { Attr = 0xEF } }, consumable] } };
                That(!service.Plan(current, use, out _).Accepted, "幸运+15拒绝，不溢出配置或破坏装备");
            }
        }
        var boostedSource = source with { Position = MainSlot(65),
            Core = InventoryRewardGrantService.Create(r.Items.Get(10007839),2) with { ExpireTime = 0 } };
        var boostGuard = new InventoryItem(3, MainSlot(122), InventoryRewardGrantService.Create(r.Items.Get(10007370),2) with { ExpireTime = 0 });
        var boostedRole = role with { Inventory = new([target with { Core = core with { Attr = 0xEA } }, boostedSource, boostGuard], Gold:0) };
        foreach (int roll in new[] { 47999,48000 })
        {
            rng.Value = roll;
            result = service.Plan(boostedRole,request with { MaterialSlot = 65 },out receipt);
            bool success = roll == 48000;
            That(result.Accepted && receipt is { GoldCost:0, MaterialRemaining:1, Refunds.IsEmpty:true } &&
                receipt.OutcomeCode == (success ? 0 : 2) && receipt.NewLevel == (success ? 11 : 0) &&
                receipt.TerminalSlot == (success ? -1 : 122) &&
                result.Plan!.After.Find(MainSlot(9)) == target with { Core = core with { Attr = success ? (byte)0xEB : (byte)0xE0 } } &&
                result.Plan.After.Find(MainSlot(122))!.Core.Count == (success ? 2 : 1) && result.Plan.After.Gold == 0,
                "真实成功之兆+10边界47999失败、48000成功，源扣一件、失败才扣保护券，完整UID及核心保留");
        }
        rng.Value = 99999;
        var edge = boostedRole with { Inventory = boostedRole.Inventory with { Items =
            [target with { Core = core with { Attr = 0xEE } },boostedSource,boostGuard] } };
        That(service.Plan(edge,request with { MaterialSlot = 65 },out receipt).Accepted && receipt is { OldLevel:14,NewLevel:15 },
            "成功之兆允许当前+14尝试到+15，区间不是产出等级上限");
        calls = rng.Calls;
        edge = edge with { Inventory = edge.Inventory with { Items = [target with { Core = core with { Attr = 0xEF } },boostedSource] } };
        That(!service.Plan(edge,request with { MaterialSlot = 65 },out _).Accepted && rng.Calls == calls,
            "当前+15超过成功之兆的使用上限，在随机前拒绝且不扣源");
        rng.Value = 0;
        var high = role with { Inventory = role.Inventory with { Items = [target with { Core = core with { Attr = 0xEB } }, source],
            AccountWallet = role.Inventory.AccountWallet.Set(CurrencyKind.ClearCube, 5000) } };
        result = service.Plan(high, request, out receipt);
        That(result.Accepted && receipt is { OutcomeCode: 3, NewLevel: 0 } && result.Plan!.After.Find(MainSlot(9)) is null &&
            result.Plan.After.Items.All(item => item.Uid != target.Uid) && receipt.Refunds.Any(row => row.ItemId == 3244 && row.Count > 0),
            "高等级普通失败销毁原目标并原子发放分解及增幅补偿，不截断为保级或遗漏红字精髓");
        That(receipt!.Refunds.Single(row => row.ItemId == 3037).Count ==
            CurrencyWallet.Get(result.Plan!.After, CurrencyKind.ClearCube) - 5000, "销毁回执只加本次晶块补偿，不能把已有5000余额再次加给客户端");
        var full = high with { Inventory = high.Inventory with { Items = [.. high.Inventory.Items,
            .. Enumerable.Range(122, ItemSlotBoundService.Range(ItemKind.Material, high.Inventory).End - 121)
                .Select(slot => source with { Uid = 1000 + slot, Position = MainSlot((short)slot) })] } };
        That(!service.Plan(full, request, out _).Accepted && full.Inventory.Find(MainSlot(9))!.Core.Upgrade == 11 &&
            full.Inventory.Gold == high.Inventory.Gold, "高等级尝试先预检完整补偿容量；满材料包不允许靠拒绝失败免费重抽");
        string GuaranteedResolve(int index) => index switch { -1 => "[action type]", -2 => "[new portable amplify]", -3 => "[/action type]",
            _ => resources.Archive.ResolveString(index) };
        var guaranteed = new PvfLib.PvfTokenDocument([new(3,-1),new(6,-2),new(0,0),new(0,14),new(0,int.MaxValue),new(3,-3)],GuaranteedResolve);
        var guaranteedCatalog = new AmplifyUpgradeCatalog(path => path == "stackable/guaranteed.stk" ? guaranteed :
            new(resources.Archive.ReadTokens(path),GuaranteedResolve),GuaranteedResolve,new Dictionary<int,string> { [10007839] = "guaranteed.stk" });
        var guaranteedService = new InventoryAmplifyUpgradeService(r.Items,guaranteedCatalog,r.Enhancements,r.Disjoint,r.GoldLimits,r.EpicPieces,resources.Towns,rng);
        var guaranteedRole = full with { Inventory = full.Inventory with { Gold = 0, Items = full.Inventory.Items.Add(boostedSource with { Uid = 90 }) } };
        calls = rng.Calls;
        result = guaranteedService.Plan(guaranteedRole,request with { MaterialSlot = 65 },out receipt);
        That(result.Accepted && receipt is { OutcomeCode:0, NewLevel:12, Refunds.IsEmpty:true } && rng.Calls == calls+1 &&
            result.Plan!.After.Find(MainSlot(9))!.Uid == target.Uid && result.Plan.After.Find(MainSlot(65))!.Core.Count == 1 &&
            full.Inventory.Items.Where(item => item.Position != target.Position).All(item => result.Plan.After.Find(item.Position) == item),
            "合成高加成封顶100%时不预留不可能产生的毁装补偿，不因材料满包拒绝，不多抽随机或额外扣费");
        var actualProtection = high with { Inventory = high.Inventory with { Items = [.. high.Inventory.Items,
            new(3, MainSlot(122), InventoryRewardGrantService.Create(r.Items.Get(10007370), 2))] } };
        result = service.Plan(actualProtection, request, out receipt);
        That(result.Accepted && receipt is { OutcomeCode: 2, NewLevel: 0, TerminalSlot: 122, TerminalRemaining: 1, Refunds.IsEmpty: true } &&
            result.Plan!.After.Find(MainSlot(9))!.Core == core && result.Plan.After.Find(MainSlot(9))!.Uid == target.Uid,
            "当前PVF神圣增幅保护券在高等级失败时扣一张、归零但不销毁，完整核心与UID保留");
        var exchangeProtection = high with { Inventory = high.Inventory with { Items = [.. high.Inventory.Items,
            new(3, MainSlot(122), InventoryRewardGrantService.Create(r.Items.Get(10008386), 2) with { ExpireTime = 0 })] } };
        result = service.Plan(exchangeProtection, request, out receipt);
        That(result.Accepted && receipt is { OutcomeCode: 2, NewLevel: 0, TerminalSlot: 122, TerminalRemaining: 1 } &&
            result.Plan!.After.Find(MainSlot(9))!.Uid == target.Uid,
            "商店兑换的保护券无需持有兑换代币也能保护装备，只消费保护券本身");
        string Resolve(int index) => index switch { -1 => "[action type]", -2 => "[new amplify protect equipment]", -3 => "[/action type]",
            _ => resources.Archive.ResolveString(index) };
        var protective = new PvfLib.PvfTokenDocument(new PvfLib.PvfToken[] { new(3, -1), new(6, -2), new(0, 7), new(3, -3) }, Resolve);
        var testCatalog = new AmplifyUpgradeCatalog(path => path == "stackable/protection.stk" ? protective :
            new(resources.Archive.ReadTokens(path), Resolve), Resolve, new Dictionary<int, string> { [10005313] = "protection.stk" });
        var withProtection = high with { Inventory = high.Inventory with { Items = [.. high.Inventory.Items,
            new(3, MainSlot(65), InventoryRewardGrantService.Create(r.Items.Get(10005313), 2) with { ExpireTime = 0 })] } };
        var protectedService = new InventoryAmplifyUpgradeService(r.Items, testCatalog, r.Enhancements, r.Disjoint, r.GoldLimits, r.EpicPieces, resources.Towns, rng);
        result = protectedService.Plan(withProtection, request, out receipt);
        That(result.Accepted && receipt is { OutcomeCode: 2, NewLevel: 7, TerminalSlot: 65, TerminalRemaining: 1, Refunds.IsEmpty: true } &&
            result.Plan!.After.Find(MainSlot(9))!.Core == core with { Attr = 0xE7 } && result.Plan.After.Find(MainSlot(9))!.Uid == 1,
            "合成保护声明在毁装失败时消耗一张、保留配置等级与原UID，不发销毁补偿；不是当前PVF实机资格");
        rng.Value = 99999;
        result = protectedService.Plan(withProtection, request, out receipt);
        That(result.Accepted && receipt is { OutcomeCode: 0, NewLevel: 12, TerminalSlot: -1 } &&
            result.Plan!.After.Find(MainSlot(65))!.Core.Count == 2, "成功时不扣自动保护券，也不报告已使用");
        string PendingResolve(int index) => index == -4 ? "[total usable count]" : Resolve(index);
        var pending = new PvfLib.PvfTokenDocument([new(3,-1), new(6,-2), new(0,7), new(3,-3), new(3,-4), new(0,1)], PendingResolve);
        That(AmplifyUpgradeCatalog.ParseUse(pending, PendingResolve) is { PendingRule: "total usable count" },
            "取消兑换价格门禁不扩大为忽略其他未实现的使用次数限制");
        var pendingCatalog = new AmplifyUpgradeCatalog(path => path == "stackable/protection.stk" ? pending :
            new(resources.Archive.ReadTokens(path), PendingResolve), PendingResolve, new Dictionary<int, string> { [10005313] = "protection.stk" });
        var pendingService = new InventoryAmplifyUpgradeService(r.Items, pendingCatalog, r.Enhancements, r.Disjoint, r.GoldLimits, r.EpicPieces, resources.Towns, rng);
        calls = rng.Calls;
        That(!pendingService.Plan(withProtection, request, out _).Accepted && rng.Calls == calls,
            "存在未实现使用资格的保护券时仍在随机前拒绝，不能静默跳过保护后销毁装备");
        Console.WriteLine($"PASS {Count - before} 当前PVF普通增幅、便携、幸运、金币及完整销毁规划断言");
    }
    static InventorySlot MainSlot(short slot) => new(InventoryListType.Main, slot);
    internal sealed class FixedRandom(int value) : Random
    {
        public int Value { get; set; } = value;
        public int Calls { get; private set; }
        public override int Next(int maxValue) { Calls++; return Math.Min(Value, maxValue - 1); }
        public override int Next(int minValue, int maxValue) { Calls++; return minValue + Math.Min(Value, maxValue - minValue - 1); }
    }
}
