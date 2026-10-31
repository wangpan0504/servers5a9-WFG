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
        var purged = itemResources.Boosters.Get(10309039);
        That(catalog.Get(10309039).Name == "净化的异形结晶" && purged.Available &&
            purged.Pools is [{ Draws: 1, Rewards: [{ ItemId: 10305892, Weight: 1000, Count: 1 }] }],
            "净化的异形结晶按真实PVF开启为一个异形结晶，黎明掉落标记不能屏蔽奖池");
        const string daybreakBox = "[stackable type]\n`[booster]` 0\n[daybreak drop item]\n1\n" +
            "[booster info]\n[etc]\n1 10305892 1000 1\n[/etc]\n[/booster info]";
        That(BoosterCatalog.Parse(daybreakBox).Available, "完整根级黎明标记保留普通开箱奖励");
        foreach (string bad in new[] { "", "0", "2", "-1", "x", "1 1", "1\n[daybreak drop item]\n1" })
            That(!BoosterCatalog.Parse(daybreakBox.Replace("[daybreak drop item]\n1", "[daybreak drop item]\n" + bad)).Available,
                "未确认或畸形黎明标记不放行开箱");
        That(!BoosterCatalog.Parse(daybreakBox.Replace("[daybreak drop item]\n1\n", "")
            .Replace("[etc]", "[daybreak drop item]\n1\n[etc]")).Available,
            "嵌套黎明标记不能跳过奖池校验");
        That(!itemResources.Boosters.Get(10305892).Available && !itemResources.Boosters.Get(10305893).Available,
            "带黎明标记的异形结晶与碎片仍是材料，不能作为礼盒消费");
        That(itemResources.Boosters.Get(10312269).Pools is [{ Rewards: [{ ItemId: 10309039, Count: 1 }] }] &&
            itemResources.Boosters.Get(10312270).Pools is [{ Rewards: [{ ItemId: 10305892, Count: 10 }] }],
            "同类外盒保留真实内层净化结晶和直接十个结晶两种不同用途");
        BoosterBatchRandomTests.Run(That);
        BoosterGrowTypeTests.Run(towns, itemResources);
        BoosterRandomOptionTests.Run(towns, itemResources);
        OrdinaryTenSourceResources(towns, itemResources);
        OrdinaryEventCoinResources(itemResources);
        OrdinaryTalmaResources(towns, itemResources);
        BoosterCreatureTests.RunResources(towns, itemResources);
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
            "[etc]\n1 1106 1\n[/etc]", "[etc]\n1 1106 1 1\n[/etc]\n99" })
            That(!Parse(bad).Available, "malformed or unsupported reward pool rejects whole definition");
        That(Parse("[etc]\n1\n[/etc]\n[etc]\n1 1106 100 1\n[/etc]").Pools.Length == 1, "empty PVF placeholder is not a fake reward");
        var transferAura = Parse("[avatar]\n1 113590008 10000 1 2 0\n[/avatar]");
        That(transferAura.Available && transferAura.Pools.Length == 1 &&
            transferAura.Pools[0].Rewards.SequenceEqual(new BoosterReward[] { new(113590008, 10000, 1) }),
            "单行装扮奖池按物品、权重和数量发奖，资源特定的尾部整数列不参与");
        foreach (string bad in new[] { "[avatar]\n1 113590008 1 3 0 101580138 1 3 0\n[/avatar]",
            "[avatar]\n1 113590008 1 3\n[/avatar]", "[avatar]\n0 113590008 1 3 0\n[/avatar]",
            "[avatar]\n1 113590008 1 0 0\n[/avatar]", "[avatar]\n1 113590008 1 3 x 0\n[/avatar]" })
            That(!Parse(bad).Available, "装扮奖池多行、缺列或非法值保持未确认，不随机发外观");
        That(!itemResources.Boosters.Get(10162064).Available && !itemResources.Boosters.Get(1106).Available,
            "quota and ordinary consumables are not silently treated as simple boxes");
        That(itemResources.Boosters.Get(490704415).Available && itemResources.Boosters.Get(490704415).Pools.All(pool => pool.JobTag is not null),
            "转职奖池已解析，不能当成无过滤普通盒抽取");
        That(itemResources.Boosters.Get(490004995) is { Available: true } auraGift &&
            auraGift.Pools.Single().Rewards.Single() is { ItemId: 113590008, Count: 1 },
            "转职光环装扮礼盒读取真实单行装扮奖池，不再落成未知附件");
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
            var expiredCore = role.Inventory.Find(Bag(65))!.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds() - 1) };
            var expiredSource = role with { Inventory = new([new(1, Bag(65), expiredCore)]) };
            That(packageService.OpenCeraPackage(expiredSource, new(65, []), out var expiredReceipt, now) is { Accepted: false, Plan: null } &&
                expiredReceipt is null && expiredSource.Inventory.Find(Bag(65))!.Core == expiredCore && expiredCore.Count == 1,
                "忽略商城绝对期限也必须拒绝已过期源实例，保留源且不生成奖励规划 " + id);
        }
        var ordinarySource = current with { Inventory = new([new(1, Bag(65), Core(7542))]) };
        That(!packageService.OpenCeraPackage(ordinarySource, new(65, []), out _, now).Accepted, "商城开包命令不能消费普通随机盒");
        var avatarPackageService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits,
            itemResources.EpicPieces, debugSettings: new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true },
            contracts: itemResources.Contracts, avatars: itemResources.AvatarPackages);
        const int costumePack = 490704193;
        var costumeDefinition = itemResources.Boosters.Get(costumePack);
        var costumeAvatars = InventoryCeraPackageAvatarRewards.AvatarIds(costumeDefinition.PackageRewards, catalog);
        var capturedCostume = new CeraPackageChoice[]
        {
            new(401550106, 4), new(401560063, 4), new(401570036, 2), new(401520056, 2), new(401500096, 9),
            new(401530044, 4), new(401510075, 0), new(401540071, 0), new(401580002, 0)
        };
        That(costumeDefinition.Available && costumeAvatars.SetEquals(capturedCostume.Select(choice => choice.ItemId)),
            "夏日化装舞会礼包的时装奖励就是客户端518提交的九件");
        var costumeRole = current with
        {
            Job = 0,
            Inventory = new([new(1, Bag(65), InventoryRewardGrantService.Create(catalog.Get(costumePack), utcNow: now, shopExpiration: true))])
        };
        That(!avatarPackageService.OpenCeraPackage(costumeRole, new(65, capturedCostume[..^1]), out _).Accepted &&
            costumeRole.Inventory.Find(Bag(65))!.Core.ItemId == costumePack, "少选一件时装不能开含时装的固定礼包");
        That(!avatarPackageService.OpenCeraPackage(costumeRole, new(65, [.. capturedCostume, new(1042, 0)]), out _).Accepted,
            "多选外来编号不能开含时装的固定礼包");
        var wrongCostume = capturedCostume.ToArray();
        wrongCostume[0] = new(1042, 4);
        That(!avatarPackageService.OpenCeraPackage(costumeRole, new(65, wrongCostume), out _).Accepted,
            "错ID不能开含时装的固定礼包");
        byte invalidAbility = Enumerable.Range(0, 256).Select(value => (byte)value)
            .First(value => !itemResources.AvatarPackages.GetAvatar(401550106).Abilities.Contains(value));
        var invalidCostume = capturedCostume.ToArray();
        invalidCostume[0] = new(invalidCostume[0].ItemId, invalidAbility);
        That(!avatarPackageService.OpenCeraPackage(costumeRole, new(65, invalidCostume), out _).Accepted &&
            costumeRole.Inventory.Find(Bag(65))!.Core.Count == 1, "非法时装属性不能消费源");
        var openedCostume = avatarPackageService.OpenCeraPackage(costumeRole, new(65, capturedCostume), out var costumeReceipt, now);
        var expectedCostume = costumeDefinition.PackageRewards.GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        That(openedCostume.Accepted && costumeReceipt is { ItemId: costumePack, Slot: 65 } &&
            expectedCostume.All(pair => costumeReceipt.Rewards.Any(row => row.ItemId == pair.Key && row.Count == pair.Value)),
            "选择集合对齐礼包内全部时装时发放整包，包括盒子和账号绑定物");
        foreach (var choice in capturedCostume)
            That(openedCostume.Plan!.After.Items.Any(item => item.Core.ItemId == choice.ItemId && item.Core.Durability == choice.Option &&
                item.Position.List == InventoryListType.Avatar && item.Avatar is not null),
                "时装Durability写入玩家选择 " + choice.ItemId);
        That(costumeRole.Inventory.Find(Bag(65))!.Core.Count == 1, "含时装固定礼包提交前不改源");
        That(avatarPackageService.OpenCeraPackage(costumeRole, new(65, []), out var emptyCostumeReceipt, now).Accepted &&
            emptyCostumeReceipt!.Rewards.Length == expectedCostume.Count,
            "空选择仍走固定包发放，不要求时装option");
        CeraPackageAvatarTests.Run(towns, itemResources);
        BoosterUsePlacesTests.RunResources(towns, characters, itemResources);
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
        That(InventoryRewardGrantService.TryGrantBatch(contractRole, [new(10000387, 1)], catalog,
            itemResources.GoldLimits, itemResources.EpicPieces, now) is { Accepted: true, Plan.After.Items.IsEmpty: true },
            "统一grant消费正式模板携带的契约资源事实，不限制获取来源或创建实体");
        var existingPremium = new InventoryItem(99, Bag(65), Core(10000387) with { Marker16 = 77, SortLockFlag = 1 });
        var restoredPremium = InventoryRewardGrantService.TryGrantItems(contractRole, [existingPremium], catalog,
            itemResources.GoldLimits, itemResources.EpicPieces, now);
        That(restoredPremium.Accepted && restoredPremium.Plan!.After.Items.IsEmpty &&
            restoredPremium.Plan.After.AccountWallet.ContractExpiry(97) == now.ToUnixTimeSeconds() + 86400,
            "邮件等实际接收入口将新收到的契约实例转为期限，不占实体槽");
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
                !itemResources.Contracts.TryAcquisitionContract(row.ItemId, out _) &&
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
            var expectedExpiries = expectedRewards.Where(pair => itemResources.Contracts.TryAcquisitionContract(pair.Key, out _))
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
                !itemResources.EpicPieces.ByPiece.ContainsKey(pair.Key) && !itemResources.Contracts.TryAcquisitionContract(pair.Key, out _))
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

        var resourceWalletBox = itemResources.Boosters.Get(2683799);
        That(resourceWalletBox.Available && resourceWalletBox.Selection is null && resourceWalletBox.PackageRewards.IsDefaultOrEmpty &&
            resourceWalletBox.Pools is [{ Draws: 1, Rewards: [{ ItemId: 2681762, Weight: > 0, Count: > 0 }] }],
            "匹配资源中的钱包礼盒必须具有唯一确定奖励，不能静默跳过结构变化");
        int resourceWalletCount = resourceWalletBox.Pools[0].Rewards[0].Count;
        foreach (var (box, reward, count) in new[] { (2660291, 1, 10), (10160255, 10158124, 1), (2683799, 2681762, resourceWalletCount) })
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
        current = current with { Inventory = new([new(1, Bag(3), Core(38))]) };
        int calls = random.Calls;
        That(!service.Open(current, new(3, 1), out _).Accepted && random.Calls == calls,
            "unsupported detail rejected before randomness 38");
        current = current with { Inventory = new([new(1, Bag(3), Core(762))]) };
        calls = random.Calls;
        var boundPremium = service.Open(current, new(3, 1), out var boundReceipt, now);
        That(unsupportedPremium
            ? boundPremium is { Accepted: false, Plan: null } && boundReceipt is null && random.Calls == calls
            : boundPremium.Accepted && boundReceipt is not null && boundPremium.Plan!.After.Find(Bag(3)) is null &&
                boundPremium.Plan.After.AccountWallet.PremiumContracts.Count == premiumOpen.Plan!.After.AccountWallet.PremiumContracts.Count,
            "未覆盖契约目录时也按模板绑定的实际支持情况处理762，不固定拒绝全部契约礼盒");
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
            Reject(() => repo.CommitInventory(owner, stale with { GrowType = 5 }, stalePlan), "旧开箱计划不能重绑到新首转分支");
            Query($"UPDATE characters SET grow_type=5 WHERE id={stale.Id}");
            string changedBranch = Snapshot();
            Reject(() => repo.CommitInventory(owner, stale, stalePlan), "持久化首转变化由SQL提交前资格复验拒绝");
            That(Snapshot() == changedBranch, "分支变更拒绝不消耗源或发放随机奖励");
            Query($"UPDATE characters SET grow_type={stale.GrowType} WHERE id={stale.Id}");
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
    // 保存普通奖池独立期望，继续使用既有booster注册入口。
    static void OrdinaryTenSourceResources(TownCatalog towns, ItemResources resources)
    {
        var now = DateTimeOffset.UtcNow;
        var random = new EdgeRandom();
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, random, contracts: resources.Contracts, randomOptions: resources.RandomOptions);
        (int Id, bool Unseal, BoosterGrantedItem[] Rewards)[] cases =
        [
            (10157388, true, [new(101030330, 1), new(100090160, 1), new(100140155, 1), new(100190137, 1), new(100240145, 1), new(100290146, 1), new(100310733, 1), new(100310996, 1), new(100320847, 1)]),
            (10157391, true, [new(101000323, 1), new(100090160, 1), new(100140155, 1), new(100190137, 1), new(100240145, 1), new(100290146, 1), new(100310733, 1), new(100310996, 1), new(100320847, 1)]),
            (10157392, true, [new(101000323, 1), new(100090160, 1), new(100140155, 1), new(100190137, 1), new(100240145, 1), new(100290146, 1), new(100310733, 1), new(100310996, 1), new(100320847, 1)]),
            (10157400, true, [new(101000323, 1), new(100080213, 1), new(100130206, 1), new(100180191, 1), new(100230191, 1), new(100280176, 1), new(100310733, 1), new(100310996, 1), new(100320847, 1)]),
            (10157403, true, [new(101030330, 1), new(100080213, 1), new(100130206, 1), new(100180191, 1), new(100230191, 1), new(100280176, 1), new(100310733, 1), new(100310996, 1), new(100320847, 1)]),
            (10157404, true, [new(101030330, 1), new(100080213, 1), new(100130206, 1), new(100180191, 1), new(100230191, 1), new(100280176, 1), new(100310733, 1), new(100310996, 1), new(100320847, 1)]),
            (10157417, true, [new(101030330, 1), new(100070236, 1), new(100120229, 1), new(100170220, 1), new(100220210, 1), new(100270209, 1), new(100310733, 1), new(100310996, 1), new(100320847, 1)]),
            (10306696, false, [new(10305787, 350)]),
            (10306697, false, [new(10305787, 150)]),
            (10306698, false, [new(10305787, 50)]),
        ];
        foreach (var test in cases)
        {
            var template = resources.Items.Get(test.Id);
            var definition = resources.Boosters.Get(test.Id);
            That(definition.Available && definition.Selection is null && definition.PackageRewards.IsDefaultOrEmpty &&
                !definition.Lottery && definition.FirstGrowTypes.IsEmpty && definition.CooltimeMaintenance is null &&
                definition.CoolTime == (test.Unseal ? 1000 : 0) && definition.CoolGroup is null &&
                definition.UnsealsRandomOptions == test.Unseal && definition.Pools.Length == test.Rewards.Length &&
                definition.Pools.Select((pool, at) => pool.Draws == 1 && pool.JobTag is null &&
                    pool.Rewards.SequenceEqual(new BoosterReward[] { new(test.Rewards[at].ItemId, 1000, test.Rewards[at].Count) })).All(value => value),
                "ordinary exact saved pools and root unseal declaration " + test.Id);
            That(template.Kind == ItemKind.Consumable && template.RequiredSkill == 0 &&
                service.UsePlaces(test.Id) is { PendingRule: null, Village: false, SeriaRoom: false },
                "ordinary source keeps real kind, required skill and unrestricted place declaration " + test.Id);
            if (test.Unseal)
                That(template.MinimumLevel == 1 && template.Jobs == ushort.MaxValue && template.StackLimit == 1000 &&
                    template.Expiration.Resolve(now) == 0,
                    "seven saved source qualifications remain all jobs at level one " + test.Id);
            byte level = test.Unseal ? (byte)1 : Math.Max((byte)1, template.MinimumLevel);
            byte job = test.Unseal ? (byte)0 : (byte)Enumerable.Range(0, 16).First(value => template.AllowsUse(level, (byte)value));
            int openCount = test.Unseal ? 2 : 1;
            var source = new InventoryItem(1, Bag(65), InventoryRewardGrantService.Create(template, 2, now));
            var sentinel = new InventoryItem(2, Bag(66), InventoryRewardGrantService.Create(resources.Items.Get(1106), 3, now));
            var role = new Character(1, 0, "OrdinaryTen", 0, towns.Room, towns.InitialReturn)
            {
                Job = job, Level = level, GrowType = 0,
                Inventory = new([source, sentinel], Gold: 12345) { ReviveCoins = 3, WinPoints = 7 }
            };
            That(InventoryAvatarPackageService.SourceEligible(role, source, template),
                "exact ordinary source fixture satisfies current real source qualification " + test.Id);
            string before = JsonSerializer.Serialize(role.Inventory);
            int calls = random.Calls;
            var opened = service.Open(role, new(65, openCount), out var receipt, now);
            var expected = test.Rewards.Select(row => new BoosterGrantedItem(row.ItemId, row.Count * openCount))
                .OrderBy(row => row.ItemId).ToArray();
            That(opened.Accepted && opened.Plan is not null && receipt is { Slot: 65 } && receipt.ItemId == test.Id &&
                receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(expected),
                "exact ordinary source grants every independent saved pool and full receipt " + test.Id);
            var plan = opened.Plan!;
            var outputs = plan.After.Items.Where(item => item.Position != source.Position && item.Position != sentinel.Position).ToArray();
            That(plan.Before == role.Inventory && JsonSerializer.Serialize(role.Inventory) == before &&
                plan.Reason == "booster_open" && plan.UsedItems.SequenceEqual(new ItemUseIntent[] { new(test.Id, openCount) }) &&
                plan.CharacterPrecondition is { } qualification && qualification.Matches(role, now.ToUnixTimeSeconds()) &&
                plan.OverflowItems.IsEmpty && plan.AccountRefillClaims.IsEmpty && plan.DailyItemMails.IsEmpty &&
                plan.ChangedCurrencies.IsEmpty && plan.ChangedEpicPieces.IsEmpty &&
                JsonSerializer.Serialize(plan.After with { Items = role.Inventory.Items, ConsumableStates = role.Inventory.ConsumableStates }) == before &&
                plan.After.ConsumableStates.SequenceEqual(test.Unseal
                    ? new[] { new ConsumableState(ConsumableStateKind.Cooldown, test.Id, now.ToUnixTimeMilliseconds() + 1000, 0, -test.Id) }
                    : []),
                "complete plan preserves every other asset, records exact declared cooldown and leaves input unchanged " + test.Id);
            That(plan.After.Find(sentinel.Position) == sentinel && (openCount == 2 ? plan.After.Find(source.Position) is null :
                    plan.After.Find(source.Position) == source with { Core = source.Core with { Value = 1 } }) &&
                outputs.GroupBy(item => item.Core.ItemId).Select(group => new BoosterGrantedItem(group.Key, group.Sum(item => item.Core.Count)))
                    .OrderBy(row => row.ItemId).SequenceEqual(expected) &&
                outputs.All(item => item.Uid == 0 && item.Avatar is null && item.Creature is null &&
                    ItemSlotBoundService.Allows(plan.After, item.Position, item.Core.Kind)),
                "exact output instances and source decrement leave unrelated instance intact " + test.Id);
            foreach (var item in outputs)
            {
                var rewardTemplate = resources.Items.Get(item.Core.ItemId);
                var baseline = InventoryRewardGrantService.Create(rewardTemplate, item.Core.Count, now);
                if (test.Unseal)
                {
                    var rule = resources.RandomOptions.ResolveBoosterReward(rewardTemplate);
                    var rolled = new[] { item.Core.Random0, item.Core.Random1, item.Core.Random2 };
                    That(rewardTemplate.Kind == ItemKind.Equipment && rule is not null && item.Core.Random0.Type != 0 &&
                        item.Core with { Random0 = default, Random1 = default, Random2 = default,
                            RandomChangedIndex = baseline.RandomChangedIndex } == baseline &&
                        rule.Pools.Where(pool => pool.Weight > 0).Any(pool =>
                            rolled.Take(pool.Count).Select((value, at) => pool.Groups[at].Any(row => row.Weight > 0 &&
                                new RandomOption(row.Type, row.Value1, row.Value2) == value)).All(value => value) &&
                            rolled.Take(pool.Count).Select(value => value.Type).Distinct().Count() == pool.Count &&
                            rolled.Skip(pool.Count).All(value => value == default)),
                        "exact source equipment retains full creation core and legal current random-option pool " + test.Id + ":" + item.Core.ItemId);
                    if (item.Core.ItemId is 101000323 or 101030330)
                        That(rewardTemplate.EquipmentSlot == 12 && rewardTemplate.MinimumLevel == 85 && rewardTemplate.Jobs == 6657 &&
                            rewardTemplate.Grade == 87 && rewardTemplate.Rarity == 2 && rewardTemplate.Durability == 45 &&
                            rewardTemplate.WeaponSubType == (item.Core.ItemId == 101000323 ? 0 : 3) &&
                            item.Core.Value == 999999998 && item.Core.Attr == 0 && item.Core.GenuineUpgrade == 0 &&
                            item.Core.SealFlag == 0 && item.Core.ExpireTime == 0,
                            "two saved weapon leaf fields stay distinct from source qualification and instance quality " + item.Core.ItemId);
                }
                else
                    That(rewardTemplate.Id == 10305787 && rewardTemplate.Kind == ItemKind.Quest &&
                        rewardTemplate.StackLimit == int.MaxValue && item.Core == baseline && item.Core.ExpireTime == 0,
                        "saved useful-junk reward uses complete Quest core and explicit stack-limit fallback " + test.Id);
            }
            That(test.Unseal ? random.Calls > calls : random.Calls == calls,
                "singleton reward IDs need no draw while unsealed instances still draw options " + test.Id);

            // 统一核对各源容量拒绝，持久化与回滚沿用既有测试。
            var fillerTemplate = resources.Items.Get(test.Rewards[0].ItemId);
            var range = ItemSlotBoundService.Range(fillerTemplate.Kind, role.Inventory.Expansion);
            var fillerCore = InventoryRewardGrantService.Create(fillerTemplate, fillerTemplate.StackLimit, now);
            var blocked = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.AddRange(
                Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                    new InventoryItem(100 + slot, Bag((short)slot), fillerCore))) } };
            string blockedBefore = JsonSerializer.Serialize(blocked.Inventory);
            calls = random.Calls;
            That(service.Open(blocked, new(65, openCount), out var rejected, now) is { Accepted: false, Error: 4, Plan: null } &&
                rejected is null && random.Calls == calls && JsonSerializer.Serialize(blocked.Inventory) == blockedBefore,
                "exact source full reward page rejects atomically before RNG and keeps all source and assets " + test.Id);
        }
    }

    static BoosterDefinition Parse(string pools) => BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n" + pools + "\n[/booster info]");
    // 三叶正文仅约束真实Catalog资源，不把保存路径或模板创建当作开盒成功。
    static void OrdinaryEventCoinResources(ItemResources resources)
    {
        var now = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        long[] weights = [1000, 1000, 1000, 1000, 10000, 80000, 1500, 1000, 1000, 1000, 1000,
            50, 50, 50, 50, 50, 50, 50, 50, 50, 50];
        (int SourceId, int LeafId, int SourceStackLimit)[] cases =
            [(490005633, 490005702, int.MaxValue), (490005637, 490005703, int.MaxValue), (490005632, 490005704, 1000)];
        foreach (var test in cases)
        {
            That(resources.Items.Contains(test.SourceId) && resources.Items.Contains(test.LeafId) &&
                !resources.Items.ContainsEquipment(test.SourceId) && !resources.Items.ContainsEquipment(test.LeafId),
                "活动硬币源及叶必须存在于当前真实stackable索引，不能用保存路径或预置模板替代 " + test.SourceId);
            var source = resources.Items.Get(test.SourceId);
            var leaf = resources.Items.Get(test.LeafId);
            var definition = resources.Boosters.Get(test.SourceId);
            var expectedRows = weights.Select((weight, index) => new BoosterReward(test.LeafId, weight, index + 10)).ToArray();
            That(definition.Available && definition.Selection is null && definition.PackageRewards.IsDefaultOrEmpty &&
                !definition.Lottery && !definition.NpcPocketPot && definition.FirstGrowTypes.IsEmpty && !definition.UnsealsRandomOptions &&
                definition.CustomAbilityGrade is null && !definition.SetsDisjointInstanceType && !definition.SetsEpicExchangeInstanceType &&
                definition.DailyRetainedLimit is null && definition.RuneBox is null && definition.CoolTime == 0 &&
                definition.CoolGroup is null && definition.CooltimeMaintenance is null && definition.Pools.Length == 1 &&
                definition.Pools[0] is { Draws: 1, JobTag: null, MinimumLevel: 0, MaximumLevel: int.MaxValue,
                    GrowType: -1, RequiredKind: null } pool && pool.Rewards.SequenceEqual(expectedRows),
                "三源各保留一个独立etc池及10到30的完整21条原权重行，不按同ID合并或固定发15个 " + test.SourceId);
            That(source.Kind == ItemKind.Consumable && source.StackableType == "[booster] 0" &&
                source.StackLimit == test.SourceStackLimit && source.MinimumLevel == 1 && source.MaximumLevel == int.MaxValue &&
                source.Jobs == ushort.MaxValue &&
                source.RequiredSkill == 0 && source.AttachType.Trim('`', ' ', '\r', '\n') == "[account]" &&
                source.Expiration == new ItemExpiration() && source.Expiration.Resolve(now) == 0,
                "活动硬币盒保留各自叠堆上限、账号绑定、源资格和无期限声明 " + test.SourceId);
            That(leaf.Kind == ItemKind.Material && leaf.StackableType == "[material] 0" && leaf.StackLimit == int.MaxValue &&
                leaf.MinimumLevel == 1 && leaf.MaximumLevel == int.MaxValue && leaf.Jobs == ushort.MaxValue && leaf.RequiredSkill == 0 &&
                leaf.AttachType.Trim('`', ' ', '\r', '\n') == "[account]" && leaf.Expiration == new ItemExpiration() &&
                leaf.Expiration.Resolve(now) == 0,
                "三硬币是账号绑定材料，未声明stack limit使用maxint且没有绝对、相对或周期期限 " + test.LeafId);
            foreach (int count in new[] { 10, 30 })
            {
                var core = InventoryRewardGrantService.Create(leaf, count, now);
                That(core.ItemId == test.LeafId && core.Kind == ItemKind.Material && core.Count == count &&
                    core.ExpireTime == 0 && core.SealFlag == 0,
                    "真实材料模板创建保留10与30的数量端点和无期限核心，不等于开盒或扣材成功 " + test.LeafId);
            }
        }
    }

    static void OrdinaryTalmaResources(TownCatalog towns, ItemResources resources)
    {
        var now = DateTimeOffset.UtcNow;
        var random = new EdgeRandom();
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, random, contracts: resources.Contracts, randomOptions: resources.RandomOptions);
        (int SourceId, int Count)[] cases = [(490005948, 40), (490005949, 50), (490005950, 70)];
        That(resources.Items.Contains(10161538) && !resources.Items.ContainsEquipment(10161538),
            "塔尔玛材料必须来自当前真实stackable索引，不以pending或预置模板补齐");
        var leaf = resources.Items.Get(10161538);
        That(leaf.Kind == ItemKind.Material && leaf.StackableType == "[material] 0" &&
            leaf.Grade == 1 && leaf.Rarity == 3 && leaf.MinimumLevel == 1 && leaf.MaximumLevel == int.MaxValue &&
            leaf.Jobs == ushort.MaxValue && leaf.RequiredSkill == 0 && leaf.StackLimit == int.MaxValue &&
            leaf.Durability == 0 && leaf.Weight == 0 && leaf.AttachType.Trim('`', ' ', '\r', '\n') == "[trade]" &&
            leaf.Expiration == new ItemExpiration() && leaf.Expiration.Resolve(now) == 0 &&
            !leaf.UseForbidden && !leaf.IsPremiumService && !leaf.HasAcquisitionEffect &&
            CurrencyWallet.RewardKind(10161538) is null && !resources.EpicPieces.ByPiece.ContainsKey(10161538),
            "真实塔尔玛叶是可交易材料，缺省叠maxint/无期限并走物理入包，不继承源账号绑定或90级资格");
        foreach (var test in cases)
        {
            var template = resources.Items.Get(test.SourceId);
            var definition = resources.Boosters.Get(test.SourceId);
            That(definition.Available && definition.Selection is null && definition.PackageRewards.IsDefaultOrEmpty &&
                !definition.Lottery && !definition.NpcPocketPot && definition.FirstGrowTypes.IsEmpty &&
                !definition.UnsealsRandomOptions && definition.CustomAbilityGrade is null &&
                !definition.SetsDisjointInstanceType && !definition.SetsEpicExchangeInstanceType &&
                definition.DailyRetainedLimit is null && definition.RuneBox is null &&
                definition.CoolTime == 1000 && definition.CoolGroup is null && definition.CooltimeMaintenance is null &&
                definition.Pools.Length == 1 && definition.Pools[0] is
                    { Draws: 1, JobTag: null, MinimumLevel: 0, MaximumLevel: int.MaxValue, GrowType: -1, RequiredKind: null } pool &&
                pool.Rewards.SequenceEqual(new BoosterReward[] { new(10161538, 1000, test.Count) }),
                "三源各保留独立etc单池draw1和原权重1000及40/50/70数量，不套用21行活动硬币池 " + test.SourceId);
            That(template.Kind == ItemKind.Consumable && template.StackableType == "[booster] 0" &&
                template.Grade == 1 && template.Rarity == 3 && template.MinimumLevel == 90 &&
                template.MaximumLevel == int.MaxValue && template.Jobs == ushort.MaxValue &&
                template.RequiredSkill == 0 && template.StackLimit == 1000 && !template.UseForbidden &&
                template.AttachType.Trim('`', ' ', '\r', '\n') == "[account]" &&
                template.Expiration == new ItemExpiration() && template.Expiration.Resolve(now) == 0 &&
                service.UsePlaces(test.SourceId) is { PendingRule: null, Village: false, SeriaRoom: false },
                "塔尔玛源保留90级全职业账号绑定、叠1000及无期限/地点限制的真实资格 " + test.SourceId);
            foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
            {
                var source = new InventoryItem(1, Bag(65), InventoryRewardGrantService.Create(template, 2, now));
                var sentinel = new InventoryItem(2, Bag(66), InventoryRewardGrantService.Create(resources.Items.Get(1106), 3, now));
                var role = new Character(1, 0, "Talma", 0, towns.Room, towns.InitialReturn)
                {
                    Job = job, Level = 90, GrowType = 0,
                    Inventory = new([source, sentinel], Gold: 12345) { ReviveCoins = 3, WinPoints = 7 }
                };
                That(InventoryAvatarPackageService.SourceEligible(role, source, template),
                    "90级每职业满足塔尔玛源真实资格 " + test.SourceId + ":" + job);
                string before = JsonSerializer.Serialize(role.Inventory);
                int calls = random.Calls;
                var opened = service.Open(role, new(65, 1), out var receipt, now);
                That(opened.Accepted && opened.Plan is not null && receipt is { Slot: 65 } && receipt.ItemId == test.SourceId &&
                    receipt.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(10161538, test.Count) }),
                    "原booster入口只开一盒，回执完整发40/50/70个塔尔玛材料 " + test.SourceId + ":" + job);
                var plan = opened.Plan!;
                var output = plan.After.Items.Where(item => item.Position != source.Position && item.Position != sentinel.Position).ToArray();
                That(plan.Before == role.Inventory && JsonSerializer.Serialize(role.Inventory) == before &&
                    plan.Reason == "booster_open" && plan.UsedItems.SequenceEqual(new ItemUseIntent[] { new(test.SourceId, 1) }) &&
                    plan.CharacterPrecondition is { } qualification && qualification.Matches(role, now.ToUnixTimeSeconds()) &&
                    plan.OverflowItems.IsEmpty && plan.AccountRefillClaims.IsEmpty && plan.DailyItemMails.IsEmpty &&
                    plan.ChangedCurrencies.IsEmpty && plan.ChangedEpicPieces.IsEmpty &&
                    JsonSerializer.Serialize(plan.After with { Items = role.Inventory.Items, ConsumableStates = role.Inventory.ConsumableStates }) == before &&
                    plan.After.ConsumableStates.SequenceEqual(new[] { new ConsumableState(ConsumableStateKind.Cooldown,
                        test.SourceId, now.ToUnixTimeMilliseconds() + 1000, 0, -test.SourceId) }) && random.Calls == calls,
                    "单确定行无需随机，完整计划保留非物品资产及原输入快照 " + test.SourceId + ":" + job);
                That(plan.After.Find(source.Position) == source with { Core = source.Core with { Value = 1 } } &&
                    plan.After.Find(sentinel.Position) == sentinel && output.Length == 1 && output[0].Uid == 0 &&
                    output[0].Core == new ItemCore(10161538, ItemKind.Material, test.Count) &&
                    output[0].Avatar is null && output[0].Creature is null &&
                    ItemSlotBoundService.Allows(plan.After, output[0].Position, ItemKind.Material),
                    "源精确扣1余1，奖励进入真实材料分页且不改无关实例 " + test.SourceId + ":" + job);
                if (job != 0) continue;
                void RejectOpen(Character blocked, int count, ushort error, string boundary)
                {
                    string rejectedBefore = JsonSerializer.Serialize(blocked.Inventory);
                    int rejectedCalls = random.Calls;
                    That(service.Open(blocked, new(65, count), out var rejected, now) is
                        { Accepted: false, Plan: null } failed && failed.Error == error && rejected is null &&
                        random.Calls == rejectedCalls && JsonSerializer.Serialize(blocked.Inventory) == rejectedBefore,
                        "塔尔玛源拒绝保全原源/材料/资产且无RNG：" + boundary + ":" + test.SourceId);
                }
                RejectOpen(role with { Level = 89 }, 1, 19, "低于90级");
                RejectOpen(role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(source,
                    source with { Core = source.Core with { EquipmentLockId = 1 } }) } }, 1, 19, "安全锁");
                RejectOpen(role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(source,
                    source with { Core = source.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } }) } }, 1, 19, "精确到期");
                RejectOpen(role, 3, 19, "请求数量超过源2盒");
                var range = ItemSlotBoundService.Range(ItemKind.Material, role.Inventory.Expansion);
                var full = InventoryRewardGrantService.Create(leaf, leaf.StackLimit, now);
                var blockedPage = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.AddRange(
                    Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                        new InventoryItem(100 + slot, Bag((short)slot), full))) } };
                RejectOpen(blockedPage, 1, 4, "真实材料页全满且已有堆达上限");
            }
        }
    }

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
