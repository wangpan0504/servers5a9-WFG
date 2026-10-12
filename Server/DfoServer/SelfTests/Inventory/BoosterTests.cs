using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class BoosterTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException) { failed = true; }
            That(failed, name);
        }
        var catalog = itemResources.Items;
        BoosterBatchRandomTests.Run(That);
        var random = new EdgeRandom();
        var service = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces, random);
        var now = DateTimeOffset.UtcNow;
        var current = new Character(1, 0, "Booster", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        That(itemResources.Boosters.Get(7542).Pools.Length == 3 && itemResources.Boosters.Get(2058).Pools.Length == 2,
            "repeated pool tags remain independent in matched PVF");
        That(itemResources.Boosters.DirectActivationItems.Contains(30) && itemResources.Boosters.DirectActivationItems.Contains(33) &&
            !itemResources.Boosters.DirectActivationItems.Contains(1), "contract identity comes from matched premium resources, coins stay wallets");
        var parsed = Parse("[etc]\n2 1106 0.25 7 1112 0.75 9\n[/etc]\n[etc]\n1 1106 100 3\n[/etc]");
        That(parsed.Available && parsed.Pools.Length == 2 && parsed.Pools[0].Draws == 2 &&
            parsed.Pools[0].Rewards.SequenceEqual(new BoosterReward[] { new(1106, 25, 7), new(1112, 75, 9) }),
            "fractional weights retain exact proportions and all declared quantities");
        foreach (string bad in new[] { "[etc]\n0 1106 1 1\n[/etc]", "[etc]\n1 1106 -1 1\n[/etc]", "[etc]\n1 1106 1 0\n[/etc]",
            "[etc]\n1 1106 1\n[/etc]", "[etc]\n1 1106 1 1\n[/etc]\n99", "[etc]\n1 1106 1 1\n[/etc]\n[avatar]\n1 1 1 1 1 1\n[/avatar]" })
            That(!Parse(bad).Available, "malformed or unsupported reward pool rejects whole definition");
        That(Parse("[etc]\n1\n[/etc]\n[etc]\n1 1106 100 1\n[/etc]").Pools.Length == 1, "empty PVF placeholder is not a fake reward");
        That(!itemResources.Boosters.Get(10162064).Available && !itemResources.Boosters.Get(490704415).Available && !itemResources.Boosters.Get(1106).Available,
            "quota, advancement pools and ordinary consumables are not silently treated as simple boxes");
        var package = BoosterCatalog.Parse("[stackable type]\n`[usable cera package]` 0\n[/stackable type]\n[package data]\n1106 2 1112 1\n[/package data]");
        That(package.Available && package.PackageRewards.SequenceEqual(new BoosterReward[] { new(1106, 1, 2), new(1112, 1, 1) }),
            "fixed CERA packages expose every PVF package reward without dropping rows");
        foreach (string body in new[] { "", "1106", "1106 0", "0 1", "1106 2147483648" })
            That(!BoosterCatalog.Parse("[stackable type]\n`[usable cera package]` 0\n[package data]\n" + body + "\n[/package data]").Available,
                "固定礼包拒绝缺失、非法和超界奖励行");
        That(!BoosterCatalog.Parse("[stackable type]\n`[usable cera package]` 0\n[package data]\n1106 1\n[/package data]\n" +
            "[package data]\n1112 1\n[/package data]").Available, "固定礼包拒绝重复奖励段，不能只采用第一段");
        That(!BoosterCatalog.Parse("[stackable type]\n`[usable cera package]` 0\n[total usable count]\n1\n[package data]\n1106 1\n[/package data]").Available,
            "固定礼包不能绕过未实现的开包资格和使用次数限制");
        That(CeraPackageRequests.Read([65, 0, 0]) is { Slot: 65, Choices.Length: 0 }, "固定礼包原生请求保留空选择列表");
        var packageChoice = CeraPackageRequests.Read([65, 0, 1, 0x12, 0x04, 0, 0, 0xff]);
        That(packageChoice.Choices is [{ ItemId: 1042, Option: 255 }], "时装选择字段完整保留，不解释未知属性byte");
        foreach (byte[] body in new byte[][] { [], [65, 0], [65, 0, 0, 0], [65, 0, 1], [255, 255, 0],
            [65, 0, 1, 0, 0, 0, 0, 0], [65, 0, 2, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1] })
            Reject(() => CeraPackageRequests.Read(body), "礼包请求拒绝截断、尾段、无效槽位及重复选择");

        var packageService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits,
            itemResources.EpicPieces, debugSettings: new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true },
            contracts: itemResources.Contracts);
        foreach (int id in new[] { 490705385, 490705386, 490705483 })
        {
            var expected = itemResources.Boosters.Get(id).PackageRewards.GroupBy(row => row.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
            var role = current with { Inventory = new([new(1, Bag(65), InventoryRewardGrantService.Create(catalog.Get(id), utcNow: now, shopExpiration: true))]) };
            var opened = packageService.OpenCeraPackage(role, new(65, []), out var packageReceipt, now);
            That(opened.Accepted && packageReceipt!.Rewards.Length == expected.Count &&
                packageReceipt.Rewards.All(row => expected[row.ItemId] == row.Count), "众神降世礼包完整发放匹配PVF的全部固定奖励 " + id);
            That(role.Inventory.Find(Bag(65))!.Core.Count == 1 && opened.Plan!.After.Items.Sum(row => (long)row.Core.Count) == expected.Values.Sum(value => (long)value),
                "固定开包在提交前不改源，规划奖励数量与资源一致 " + id);
            That(packageService.OpenCeraPackage(role, new(65, [new(1042, 255)]), out var rejectedReceipt, now) is { Accepted: false } && rejectedReceipt is null &&
                !packageService.Open(role, new(65, 1), out _, now).Accepted, "非空时装选择和普通开箱入口均不能消费固定商城包 " + id);
            var expiredSource = role with { Inventory = new([new(1, Bag(65), InventoryRewardGrantService.Create(catalog.Get(id), utcNow: now))]) };
            That(!service.OpenCeraPackage(expiredSource, new(65, []), out _, now).Accepted,
                "按PVF原期限创建的2020礼包已经过期，不能消费源 " + id);
        }
        var ordinarySource = current with { Inventory = new([new(1, Bag(65), Core(7542))]) };
        That(!packageService.OpenCeraPackage(ordinarySource, new(65, []), out _, now).Accepted, "商城开包命令不能消费普通随机盒");
        var contractRole = current with { Inventory = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty } };
        foreach (int type in new[] { 22, 27 })
        {
            string rule = type == 22 ? "[overequip list] 1 `[weapon]` [/overequip list]\n[type]22\n[over equipinfo]1 1 10\n" :
                "[type]27\n[over skill]7\n";
            var contractCatalog = PremiumContractCatalog.Parse(rule + "[target item]7001\n[term]1");
            var contractGrant = InventoryRewardGrantService.TryGrantBatch(contractRole, [new(7001, 1)], catalog,
                itemResources.GoldLimits, itemResources.EpicPieces, now, contracts: contractCatalog);
            That(contractGrant.Accepted && contractGrant.Plan!.After.Items.IsEmpty &&
                contractGrant.Plan.After.AccountWallet.ContractExpiry(type) == now.ToUnixTimeSeconds() + 86400,
                "具有完整参数的已支持契约礼包奖励写入账号期限而不创建实体槽 " + type);
        }
        That(!InventoryRewardGrantService.TryGrantBatch(contractRole, [new(10000387, 1)], catalog,
            itemResources.GoldLimits, itemResources.EpicPieces, now).Accepted,
            "统一grant缺契约依赖时拒绝新premium奖励，不因[contract]/[etc]宽类别发成普通实体");
        var existingPremium = new InventoryItem(99, Bag(65), Core(10000387) with { Marker16 = 77, SortLockFlag = 1 });
        var restoredPremium = InventoryRewardGrantService.TryGrantItems(contractRole, [existingPremium], catalog,
            itemResources.GoldLimits, itemResources.EpicPieces, now);
        That(restoredPremium.Accepted && restoredPremium.Plan!.After.Items.Single() == existingPremium &&
            restoredPremium.Plan.After.AccountWallet.PremiumContracts.Count == 0,
            "完整既有premium实例转移保留UID及核心，不重新激活或误丢弃；消费仍须独立能力门禁");
        foreach (int type in new[] { 84, 97, 29 })
        {
            var unsupportedContract = PremiumContractCatalog.Parse("[type] " + type + "\n[target item] 1106\n[term] 1\n[/type]");
            var rejectedGrant = InventoryRewardGrantService.TryGrantBatch(contractRole, [new(42, 1), new(1106, 1)], catalog,
                itemResources.GoldLimits, itemResources.EpicPieces, now, contracts: unsupportedContract);
            That(rejectedGrant is { Accepted: false, Plan: null } && contractRole.Inventory == InventorySnapshot.Empty,
                "已识别但效果未完整实现的premium整批拒绝，不退回实体或只发前项复活币 " + type);
        }
        var contractRandom = new EdgeRandom();
        var injectedContracts = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits,
            itemResources.EpicPieces, contractRandom, contracts: itemResources.Contracts);
        var pending = current with { Inventory = new([new(1, Bag(3), Core(38))]) };
        That(!injectedContracts.Open(pending, new(3, 1), out var rejected, now).Accepted && rejected is null &&
            pending.Inventory.Find(Bag(3))!.Core.Count == 1 && contractRandom.Calls == 0,
            "提供完整契约目录也不能忽略38的独立detail合同，源和所有奖项完整保留");
        var premiumBox = itemResources.Boosters.Get(762);
        That(premiumBox.Available && premiumBox.Selection is null && premiumBox.PackageRewards.IsDefaultOrEmpty,
            "762真实奖池保持全部原池、抽数和正权重候选，不凭名称猜包含哪些契约");
        bool unsupportedPremium = premiumBox.Pools.SelectMany(pool => pool.Rewards).Where(row => row.Weight > 0)
            .Any(row => CurrencyWallet.RewardKind(row.ItemId) is null && !itemResources.EpicPieces.ByPiece.ContainsKey(row.ItemId) &&
                !itemResources.Contracts.TryConsumableContract(row.ItemId, out _) &&
                (itemResources.Contracts.Recognizes(row.ItemId) || itemResources.Boosters.DirectActivationItems.Contains(row.ItemId)));
        var premiumRole = current with { Inventory = new([new(1, Bag(3), Core(762))]) };
        var premiumOpen = injectedContracts.Open(premiumRole, new(3, 1), out var premiumReceipt, now);
        if (unsupportedPremium)
        {
            That(premiumOpen is { Accepted: false, Plan: null } && premiumReceipt is null && contractRandom.Calls == 0 &&
                premiumRole.Inventory.Find(Bag(3))!.Core.Count == 1 && premiumRole.Inventory.AccountWallet.PremiumContracts.Count == 0,
                "实际含未支持premium的762在随机前拒绝整包，源、账号期限和全部奖项保持不变");
        }
        else
        {
            // EdgeRandom命中每个独立池的首个正权重候选，按原抽数和数量独立合计全部预期。
            var expectedRewards = premiumBox.Pools.Select(pool => (Pool: pool, Reward: pool.Rewards.First(row => row.Weight > 0)))
                .GroupBy(row => row.Reward.ItemId).ToDictionary(group => group.Key, group => group.Sum(row => row.Pool.Draws * row.Reward.Count));
            var expectedExpiries = expectedRewards.Where(pair => itemResources.Contracts.TryConsumableContract(pair.Key, out _))
                .GroupBy(pair => itemResources.Contracts.Get(pair.Key)!.Type)
                .ToDictionary(group => group.Key, group => now.ToUnixTimeSeconds() +
                    group.Sum(pair => itemResources.Contracts.Get(pair.Key)!.DurationSeconds * pair.Value));
            That(premiumOpen.Accepted && premiumReceipt!.Rewards.Length == expectedRewards.Count &&
                premiumReceipt.Rewards.All(row => expectedRewards[row.ItemId] == row.Count) &&
                premiumOpen.Plan!.After.AccountWallet.PremiumContracts.Count == expectedExpiries.Count &&
                expectedExpiries.All(pair => premiumOpen.Plan.After.AccountWallet.ContractExpiry(pair.Key) == pair.Value) &&
                premiumRole.Inventory.Find(Bag(3))!.Core.Count == 1 && premiumOpen.Plan.After.Find(Bag(3)) is null,
                "全部真实候选已支持的762完整消费并按全部实际数量激活期限，原源快照不变");
            foreach (var group in expectedRewards.GroupBy(pair => CurrencyWallet.RewardKind(pair.Key)))
                if (group.Key is { } kind)
                    That(CurrencyWallet.Get(premiumOpen.Plan!.After, kind) == group.Sum(pair => (long)pair.Value), "762完整保留虚拟货币收益");
            var expectedPhysical = expectedRewards.Where(pair => CurrencyWallet.RewardKind(pair.Key) is null &&
                !itemResources.EpicPieces.ByPiece.ContainsKey(pair.Key) && !itemResources.Contracts.TryConsumableContract(pair.Key, out _))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var actualPhysical = premiumOpen.Plan!.After.Items.GroupBy(item => item.Core.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Core.Count));
            That(actualPhysical.Count == expectedPhysical.Count && expectedPhysical.All(pair => actualPhysical.GetValueOrDefault(pair.Key) == pair.Value),
                "762完整实物奖励与虚拟权益分别保存，不创建契约伪实体或丢弃已支持实物");
            foreach (var pair in expectedRewards.Where(pair => itemResources.EpicPieces.ByPiece.ContainsKey(pair.Key)))
                That(premiumOpen.Plan.After.EpicPieces.Get(itemResources.EpicPieces.ByPiece[pair.Key].Index) == pair.Value,
                    "762完整保留账号图鉴碎片收益");
        }
        var randomPool = itemResources.Boosters.Get(2058);
        int syntheticPremiumId = randomPool.Pools.SelectMany(pool => pool.Rewards)
            .First(row => row.Weight > 0 && CurrencyWallet.RewardKind(row.ItemId) is null && !itemResources.EpicPieces.ByPiece.ContainsKey(row.ItemId)).ItemId;
        foreach (int type in new[] { 84, 97 })
        {
            var syntheticContracts = new PremiumContractCatalog(new Dictionary<int, PremiumContract>
            { [syntheticPremiumId] = new(syntheticPremiumId, type, 86400) });
            var rejectRandom = new EdgeRandom();
            var rejectService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits,
                itemResources.EpicPieces, rejectRandom, contracts: syntheticContracts);
            var source = current with { Inventory = new([new(1, Bag(3), Core(2058))]) };
            That(rejectService.Open(source, new(3, 1), out var rejectedReceipt, now) is { Accepted: false, Plan: null } &&
                rejectedReceipt is null && rejectRandom.Calls == 0 && source.Inventory.Find(Bag(3))!.Core.Count == 1,
                "合成未支持premium候选在实际多候选随机池抽取前拒绝，池和权重均不修改 " + type);
        }

        current = current with { Inventory = new([new(1, Bag(3), Core(7542, 2)), new(2, Bag(65), Core(1108, 990) with { SortLockFlag = 1 })], Gold: 12345) };
        var before = current.Inventory;
        var result = service.Open(current, new(3, 2), out var receipt, now);
        That(!result.Accepted && result.Error == 4 && result.Plan is null && receipt is null && current.Inventory == before,
            "reward stack overflow keeps all boxes, rewards and gold without allocating another stack");
        current = current with { Inventory = before with { Items = before.Items.SetItem(1,
            before.Items[1] with { Core = before.Items[1].Core with { Value = 960 } }) } };
        before = current.Inventory;
        result = service.Open(current, new(3, 2), out receipt, now);
        That(result.Accepted && current.Inventory == before && random.Calls == 0, "deterministic batch is planned without modifying source or drawing unnecessarily");
        That(result.Plan!.After.Find(Bag(3)) is null && result.Plan.After.Find(Bag(65)) is { Uid: 2, Core.Value: 1000, Core.SortLockFlag: 1 } &&
            result.Plan.After.Items.Count(item => item.Core.ItemId == 1108) == 1, "quickbar source and exact stack capacity preserve surviving UID and sort lock");
        That(receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(1108, 40), new(1114, 40), new(1016, 40) }) && result.Plan.After.Gold == 12345,
            "every independent pool is multiplied by source count without an extra fee");
        byte[] ack = BoosterPackets.Opened(result, receipt);
        That(ack.Length == 42 && ack[0] == 1 && ack[1] == 0 && I32(ack, 2) == 7542 && U16(ack, 6) == 3 &&
            I32(ack, 8) == 0 && I32(ack, 12) == 0 && U16(ack, 16) == 3 && I32(ack, 18) == 1108 && I32(ack, 22) == 40,
            "Client success has full 18-byte header followed by item/count pairs");
        That(BoosterPackets.Opened(InventoryResult.Reject(19), null).SequenceEqual(new byte[] { 0, 19, 0 }), "failure uses common u16 result code");
        var fullEquipment = Enumerable.Range(9, 56).Select(slot => new InventoryItem(slot - 8, Bag((short)slot), Core(10400))).ToImmutableArray();
        var overflowGrant = InventoryRewardGrantService.TryGrantBatch(current with { Inventory = new(fullEquipment) },
            [new(10400, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces, now, allowOverflowMail: true);
        That(overflowGrant.Accepted && overflowGrant.Plan!.OverflowItems is { Length: 1 } &&
            overflowGrant.Plan.OverflowItems[0].Core.ItemId == 10400 &&
            overflowGrant.Plan.After.Items.Length == fullEquipment.Length,
            "package overflow keeps the source transaction successful and records the complete reward for mail delivery");
        That(BoosterRequests.Read([3, 0, 2, 0, 0, 0]) == new BoosterOpenRequest(3, 2), "ordinary request carries u16 slot and i32 count");
        foreach (int width in new[] { 0, 2, 5, 7, 9 })
            Reject(() => BoosterRequests.Read(new byte[width]), "parser rejects nonordinary width " + width);
        foreach (int count in new[] { int.MinValue, -1, 0, 3, int.MaxValue })
            That(!service.Open(current, new(3, count), out _).Accepted && current.Inventory == before, "invalid source count does not normalize to one or whole stack");
        foreach (var core in new[] { Core(7542, 2) with { EquipmentLockId = 1 }, Core(7542, 2) with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) }, Core(1106) })
            That(!service.Open(current with { Inventory = new([new(1, Bag(3), core)]) }, new(3, 1), out _).Accepted,
                "safety lock, exact expiry and wrong source reject without consuming");
        var sorted = current with { Inventory = new([new(1, Bag(3), Core(7542) with { SortLockFlag = 1 })]) };
        That(service.Open(sorted, new(3, 1), out _).Accepted, "sorting lock does not forbid item use");

        var filled = Enumerable.Range(65, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(1106, 1000))).ToImmutableArray();
        filled = filled.SetItem(0, new(65, Bag(65), Core(7542))).SetItem(1, new(66, Bag(66), Core(1114, 980)))
            .SetItem(2, new(67, Bag(67), Core(1016, 980)));
        current = current with { Inventory = new(filled) };
        result = service.Open(current, new(65, 1), out _);
        That(result.Accepted && result.Plan!.After.Items.Length == 56 && result.Plan.After.Find(Bag(65)) is { Uid: 0, Core.ItemId: 1108, Core.Value: 20 },
            "full page succeeds using the exhausted source slot, without a fixed empty-slot prerequisite");
        var notFreed = current with { Inventory = current.Inventory with { Items = filled.SetItem(0, filled[0] with { Core = Core(7542, 2) }) } };
        That(!service.Open(notFreed, new(65, 1), out _).Accepted && notFreed.Inventory.Find(Bag(65))!.Core.Count == 2,
            "full page rejects complete operation when source slot is retained");

        current = current with { Inventory = new([new(1, Bag(3), Core(2058, 2))]) };
        result = service.Open(current, new(3, 2), out receipt);
        That(result.Accepted && random.Calls == 4 && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(2050, 2), new(2054, 2) }),
            "both repeated pools draw once for every source and combine identical outcomes");
        random.Last = true;
        result = service.Open(current, new(3, 1), out receipt);
        That(result.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(2053, 1), new(2057, 1) }), "last weight interval stays reachable");
        var bulkRandom = new BoosterBatchRandomTests.BudgetRandom(31024);
        var bulkService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces, bulkRandom);
        var bulk = current with { Inventory = new([new(1, Bag(3), Core(2058, int.MaxValue))]) };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        result = bulkService.Open(bulk, new(3, int.MaxValue), out receipt);
        timer.Stop();
        That(result.Accepted && bulkRandom.Calls < 128 && receipt!.Rewards.Sum(row => (long)row.Count) == 2L * int.MaxValue &&
            result.Plan!.After.Find(Bag(3)) is null && bulk.Inventory.Find(Bag(3))!.Core.Count == int.MaxValue,
            "largest legal random batch keeps all rewards, consumes no source before commit and needs bounded random work");
        That(receipt!.Rewards.Where(row => row.ItemId is >= 2050 and <= 2053).Sum(row => (long)row.Count) == int.MaxValue &&
            receipt.Rewards.Where(row => row.ItemId is >= 2054 and <= 2057).Sum(row => (long)row.Count) == int.MaxValue &&
            receipt.Rewards.All(row => result.Plan!.After.Items.Where(item => item.Core.ItemId == row.ItemId).Sum(item => (long)item.Core.Count) == row.Count),
            "independent pool counts and complete grant plan agree for a multi-billion draw request");
        Console.WriteLine($"Bulk booster planning: {timer.Elapsed.TotalMilliseconds:F2}ms, {bulkRandom.Calls} random calls");
        var fullEmblems = Enumerable.Range(289, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(2050, int.MaxValue)));
        bulk = bulk with { Inventory = new([bulk.Inventory.Items[0], .. fullEmblems]) };
        That(bulkService.Open(bulk, new(3, int.MaxValue), out receipt) is { Accepted: false, Error: 4 } && receipt is null &&
            bulk.Inventory.Find(Bag(3))!.Core.Count == int.MaxValue, "full reward page rejects entire large batch without partial consumption or truncation");
        current = current with { Inventory = new([new(1, Bag(3), Core(114))]) };
        result = service.Open(current, new(3, 1), out receipt);
        var output = result.Plan!.After.Items.Single();
        That(result.Accepted && current.Level == 1 && current.Job == 15 && output.Core == InventoryRewardGrantService.Create(catalog.Get(receipt!.Rewards[0].ItemId)),
            "equipment reward keeps full core, seal and durability without output job or wear-level restrictions");

        foreach (var (box, reward, count) in new[] { (2660291, 1, 10), (10160255, 10158124, 1), (2683799, 2681762, 720) })
        {
            current = current with { Inventory = new([new(1, Bag(3), Core(box))]) };
            result = service.Open(current, new(3, 1), out receipt);
            var kind = CurrencyWallet.RewardKind(reward)!.Value;
            That(result.Accepted && result.Plan!.After.Items.IsEmpty && CurrencyWallet.Get(result.Plan.After, kind) == count,
                "reward routes to actual wallet without a physical instance " + reward);
            var fullWallet = current with { Inventory = CurrencyWallet.Set(current.Inventory, kind, int.MaxValue) };
            That(service.Open(fullWallet, new(3, 1), out _) is { Accepted: false, Error: 4 } && fullWallet.Inventory.Items.Length == 1,
                "wallet overflow keeps box and never truncates reward " + reward);
        }
        foreach (int id in new[] { 38, 762 })
        {
            current = current with { Inventory = new([new(1, Bag(3), Core(id))]) };
            int calls = random.Calls;
            That(!service.Open(current, new(3, 1), out _).Accepted && random.Calls == calls, "unsupported detail or premium pool rejected before randomness " + id);
        }
        current = current with { Inventory = new([new(1, Bag(3), Core(2660291) with { Value = int.MaxValue })]) };
        That(service.Open(current, new(3, int.MaxValue), out _) is { Accepted: false, Error: 4 }, "batch reward arithmetic overflow rejects without a long deterministic loop");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-booster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("booster-test", () => { });
            repo.Create(owner, new("BoxOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), Core(2660291)), new(0, Bag(65), Core(7542, 2))], Gold: 12345)
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var saved = session.Selected!;
            string snapshot = Snapshot();
            Query("CREATE TRIGGER fail_box BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'box rollback'); END");
            Reject(() => session.PlanAndCommitInventory(role => service.Open(role, new(3, 1), out _)), "wallet write failure propagates before success");
            That(Snapshot() == snapshot && session.Selected == saved, "box, reward, revisions, UID sequence and audit roll back together");
            Query("DROP TRIGGER fail_box");
            var stale = session.Selected!;
            var stalePlan = service.Open(stale, new(65, 2), out _).Plan!;
            That(session.PlanAndCommitInventory(role => service.Open(role, new(3, 1), out _)).Accepted, "retry after explicit reconnectable failure consumes only one coin box");
            Reject(() => repo.CommitInventory(owner, stale, stalePlan), "old revision cannot commit a second random result");
            That(session.PlanAndCommitInventory(role => service.Open(role, new(65, 2), out _)).Accepted, "complete physical reward batch commits");
            snapshot = Snapshot();
            That(!session.PlanAndCommitInventory(role => service.Open(role, new(3, 1), out _)).Accepted && Snapshot() == snapshot,
                "exhausted replay cannot mint another reward");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                "repository reopen restores every committed item and wallet exactly");
            var sibling = repo.Create(owner, new("BoxTwo", towns.Room, towns.InitialReturn));
            That(sibling.Inventory.ReviveCoins == 0 && sibling.Inventory.Items.IsEmpty, "character-owned coin reward does not leak on character creation");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.PlanAndCommitInventory(role => service.Open(role, new(65, 1), out _)), "old generation cannot open boxes");
            That(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion, "booster reuses atomic inventory storage without a schema change");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                Query("SELECT count(*) FROM currency_audit_log") + ":" + Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
        }
        finally { Directory.Delete(root, true); }
        InventoryPlanningTests.Run(towns, characters, itemResources, That);
        Console.WriteLine($"PASS {Count - checks} booster assertions");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count, now);
    }
    static BoosterDefinition Parse(string pools) => BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n" + pools + "\n[/booster info]");
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
    static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
    sealed class EdgeRandom : Random
    {
        public int Calls { get; private set; }
        public bool Last { get; set; }
        public override long NextInt64(long maxValue)
        {
            Calls++;
            return Last ? maxValue - 1 : 0;
        }
    }
}
