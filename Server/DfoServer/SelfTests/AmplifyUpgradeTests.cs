using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json;
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
        AmplifyUse? Reinforcement(string label, params int[] values)
        {
            string Resolve(int index) => index switch { -1 => "[action type]", -2 => label, -3 => "[/action type]",
                _ => throw new InvalidOperationException() };
            return AmplifyUpgradeCatalog.ParseUse(new([new(3,-1),new(6,-2),
                ..values.Select(value => new PvfLib.PvfToken(0,value)),new(3,-3)], Resolve), Resolve);
        }
        That(Reinforcement("[portable upgrade]",0,30) is { Kind:AmplifyUseKind.Portable, Reinforce:true, MaximumUpgrade:30 } &&
            Reinforcement("[new portable upgrade]",0,14,30000) is { Kind:AmplifyUseKind.Portable, Reinforce:true, SuccessBonusWeight:30000 } &&
            Reinforcement("[protect equipment]") is { Kind:AmplifyUseKind.Protection, Reinforce:true, RetainLevel:0 } &&
            Reinforcement("[new protect equipment]",10) is { Kind:AmplifyUseKind.Protection, Reinforce:true, RetainLevel:10 },
            "便携强化及保护声明保留独立强化模式，不能套用增幅概率或消费增幅保护券");
        string serverField = "action usable server type";
        string ResolvePortable(int index) => index switch { -1 => "[action type]", -2 => "[new portable amplify]",
            -3 => "[/action type]", -4 => "[portable amplify]", -5 => "[" + serverField + "]",
            -6 => "[/" + serverField + "]", -7 => "[general server]", -8 => "[event server]",
            -9 => "[unknown server]", -10 => "[extra]", -11 => "[/extra]", _ => throw new InvalidOperationException() };
        AmplifyUse? Portable(bool boosted, params int[] values) => AmplifyUpgradeCatalog.ParseUse(new(
            [new(3,-1), new(6,boosted ? -2 : -4), .. values.Select(value => new PvfLib.PvfToken(0,value)), new(3,-3)],
            ResolvePortable), ResolvePortable);
        AmplifyUse? ServerUse(params PvfLib.PvfToken[] suffix) => AmplifyUpgradeCatalog.ParseUse(new(
            [new(3,-1),new(6,-4),new(0,0),new(0,30),new(3,-3),..suffix], ResolvePortable), ResolvePortable);
        foreach (string field in new[] { "action usable server type", "usable game server type", "usable server type" })
        {
            serverField = field;
            That(ServerUse(new(3,-5),new(6,-7),new(3,-6)) is { PendingRule:null, MaximumUpgrade:30 } &&
                ServerUse(new(3,-5),new(6,-7),new(6,-8),new(3,-6)) is { PendingRule:null },
                "服务器声明不改变增幅器的原升级范围：" + field);
            foreach (var suffix in new PvfLib.PvfToken[][]
            {
                [new(3,-5),new(6,-8),new(3,-6)], [new(3,-5),new(6,-9),new(3,-6)],
                [new(3,-5),new(6,-7),new(6,-7),new(3,-6)], [new(3,-5),new(3,-6)],
                [new(3,-5),new(0,0),new(3,-6)], [new(3,-5),new(6,-7)],
                [new(3,-5),new(6,-7),new(3,-6),new(3,-5),new(6,-7),new(3,-6)],
                [new(3,-10),new(3,-5),new(6,-7),new(3,-6),new(3,-11)]
            }) That(ServerUse(suffix) is { PendingRule:null, MinimumUpgrade:0, MaximumUpgrade:30, SuccessBonusWeight:0 },
                "忽略专用服、未知、重复、空值及其他服务器声明形态，不提高等级范围或成功率：" + field);
        }
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
        foreach (byte mode in new byte[] { 3, 4, 6, 255 })
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
        EquipmentUpgradeBonusTests.RunResources(resources);
        int before = Count;
        var r = resources.ItemResources;
        foreach (int id in new[] { 490011012,490011266,490011334,490011881,490012050,490012067,490012459,
            490012568,490012611,490012697,490013119,490013124,490013325 })
            That(r.AmplifyUpgrades.Get(id) is { Kind:AmplifyUseKind.Portable, PendingRule:null },
                "全量普查命中的普通服一次性增幅器均可按原限制消费：" + id);
        var rules = r.AmplifyUpgrades.Rules;
        That(rules.NoticeLevel == 12 && r.AmplifyUpgrades.ReinforceRules.NoticeLevel == 12,
            "强化与增幅分别保留各自PVF的notice门槛");
        That(rules.Steps.Length == 255 && rules.Steps[0] is { FailureWeight: 0, MaterialId: 3242, MaterialCount: 1 } &&
            rules.Steps[10] is { FailureWeight: 60000, PenaltyType: 3, MaterialCount: 11 }, "当前PVF普通amplify表按24列读取，不误选new amplify表");
        That(r.AmplifyUpgrades.Lucky.Length == 15 && r.AmplifyUpgrades.Lucky[14] == new LuckyAmplifyStep(14, 82, 0, 18),
            "当前PVF独立幸运增幅概率覆盖+0至+14，不使用活动旧fortune表");
        That(r.AmplifyUpgrades.LuckyReinforce.Length == 15 && r.AmplifyUpgrades.LuckyReinforce[14] == new LuckyAmplifyStep(14, 85, 0, 15) &&
            r.AmplifyUpgrades.Get(10007406) is { Kind: AmplifyUseKind.Lucky, Reinforce: true },
            "幸运强化使用strengthening独立概率，+14成功率15%而非幸运增幅的18%");
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
        var service = new InventoryAmplifyUpgradeService(r.Items, r.AmplifyUpgrades, r.Enhancements, r.Disjoint, r.GoldLimits, r.EpicPieces, rng);
        var luckySource = new InventoryItem(3, MainSlot(65), InventoryRewardGrantService.Create(r.Items.Get(10007406), 2) with { ExpireTime = 0 });
        var reinforcement = target with { Core = core with { Attr = 0xEE, AmplifyType = 0, AmplifyValue = 0 } };
        var luckyRole = role with { Inventory = new([reinforcement, luckySource], Gold: 0) };
        var luckyRequest = request with { Mode = EnhancementMode.Reinforce, MaterialSlot = 65 };
        foreach (var (roll, level, code) in new[] { (84999, 13, 2), (85000, 15, 0) })
        {
            rng.Value = roll;
            var luckyResult = service.Plan(luckyRole, luckyRequest, out var luckyReceipt);
            That(luckyResult.Accepted && luckyReceipt is { Mode: EnhancementMode.Reinforce, Path: 4, MaterialRemaining: 1, GoldCost: 0, TerminalSlot: -1 } &&
                luckyReceipt.NewLevel == level && luckyReceipt.OutcomeCode == code &&
                luckyResult.Plan!.After.Find(target.Position) == reinforcement with { Core = reinforcement.Core with { Attr = (byte)(0xE0 | level) } },
                "幸运强化+14按独立概率降一级或升一级，不扣金币或任何保护券，完整UID和核心保留");
        }
        That(!service.Plan(luckyRole, luckyRequest with { Mode = EnhancementMode.LuckyAmplify }, out _).Accepted &&
            !service.Plan(luckyRole with { Inventory = luckyRole.Inventory with { Items = [reinforcement with { Core = reinforcement.Core with { Attr = 0xEF } }, luckySource] } },
                luckyRequest, out _).Accepted, "幸运强化不接受增幅模式或当前+15目标");
        rng.Value = 99999;
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
        var restrictedHigh = high with { Inventory = high.Inventory with { Items = high.Inventory.Items.Select(item =>
            item.Uid == target.Uid ? item with { Core = item.Core with { DisjointForbidden = true, DestructionSoulForbidden = true } } : item).ToImmutableArray() } };
        var restrictedFailure = service.Plan(restrictedHigh, request, out var restrictedReceipt);
        That(restrictedFailure.Accepted && restrictedReceipt is { OutcomeCode: 3 } &&
            receipt.Refunds.Any(row => row.ItemId == r.Disjoint.Souls[template.Rarity]?.ItemId) &&
            restrictedReceipt.Refunds.SequenceEqual(receipt.Refunds.Where(row => row.ItemId != r.Disjoint.Souls[template.Rarity]?.ItemId)),
            "受限实例仍可正常增幅毁装，只去掉实际灵魂返还，晶体和红字精髓保持");
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
        var guaranteedService = new InventoryAmplifyUpgradeService(r.Items,guaranteedCatalog,r.Enhancements,r.Disjoint,r.GoldLimits,r.EpicPieces,rng);
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
        var protectedService = new InventoryAmplifyUpgradeService(r.Items, testCatalog, r.Enhancements, r.Disjoint, r.GoldLimits, r.EpicPieces, rng);
        result = protectedService.Plan(withProtection, request, out receipt);
        That(result.Accepted && receipt is { OutcomeCode: 2, NewLevel: 7, TerminalSlot: 65, TerminalRemaining: 1, Refunds.IsEmpty: true } &&
            result.Plan!.After.Find(MainSlot(9))!.Core == core with { Attr = 0xE7 } && result.Plan.After.Find(MainSlot(9))!.Uid == 1,
            "合成保护声明在毁装失败时消耗一张、保留配置等级与原UID，不发销毁补偿；不是当前PVF实机资格");
        result = protectedService.Plan(withProtection, request, out receipt, canUseSource: _ => false);
        That(result.Accepted && receipt is { OutcomeCode: 3, TerminalSlot: -1 } &&
            result.Plan!.After.Find(MainSlot(65))!.Core.Count == 2,
            "自动保护券也通过统一使用资格筛选，场景禁止时不扣券、不冒充已保护");
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
        var pendingService = new InventoryAmplifyUpgradeService(r.Items, pendingCatalog, r.Enhancements, r.Disjoint, r.GoldLimits, r.EpicPieces, rng);
        calls = rng.Calls;
        That(!pendingService.Plan(withProtection, request, out _).Accepted && rng.Calls == calls,
            "存在未实现使用资格的保护券时仍在随机前拒绝，不能静默跳过保护后销毁装备");
        Exact433ProtectionCommit(resources, role, service, rng, request);
        PotionBoundaries(resources, role, request);
        PortableReinforcement(resources, role, request);
        NpcReinforcement(resources, role, request);
        Console.WriteLine($"PASS {Count - before} 当前PVF普通增幅、便携、幸运、金币及完整销毁规划断言");
    }

    static void PortableReinforcement(TcpTestResources resources, Character seed, EnhancementRequest request)
    {
        var r = resources.ItemResources;
        foreach (int id in new[] { 2682839,2682840,10002488,10003674,10005169,10005312,10005885,10007838,10090037,490005583,490702163 })
            That(r.AmplifyUpgrades.Get(id) is { Kind:AmplifyUseKind.Portable, Reinforce:true, PendingRule:null },
                "全量核查各参数形状的便携强化器按自身资格接入：" + id);
        That(r.AmplifyUpgrades.Get(7804) is { Kind:AmplifyUseKind.Protection, Reinforce:true, RetainLevel:0 } &&
            r.AmplifyUpgrades.Get(10007840) is { Kind:AmplifyUseKind.Protection, Reinforce:true, RetainLevel:10 },
            "旧强化保护券归零，新强化保护券保留+10，不与增幅保护混用");
        var rules = r.AmplifyUpgrades.ReinforceRules;
        That(rules.Steps.Length == 255 && rules.Steps[0] is { FailureWeight:0, MaterialId:3037, MaterialCount:10 } &&
            rules.Steps[10] is { FailureWeight:75000, PenaltyType:2, PenaltyValue:3 } &&
            rules.Steps[12] is { FailureWeight:86000, PenaltyType:3 },
            "强化独立normal表的保级、降三级及销毁政策，不混入amplify或活动表");
        var random = new FixedRandom(0);
        var upgrading = new InventoryAmplifyUpgradeService(r.Items,r.AmplifyUpgrades,r.Enhancements,r.Disjoint,
            r.GoldLimits,r.EpicPieces,random,consumables:r.RecoveryConsumables);
        var service = new InventoryEnhancementService(r.Items,r.Enhancements,r.MedalUpgrades,random,amplification:upgrading);
        var target = seed.Inventory.Find(request.Target)!;
        var source = new InventoryItem(3,MainSlot(65),InventoryRewardGrantService.Create(r.Items.Get(10005312),2) with { ExpireTime=0 });
        var wrongGuard = new InventoryItem(4,MainSlot(122),InventoryRewardGrantService.Create(r.Items.Get(10007370),2) with { ExpireTime=0 });
        var use = request with { Mode=EnhancementMode.Reinforce, MaterialSlot=65 };
        Character Actor(byte level, int sourceId=10005312, int guardId=7804) => seed with { Inventory=new([
            target with { Core=target.Core with { Attr=(byte)(0xE0|level), AmplifyType=0, AmplifyValue=0 } },
            source with { Core=InventoryRewardGrantService.Create(r.Items.Get(sourceId),2) with { ExpireTime=0 } },
            wrongGuard, new(5,MainSlot(123),InventoryRewardGrantService.Create(r.Items.Get(guardId),2) with { ExpireTime=0 })],Gold:0) };
        foreach (var (level,roll,expected,code,guard) in new[] {
            (0,0,1,0,7804),(9,69999,9,1,7804),(9,70000,10,0,7804),
            (10,74999,7,2,7804),(10,75000,11,0,7804),(12,0,0,2,7804),(12,0,10,2,10007840),(12,86000,13,0,10007840) })
        {
            random.Value=roll;
            var actor=Actor((byte)level,guardId:guard);
            var result=service.Plan(actor,use,out var receipt);
            bool protectedFailure=level==12 && code==2;
            var original=actor.Inventory.Find(use.Target)!;
            That(result.Accepted && receipt is { Mode:EnhancementMode.Reinforce,Path:0,GoldCost:0,MaterialRemaining:1,Refunds.IsEmpty:true } &&
                receipt.NewLevel==expected && receipt.OutcomeCode==code && receipt.TerminalSlot==(protectedFailure?123:-1) &&
                result.Plan!.After.Find(use.Target)==original with { Core=original.Core with { Attr=(byte)(0xE0|expected) } } &&
                result.Plan.After.Find(MainSlot(122))==wrongGuard && result.Plan.After.Find(MainSlot(123))!.Core.Count==(protectedFailure?1:2) &&
                result.Plan.After.Gold==0,"便携强化按普通表边界结算，只扣源一件；毁装失败才消费同类保护，完整实例保持");
        }
        foreach (var (roll,expected) in new[] { (60999,9),(61000,10) })
        {
            random.Value=roll;
            var result=service.Plan(Actor(9,10007838),use,out var receipt);
            That(result.Accepted && receipt!.NewLevel==expected && receipt.GoldCost==0 && receipt.MaterialRemaining==1,
                "新便携强化器相对成功权重1.3倍，+9边界61000；不能使用增幅+9概率");
        }
        var unprotected=Actor(12);
        unprotected=unprotected with { Inventory=unprotected.Inventory with { Items=unprotected.Inventory.Items.RemoveAt(3) } };
        random.Value=0;
        var destroyed=service.Plan(unprotected,use,out var destruction);
        That(destroyed.Accepted && destruction is { OutcomeCode:3,TerminalSlot:-1 } &&
            destroyed.Plan!.After.Find(use.Target) is null && destroyed.Plan.After.Find(MainSlot(122))==wrongGuard &&
            destroyed.Plan.After.Items.All(item=>item.Uid!=target.Uid),
            "只有增幅保护券时不错误扣券，强化销毁按原事务规划完整补偿，不发保级成功");
        int calls=random.Calls;
        foreach (var invalid in new[] { Actor(31),Actor(10,2682839),Actor(0,2682840) })
            That(!service.Plan(invalid,use,out _).Accepted && random.Calls==calls,
                "Attr五位上限和便携器声明的当前等级区间在抽签前拒绝");
        That(!service.Plan(Actor(0),use with { Mode=EnhancementMode.Amplify },out _).Accepted &&
            !service.Plan(seed with { Inventory=Actor(0).Inventory with { Items=Actor(0).Inventory.Items.SetItem(0,target) } },use,out _).Accepted,
            "强化器不能用增幅请求或异次元装备，保留模式与目标资格");
    }

    static void NpcReinforcement(TcpTestResources resources, Character seed, EnhancementRequest request)
    {
        var r = resources.ItemResources;
        var rules = r.AmplifyUpgrades.ReinforceRules;
        var priced = r.Items.Get(31440) with { Grade = 1, Rarity = 3, EquipmentSlot = 12 };
        That(rules.LevelCostWeights.Length == 19 && rules.LevelCostWeights[0] == 0.5f &&
            rules.LevelCostWeights[4] == 1f && rules.LevelCostWeights[11] == 2.5f && rules.LevelCostWeights[18] == 16f &&
            r.AmplifyUpgrades.Rules.LevelCostWeights.IsDefaultOrEmpty,
            "强化表保留0..18等级费用倍率且a=b；增幅表没有该段");
        That(rules.Gold(priced, 0) == 360 && rules.Gold(priced, 4) == 720 && rules.Gold(priced, 11) == 1800 &&
            rules.Gold(priced, 18) == 11520 && rules.Gold(priced, 30) == 11520 &&
            rules.Gold(priced with { EquipmentSlot = 25 }, 0) == 300,
            "NPC强化金币三段各自+0.5截断，超表等级沿用末档，耳环用第12项");
        That(r.AmplifyUpgrades.Rules.Gold(priced) == 3000 && r.AmplifyUpgrades.Rules.Gold(priced, 11) == 3000,
            "增幅金币仍两段，不吃强化等级倍率");
        var random = new FixedRandom(0);
        var upgrading = new InventoryAmplifyUpgradeService(r.Items, r.AmplifyUpgrades, r.Enhancements, r.Disjoint,
            r.GoldLimits, r.EpicPieces, random);
        var service = new InventoryEnhancementService(r.Items, r.Enhancements, r.MedalUpgrades, random, amplification: upgrading);
        var target = seed.Inventory.Find(request.Target)!;
        var gear = r.Items.Get(target.Core.ItemId);
        That(gear.Grade > 0 && gear.Grade < rules.Costs.Length && rules.Costs[gear.Grade] > 0,
            "31440 在强化费用表有正品级，NPC金币可计算");
        var use = request with { Mode = EnhancementMode.Reinforce, MaterialSlot = 358 };
        Character Actor(byte level, int cubes = 1000, uint gold = 10_000_000, byte sortLock = 0, byte lockId = 0, int guard = 0) =>
            seed with
            {
                Inventory = new([
                    target with { Core = target.Core with
                    {
                        Attr = (byte)(0xE0 | level), AmplifyType = 0, AmplifyValue = 0,
                        SortLockFlag = sortLock, EquipmentLockId = lockId
                    } },
                    .. guard == 0 ? Array.Empty<InventoryItem>() :
                        [new(5, MainSlot(123), InventoryRewardGrantService.Create(r.Items.Get(guard), 2) with { ExpireTime = 0 })]
                ], Gold: gold) { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, cubes) }
            };
        random.Value = 0;
        var actor = Actor(0, sortLock: 1);
        var result = service.Plan(actor, use, out var receipt);
        int gold0 = rules.Gold(gear, 0), cubes0 = rules.Steps[0].MaterialCount;
        That(result.Accepted && receipt is { Mode: EnhancementMode.Reinforce, Path: 0, MaterialSlot: 358, OutcomeCode: 0,
                NewLevel: 1, GoldCost: var cost, TerminalSlot: -1, Refunds.IsEmpty: true } && cost == gold0 &&
            receipt.MaterialRemaining == 1000 - cubes0 && result.Plan!.Reason == "equipment_reinforcement_npc" &&
            result.Plan.After.Find(use.Target) == actor.Inventory.Find(use.Target)! with
                { Core = actor.Inventory.Find(use.Target)!.Core with { Attr = 0xE1 } } &&
            CurrencyWallet.Get(result.Plan.After, CurrencyKind.ClearCube) == 1000 - cubes0 &&
            result.Plan.After.Gold == actor.Inventory.Gold - (uint)gold0,
            "NPC强化Mode0/槽358扣钱包无色与三段金币，排序锁不拦，完整UID与核心保留");
        int calls = random.Calls;
        foreach (var invalid in new[]
        {
            Actor(0, cubes: cubes0 - 1),
            Actor(0, gold: 0),
            Actor(0, lockId: 1),
            Actor(0) with { Inventory = Actor(0).Inventory with { Items = Actor(0).Inventory.Items.SetItem(0,
                target with { Core = target.Core with { Attr = 0xE0, AmplifyType = 3, AmplifyValue = 6 } }) } }
        })
            That(!service.Plan(invalid, use, out _).Accepted && random.Calls == calls,
                "无色不足、金币不足、安全锁、已增幅目标在抽签前拒绝，不扣钱包");
        var ticket = new InventoryItem(6, MainSlot(65), InventoryRewardGrantService.Create(r.Items.Get(7804), 2) with { ExpireTime = 0 });
        var asMaterial = Actor(0) with { Inventory = Actor(0).Inventory with { Items = Actor(0).Inventory.Items.Add(ticket) } };
        That(!service.Plan(asMaterial, use with { MaterialSlot = 65 }, out _).Accepted && random.Calls == calls,
            "保护券不能当作强化材料，仍走自动扫描而不是MaterialSlot");
        That(!service.Plan(Actor(0), use with { MaterialSlot = 357 }, out _).Accepted && random.Calls == calls,
            "材料槽必须是表内材料对应的虚拟晶块槽");
        random.Value = 0;
        var protectedRole = Actor(12, guard: 7804);
        result = service.Plan(protectedRole, use, out receipt);
        int gold12 = rules.Gold(gear, 12), cubes12 = rules.Steps[12].MaterialCount;
        That(result.Accepted && receipt is { OutcomeCode: 2, NewLevel: 0, TerminalSlot: 123, TerminalRemaining: 1,
                GoldCost: var destroyGold } && destroyGold == gold12 &&
            receipt.MaterialRemaining == 1000 - cubes12 &&
            result.Plan!.After.Find(use.Target)!.Uid == protectedRole.Inventory.Find(use.Target)!.Uid &&
            result.Plan.After.Find(MainSlot(123))!.Core.Count == 1 &&
            CurrencyWallet.Get(result.Plan.After, CurrencyKind.ClearCube) == 1000 - cubes12,
            "NPC强化+12毁装失败消费7804归零，扣无色与金币，不发销毁补偿");
        random.Value = 0;
        var destroyed = service.Plan(Actor(12), use, out var destruction);
        int cubeRefund = destruction!.Refunds.Where(row => row.ItemId == 3037).Sum(row => row.Count);
        That(destroyed.Accepted && destruction is { OutcomeCode: 3, TerminalSlot: -1 } &&
            destroyed.Plan!.After.Find(use.Target) is null &&
            CurrencyWallet.Get(destroyed.Plan.After, CurrencyKind.ClearCube) == 1000 - cubes12 + cubeRefund,
            "无保护时NPC强化销毁按原事务发放补偿，回执晶块增量不含已扣材料");
    }

    static void PotionBoundaries(TcpTestResources resources, Character seed, EnhancementRequest request)
    {
        var r = resources.ItemResources;
        var clock = new ConsumableUseSessionTests.FakeClock();
        var random = new FixedRandom(0);
        var service = new InventoryAmplifyUpgradeService(r.Items, r.AmplifyUpgrades, r.Enhancements,
            r.Disjoint, r.GoldLimits, r.EpicPieces, random, clock, r.RecoveryConsumables);
        var target = seed.Inventory.Find(request.Target)!;
        var actor = seed with { Inventory = seed.Inventory with { Items = seed.Inventory.Items.Replace(target,
            target with { Core = target.Core with { Attr = 0xE9 } }) } };
        long until = clock.GetUtcNow().ToUnixTimeMilliseconds() + 180000;
        var reinforcementRules = new EnhancementCatalog(path => path.StartsWith("stackable/", StringComparison.Ordinal)
                ? "[equipment reinforcement ticket]\n1 60 `additional` -1\n" : "",
            new Dictionary<int,string> { [490705402] = "potion-ticket.stk" },
            new Dictionary<int,string> { [target.Core.ItemId] = "potion-gear.equ" });
        var reinforcementService = new InventoryEnhancementService(r.Items, reinforcementRules, r.MedalUpgrades,
            random, clock, consumables: r.RecoveryConsumables);
        var coupon = new InventoryItem(3, MainSlot(65),
            InventoryRewardGrantService.Create(r.Items.Get(490705402), 2) with { ExpireTime = 0 });
        var reinforce = actor with { Inventory = actor.Inventory with { Items = [coupon,
            target with { Core = target.Core with { Attr = 0xE0, AmplifyType = 0, AmplifyValue = 0 } }], ConsumableStates =
            [new(ConsumableStateKind.Effect, 490004594, until, 7, -490004594)] } };
        random.Value = 6000;
        int reinforceCalls = random.Calls;
        That(reinforcementService.Plan(reinforce, request with { Mode = EnhancementMode.Reinforce, MaterialSlot = 65 },
                out var reinforceReceipt).Accepted && reinforceReceipt is { Succeeded: true, NewLevel: 1 } && random.Calls == reinforceCalls + 1,
            "实际强化服务从持久药效读取1000并改变相同6000抽样结果，不仅直接传策略参数");
        var treated = actor with { Inventory = actor.Inventory with { ConsumableStates =
            [new(ConsumableStateKind.Effect, 10004688, until, 7, 20150129)] } };
        int baseFailure = r.AmplifyUpgrades.Rules.Steps[9].FailureWeight;
        That(baseFailure is > 0 and < 100000, "药剂服务回归选实际存在成功失败两区间的+9普通资源行");
        random.Value = baseFailure - 1;
        int calls = random.Calls;
        var plain = service.Plan(actor, request, out var plainReceipt);
        var boosted = service.Plan(treated, request, out var boostedReceipt);
        That(plain.Accepted && plainReceipt is { OutcomeCode: 1, NewLevel: 9 } && boosted.Accepted &&
            boostedReceipt is { OutcomeCode: 0, NewLevel: 10 } && random.Calls == calls + 2 &&
            plainReceipt.BaseSuccessPercent == (100000 - baseFailure) / 1000m &&
            plainReceipt.SuccessPercent == plainReceipt.BaseSuccessPercent &&
            boostedReceipt.BaseSuccessPercent == plainReceipt.BaseSuccessPercent &&
            boostedReceipt.SuccessPercent > plainReceipt.SuccessPercent &&
            boostedReceipt.MaterialRemaining == plainReceipt.MaterialRemaining && boosted.Plan!.After.Gold == plain.Plan!.After.Gold &&
            boosted.Plan.After.Find(request.Target) == actor.Inventory.Find(request.Target)! with
                { Core = actor.Inventory.Find(request.Target)!.Core with { Attr = 0xEA } },
            "真实普通增幅同一基础失败边界有药升十无药保九，每次只抽一次且金币材料和完整UID契约相同");
        var wrongMode = treated with { Inventory = treated.Inventory with { ConsumableStates =
            [new(ConsumableStateKind.Effect, 490004594, until, 7, -490004594)] } };
        That(service.Plan(wrongMode, request, out var wrongReceipt).Accepted && wrongReceipt is { OutcomeCode: 1, NewLevel: 9 },
            "实际增幅服务不能借强化模式药效改变结果");

        var portableSource = new InventoryItem(3, MainSlot(65),
            InventoryRewardGrantService.Create(r.Items.Get(10007839), 2) with { ExpireTime = 0 });
        var portable = actor with { Inventory = actor.Inventory with { Items =
            [actor.Inventory.Find(request.Target)!, portableSource], Gold = 0 } };
        var portableTreated = portable with { Inventory = portable.Inventory with { ConsumableStates = treated.Inventory.ConsumableStates } };
        random.Value = InventoryAmplifyUpgradeService.AdjustedFailureWeight(baseFailure, 30000) - 1;
        calls = random.Calls;
        var portablePlain = service.Plan(portable, request with { MaterialSlot = 65 }, out var portablePlainReceipt);
        var portableBoost = service.Plan(portableTreated, request with { MaterialSlot = 65 }, out var portableBoostReceipt);
        That(portablePlain.Accepted && portablePlainReceipt is { OutcomeCode: 1, NewLevel: 9, GoldCost: 0, MaterialRemaining: 1 } &&
            portableBoost.Accepted && portableBoostReceipt is { OutcomeCode: 0, NewLevel: 10, GoldCost: 0, MaterialRemaining: 1 } &&
            random.Calls == calls + 2 && portableBoost.Plan!.After.Gold == 0,
            "真实新便携器保留原三成相对加成后再接药效，同一边界不重抽、不收金币或增加源扣费");

        var luckySource = portableSource with { Core = InventoryRewardGrantService.Create(r.Items.Get(10007407), 2) with { ExpireTime = 0 } };
        var lucky = portable with { Inventory = portable.Inventory with { Items = [portable.Inventory.Find(request.Target)!, luckySource] } };
        var luckyTreated = lucky with { Inventory = lucky.Inventory with { ConsumableStates = treated.Inventory.ConsumableStates } };
        var luckyRow = r.AmplifyUpgrades.Lucky[9];
        foreach (int roll in new[] { luckyRow.DownWeight * 1000 - 1, (luckyRow.DownWeight + luckyRow.SameWeight) * 1000 - 1,
            (luckyRow.DownWeight + luckyRow.SameWeight) * 1000 }.Distinct())
        {
            random.Value = roll;
            calls = random.Calls;
            var without = service.Plan(lucky, request with { MaterialSlot = 65, Mode = EnhancementMode.LuckyAmplify }, out var beforeReceipt);
            var withPotion = service.Plan(luckyTreated, request with { MaterialSlot = 65, Mode = EnhancementMode.LuckyAmplify }, out var afterReceipt);
            That(without.Accepted && withPotion.Accepted && beforeReceipt == afterReceipt && random.Calls == calls + 2 &&
                without.Plan!.After.Find(request.Target) == withPotion.Plan!.After.Find(request.Target),
                "实际幸运增幅三段边界加药前后完整结果相同，仍每次只抽一次");
        }
        clock.Advance(TimeSpan.FromMilliseconds(180000));
        random.Value = 6000;
        That(reinforcementService.Plan(reinforce, request with { Mode = EnhancementMode.Reinforce, MaterialSlot = 65 },
                out var reinforceExpired).Accepted && reinforceExpired is { Succeeded: false, NewLevel: 0 },
            "实际强化服务在持久效果恰到期时不再提高成功率，不按物品类别永久加成");
        random.Value = baseFailure - 1;
        That(service.Plan(treated, request, out var expired).Accepted && expired == plainReceipt,
            "实际服务在药效截止等号恢复原失败结果，残留持久行不继续提高成功率");
    }

    static void Exact433ProtectionCommit(TcpTestResources resources, Character seed,
        InventoryAmplifyUpgradeService service, FixedRandom random, EnhancementRequest request)
    {
        var r = resources.ItemResources;
        That(r.AmplifyUpgrades.Get(490705433) is { Kind: AmplifyUseKind.Protection, RetainLevel: 0, PendingRule: null },
            "真实433按无参数增幅保护动作装配，不从说明文字另加当前等级门槛");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-protection433-" + Guid.NewGuid().ToString("N"));
        bool passed = false;
        try
        {
            string database = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(database);
            var owners = new AccountOwners();
            foreach (var (level, success) in new[] { (9, false), (10, false), (10, true), (11, false), (11, true) })
            {
                using var owner = owners.Acquire($"protection433-{level}-{success}", () => { });
                var actor = repository.Create(owner, new($"Guard{level}{(success ? 'S' : 'F')}", seed.Position, seed.Return)
                {
                    Job = seed.Job, Level = seed.Level,
                    Inventory = seed.Inventory with { Items = [.. seed.Inventory.Items.Select(item => item with
                        { Uid = 0, Core = item.Position == request.Target ? item.Core with { Attr = (byte)(0xE0 | level) } : item.Core }),
                        new(0, MainSlot(122), InventoryRewardGrantService.Create(r.Items.Get(490705433), 2) with { ExpireTime = 0 })] }
                });
                var target = actor.Inventory.Find(request.Target)!;
                var source = actor.Inventory.Find(MainSlot(request.MaterialSlot))!;
                var protection = actor.Inventory.Find(MainSlot(122))!;
                var step = r.AmplifyUpgrades.Rules.Steps[level];
                That(target.Uid > 0 && source.Uid > 0 && protection.Uid > 0 &&
                    new[] { target.Uid, source.Uid, protection.Uid }.Distinct().Count() == 3 &&
                    step.FailureWeight is > 0 and < 100000, "433夹具使用真实持久UID及可达成功、失败边界");
                random.Value = success ? step.FailureWeight : step.FailureWeight - 1;
                int calls = random.Calls;
                var result = service.Plan(actor, request, out var receipt);
                bool consumed = !success && level >= 10;
                int nextLevel = success ? level + 1 : consumed ? 0 : level;
                var expected = actor.Inventory with { Gold = actor.Inventory.Gold - (uint)r.AmplifyUpgrades.Rules.Gold(r.Items.Get(target.Core.ItemId)),
                    Items = actor.Inventory.Items.Select(item => item.Uid == target.Uid
                        ? item with { Core = item.Core with { Attr = (byte)(0xE0 | nextLevel) } }
                        : item.Uid == source.Uid ? item with { Core = item.Core with { Value = item.Core.Count - step.MaterialCount } }
                        : item.Uid == protection.Uid && consumed ? item with { Core = item.Core with { Value = 1 } } : item).ToImmutableArray() };
                That(result.Accepted && random.Calls == calls + 1 && receipt is { Refunds.IsEmpty: true } &&
                    receipt.OldLevel == level && receipt.NewLevel == nextLevel && receipt.OutcomeCode == (success ? 0 : consumed ? 2 : 1) &&
                    receipt.MaterialRemaining == source.Core.Count - step.MaterialCount &&
                    receipt.TerminalSlot == (consumed ? 122 : -1) && receipt.TerminalRemaining == (consumed ? 1 : 0) &&
                    Inventory(result.Plan!.After) == Inventory(expected),
                    "433在+10尝试11及+11毁装失败只扣一张、装备归零原UID和完整核心保留；成功或+9保级失败不扣券，无销毁补偿");
                if (level == 11 && !success)
                {
                    string before = Snapshot();
                    Execute("CREATE TRIGGER fail_exact433_late BEFORE INSERT ON inventory_audit_log " +
                        "WHEN NEW.action_name='equipment_amplification_npc' BEGIN SELECT RAISE(ABORT,'exact433 late rollback'); END");
                    SqliteException? failure = null;
                    try { InventoryService.Commit(repository, owner, actor, result); }
                    catch (SqliteException error) { failure = error; }
                    That(failure is { SqliteErrorCode: 19 } && failure.Message.Contains("exact433 late rollback", StringComparison.Ordinal) &&
                        Snapshot() == before, "433保护失败末段SQL故障完整回滚装备、源、保护券、钱包、revision、审计及UID序列");
                    Execute("DROP TRIGGER fail_exact433_late");
                }
                var committed = InventoryService.Commit(repository, owner, actor, result);
                That(Inventory(committed.Inventory) == Inventory(expected) && committed.Revision == actor.Revision + 1,
                    "433原计划提交只消费规划数量并递增一次revision，回滚恢复无需重抽随机");
                var loaded = new CharacterRepository(database).LoadCharacter(owner, actor.Slot);
                That(Inventory(loaded.Inventory) == Inventory(expected) && loaded.Revision == committed.Revision && random.Calls == calls + 1,
                    "433成功、保护失败及不扣券分支重载保持完整持久实例，恢复提交不再次抽签");

                string Snapshot()
                {
                    using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
                    connection.Open();
                    var rows = new List<string>();
                    foreach (string table in new[] { "characters", "character_inventory_items", "character_inventory_slots",
                        "inventory_audit_log", "currency_audit_log", "sqlite_sequence" })
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = "SELECT * FROM " + table;
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(table + ":" + JsonSerializer.Serialize(row)); }
                    }
                    return JsonSerializer.Serialize(repository.LoadCharacter(owner, actor.Slot)) + JsonSerializer.Serialize(rows.OrderBy(row => row, StringComparer.Ordinal));
                }
                void Execute(string sql)
                {
                    using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
            }
            passed = true;
        }
        finally
        {
            if (passed && Directory.Exists(root)) TestFiles.DeleteDirectory(root, true);
            else Console.Error.WriteLine("433回归失败现场保留：" + root);
        }
        static string Inventory(InventorySnapshot value) => JsonSerializer.Serialize(value with
            { Items = value.Items.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() });
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
