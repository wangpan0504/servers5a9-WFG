using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;

namespace DfoServer.Testing.Inventory;

internal static class EpicExchangeTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { failed = true; }
            That(failed, name);
        }
        var catalog = itemResources.Items;
        var exchange = new EpicExchangeService(itemResources.EpicExchanges, catalog, itemResources.EpicPieces, itemResources.GoldLimits, itemResources.Boosters);
        var now = DateTimeOffset.UtcNow;

        // 资源缺失由业务层拒绝，自测这里只区分“能解析”和“不能解析”。
        ItemTemplate? Template(int itemId)
        {
            try { return catalog.Get(itemId); }
            catch (Exception e) when (e is InvalidDataException or IOException) { return null; }
        }

        var config = itemResources.EpicExchanges;
        That(config.RequiredItem == new ItemMaterialCost(10158124, 5) && config.BaseGold == 5_000_000 &&
            config.Scale.SequenceEqual(new[] { 1f, 1.2f, 1.5f, 2f }), "matched etc keeps required item, base gold and four cost scales");
        That(config.Groups.Select(group => group.RewardItemIds.Length).SequenceEqual(new[] { 25, 18, 1 }) &&
            config.Groups[0].RewardItemIds[0] == 100050817 && config.Groups[2].RewardItemIds.Single() == 10308715,
            "matched etc keeps three reward groups with original ids");
        That(config.Groups.Select(group => group.ExchangeItem).SequenceEqual(new ItemMaterialCost[]
            { new(10306750, 48), new(10308559, 95), new(10308559, 114) }), "matched etc keeps every [exchange item] row");
        That(config.Rewards.Count == 44 && config.Groups.All(group => group.RewardItemIds.All(config.IsReward)),
            "reward union has 44 ids and every group member is exchange-eligible");
        That(config.Rewards.All(itemId => Template(itemId) is not null), "every etc reward id resolves to a template");
        // etc 第三组是武器自选盒（10308715，stackable），其余43个编号都是装备。
        That(config.Rewards.Count(itemId => Template(itemId)!.Kind == ItemKind.Equipment) == 43 &&
            Template(10308715) is { Kind: not ItemKind.Equipment } &&
            config.Groups[2].RewardItemIds.SequenceEqual(new[] { 10308715 }),
            "reward union is 43 equipment ids plus the stackable weapon selection box");
        That(config.TryCost(0, out int cost0) && cost0 == 5_000_000 && config.TryCost(1, out int cost1) && cost1 == 6_000_000 &&
            config.TryCost(2, out int cost2) && cost2 == 7_000_000 && config.TryCost(3, out int cost3) && cost3 == 10_000_000,
            "cost table is 5M/6M/7M/10M for exchange counts 0..3");
        // 反汇编顺序：float 乘系数后先截成整数 7,500,000，再整除 1,000,000 丢余数，所以第三档是 7M 不是 7.5M。
        That((int)(5_000_000f * 1.5f) == 7_500_000 && config.TryCost(2, out int boundary) && boundary == 7_000_000,
            "7.5M boundary truncates to 7M after integer division");
        That(!config.TryCost(4, out _) && !config.TryCost(-1, out _), "cost rejects exchange counts outside 0..3");

        var source = 100050817;
        var target = 100250681;
        var current = new Character(1, 0, "EpicExchange", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        // 10158124 是账号级史诗灵魂（CurrencyKind.EpicSoul），由账号钱包扣除，不是背包实体。
        var souls = AccountWallet.Empty.Set(CurrencyKind.EpicSoul, 5);
        var sourceItem = new InventoryItem(1, Bag(9), InventoryRewardGrantService.Create(catalog.Get(source)) with { Extension119 = 0x01 });
        var funded = current with { Inventory = new([sourceItem], Gold: 5_000_000) { AccountWallet = souls } };

        var result = exchange.Exchange(funded, source, target, 9, now);
        var granted = result.Plan?.After.Items.SingleOrDefault(item => item.Core.ItemId == target);
        That(result.Accepted && result.Plan!.After.Gold == 0 &&
            result.Plan.After.AccountWallet.Get(CurrencyKind.EpicSoul) == 0 &&
            result.Plan.After.Items.All(item => item.Core.ItemId != source),
            "success spends gold, souls and source in one plan");
        That(granted is { Core.ItemId: var grantedTarget, Core.Count: 1, Core.Extension119: 0x03,
                Core.Attr: 0, Core.EnchantCardId: 0, Core.AmplifyType: 0, Core.GenuineUpgrade: 0 } &&
            grantedTarget == target,
            "target is one fresh instance with exchange count 1 and no inherited upgrades");
        var plan = result.Plan!;
        var restrictedSource = sourceItem with { Core = sourceItem.Core with { DisjointForbidden = true, DestructionSoulForbidden = true } };
        var restricted = funded with { Inventory = funded.Inventory with { Items = [restrictedSource] } };
        var restrictedExchange = exchange.Exchange(restricted, source, target, 9, now);
        That(restrictedExchange.Accepted && restrictedExchange.Plan!.After.Items.Single().Core is
            { DisjointForbidden: true, DestructionSoulForbidden: true, Extension119: 3 },
            "休亚交换重建装备时保留实例限制，不能通过更换模板解除限制");
        That(plan.Reason == "epic_exchange" && plan.ChangedSlots.Length > 0, "plan reports epic exchange and changed slots");

        // 同编号目标等于重掷品级：客户端按同稀有度装备开放，服务端不再拒绝。
        var reroll = exchange.Exchange(funded, source, source, 9, now);
        That(reroll.Accepted && reroll.Plan!.After.Items.Count(item => item.Core.ItemId == source) == 1 &&
            reroll.Plan.After.Items.Single(item => item.Core.ItemId == source).Core.Extension119 == 0x03,
            "same item id is accepted as a quality reroll with the exchange count bumped");

        // 实机样本：客户端候选里出现不在 etc 奖励并集内的同稀有度装备（魅惑的低语是魔法石，与巨剑同为 rarity 4）。
        const int magicStone = 100353081;
        var exotic = exchange.Exchange(funded, source, magicStone, 9, now);
        That(!config.IsReward(magicStone) && catalog.Get(magicStone) is { Kind: ItemKind.Equipment, Rarity: 4 } &&
            exotic.Accepted && exotic.Plan!.After.Items.Any(item => item.Core.ItemId == magicStone),
            "same-rarity equipment outside the etc reward union is a valid target");

        var third = new InventoryItem(3, Bag(9), InventoryRewardGrantService.Create(catalog.Get(source)) with { Attr = 5, Extension119 = 0x07 });
        var thirdRun = exchange.Exchange(current with
            { Inventory = new([third], Gold: 10_000_000) { AccountWallet = souls } }, source, target, 9, now);
        var thirdGrant = thirdRun.Plan?.After.Items.SingleOrDefault(item => item.Core.ItemId == target);
        That(thirdRun.Accepted && thirdRun.Plan!.After.Gold == 0 &&
            thirdRun.Plan.After.AccountWallet.Get(CurrencyKind.EpicSoul) == 0 && thirdGrant is { Core.Extension119: 0x09, Core.Attr: 0 },
            "count 3 costs 10M and produces count 4 with a zeroed core");

        // etc 第三组的武器自选盒（10308715）是 stackable，属于 190307_preyraid 交换窗；本命令只收同稀有度装备。
        var boxTemplate = catalog.Get(10308715);
        That(boxTemplate.Kind != ItemKind.Equipment && boxTemplate.Rarity == catalog.Get(source).Rarity,
            "weapon selection box shares the source rarity but is not equipment");
        That(exchange.Exchange(funded, source, 10308715, 9, now) is { Accepted: false, Error: 4, Plan: null },
            "stackable box is rejected as an exchange target even at the same rarity");
        var boxOnly = current with
        {
            Inventory = new([new InventoryItem(5, Bag(65), new ItemCore(10308715, boxTemplate.Kind, 1))], Gold: 5_000_000)
        };
        That(exchange.Exchange(boxOnly, 10308715, source, 65, now) is { Accepted: false, Error: 4, Plan: null },
            "selection box cannot be used as an exchange source");

        // 来源资格只看实例位：泰波尔斯成品（休亚直购、礼盒产物、设计图升级）都不在 etc 奖励表里。
        const int tayberrs = 100130610;
        var layman = new InventoryItem(7, Bag(9), InventoryRewardGrantService.Create(catalog.Get(tayberrs)) with { Extension119 = 0x01 });
        var laymanRun = exchange.Exchange(
            current with { Inventory = new([layman], Gold: 5_000_000) { AccountWallet = souls } }, tayberrs, target, 9, now);
        That(!config.IsReward(tayberrs) && laymanRun.Accepted &&
            laymanRun.Plan!.After.Items.Any(item => item.Core.ItemId == target && item.Core.Extension119 == 0x03),
            "source eligibility comes from the instance bit, not the reward union");
        foreach (byte bits in new byte[] { 0x00, 0x06 })
        {
            var unstamped = current with
            {
                Inventory = new([sourceItem with { Core = sourceItem.Core with { Extension119 = bits } }], Gold: 5_000_000)
                    { AccountWallet = souls }
            };
            That(exchange.Exchange(unstamped, source, target, 9, now) is { Accepted: false, Error: 4, Plan: null },
                "source without bit0 is rejected even when the count bits are set: " + bits);
        }

        var exhausted = current with
        {
            Inventory = new([sourceItem with { Core = sourceItem.Core with { Extension119 = 0x09 } }], Gold: 1_000_000_000)
                { AccountWallet = souls }
        };
        That(exchange.Exchange(exhausted, source, target, 9, now) is { Accepted: false, Error: 4, Plan: null },
            "count 4 is rejected as exhausted");

        var pair = current with
        {
            Inventory = new([
                sourceItem with { Position = Bag(20) },
                sourceItem with { Position = Bag(9), Uid = 4 }], Gold: 5_000_000) { AccountWallet = souls }
        };
        // 请求槽位优先：消费槽位20，槽位9的同编号实例原样保留。
        var picked = exchange.Exchange(pair, source, target, 20, now);
        That(picked.Accepted &&
            picked.Plan!.After.Find(Bag(9)) == (sourceItem with { Position = Bag(9), Uid = 4 }) &&
            picked.Plan.After.Items.Count(item => item.Core.ItemId == source) == 1 &&
            picked.Plan.After.Items.Count(item => item.Core.ItemId == target) == 1,
            "requested source slot wins over the lower duplicate slot");
        // 槽位不可用（老客户端送 -1）时退回主背包最小槽位。
        var fallback = exchange.Exchange(pair, source, target, -1, now);
        That(fallback.Accepted &&
            fallback.Plan!.After.Find(Bag(20)) == (sourceItem with { Position = Bag(20) }) &&
            fallback.Plan.After.Items.Count(item => item.Core.ItemId == source) == 1,
            "unusable source slot falls back to the lowest main slot");

        var others = new (string Name, Character Input, int From, int To, int Slot, ushort Error)[]
        {
            ("target of another rarity", funded, source, 10400, 9, 4),
            ("source id not in the main bag", funded, 10400, target, 9, 4),
            ("unknown ids", funded, -1, target, 9, 4),
            ("gold shortage", funded with { Inventory = funded.Inventory with { Gold = 4_999_999 } }, source, target, 9, 10),
            ("soul shortage", funded with
            {
                Inventory = funded.Inventory with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.EpicSoul, 4) }
            }, source, target, 9, 4),
            ("locked source", funded with
            {
                Inventory = funded.Inventory with { Items = funded.Inventory.Items.Replace(sourceItem,
                    sourceItem with { Core = sourceItem.Core with { EquipmentLockId = 1 } }) }
            }, source, target, 9, 4),
            ("expired source", funded with
            {
                Inventory = funded.Inventory with { Items = funded.Inventory.Items.Replace(sourceItem,
                    sourceItem with { Core = sourceItem.Core with { ExpireTime = 1 } }) }
            }, source, target, 9, 4),
            ("stored source", funded with
            {
                Inventory = funded.Inventory with { Items = funded.Inventory.Items.Replace(sourceItem,
                    sourceItem with { Position = new(InventoryListType.PersonalCargo, 0) }) }
            }, source, target, 9, 4),
            ("worn source", funded with
            {
                Inventory = funded.Inventory with { Items = funded.Inventory.Items.Replace(sourceItem,
                    sourceItem with { Position = new(InventoryListType.Equipment, 0) }) }
            }, source, target, 9, 4)
        };
        foreach (var (name, input, from, to, slot, error) in others)
        {
            var rejected = exchange.Exchange(input, from, to, slot, now);
            That(rejected is { Accepted: false, Error: var actual, Plan: null } && actual == error,
                "reject keeps every asset: " + name);
        }

        var full = current with
        {
            Inventory = new([.. Enumerable.Range(9, 56).Select(slot =>
                new InventoryItem(100 + slot, Bag((short)slot), InventoryRewardGrantService.Create(catalog.Get(10400)))),
                sourceItem with { Position = Bag(3) }], Gold: 5_000_000) { AccountWallet = souls }
        };
        That(exchange.Exchange(full, source, target, 3, now) is { Accepted: false, Error: 17 },
            "full equipment page rejects the grant instead of dropping the reward");

        That(EpicExchangeRequests.Exchange([0x12, 0x00, 0x00, 0x00, 0x81, 0xa7, 0xf6, 0x05, 0x39, 0xb4, 0xf9, 0x05]) ==
            new EpicExchangeRequest(18, source, target), "request keeps the main-bag slot plus source and target ids");
        That(EpicExchangeRequests.Exchange([0xff, 0xff, 0xff, 0xff, 0x81, 0xa7, 0xf6, 0x05, 0x39, 0xb4, 0xf9, 0x05]) ==
            new EpicExchangeRequest(-1, source, target), "legacy -1 slot still parses and falls back to the lowest slot");
        Reject(() => EpicExchangeRequests.Exchange(new byte[11]), "truncated exchange request rejected");
        Reject(() => EpicExchangeRequests.Exchange(new byte[13]), "trailing exchange bytes rejected");
        That(EpicExchangePackets.Exchanged(new(true, 0)).SequenceEqual(new byte[] { 1 }) &&
            EpicExchangePackets.Exchanged(InventoryResult.Reject(10)).SequenceEqual(new byte[] { 0, 10, 0 }),
            "reply is success byte and u16 failure code");

        // CMD 1572 (REWARD_EPIC_EXCHANGE)：三组 [reward epic item] -> [exchange item] 与礼盒可选武器交换。
        var group1Source = new InventoryItem(11, Bag(9), InventoryRewardGrantService.Create(catalog.Get(100050817)));
        var group1Reward = exchange.ExchangeReward(current with { Inventory = new([group1Source]) }, 100050817, 1, 0, now);
        That(group1Reward.Accepted && group1Reward.Plan!.Reason == "reward_epic_exchange" &&
            group1Reward.Plan.After.Items.All(item => item.Core.ItemId != 100050817) &&
            group1Reward.Plan.After.AccountWallet.Material(10306750).Balance +
                group1Reward.Plan.After.Items.Where(item => item.Core.ItemId == 10306750).Sum(item => item.Core.Count) == 48,
            "group 1 reward epic exchanges for 48 of 10306750");

        var group2Source = new InventoryItem(12, Bag(9), InventoryRewardGrantService.Create(catalog.Get(config.Groups[1].RewardItemIds[0])));
        var group2Material = exchange.ExchangeReward(current with { Inventory = new([group2Source]) }, config.Groups[1].RewardItemIds[0], 1, 10308559, now);
        That(group2Material.Accepted &&
            group2Material.Plan!.After.AccountWallet.Material(10308559).Balance +
                group2Material.Plan.After.Items.Where(item => item.Core.ItemId == 10308559).Sum(item => item.Core.Count) == 95,
            "group 2 reward epic exchanges for 95 of 10308559");

        var group3Box = new InventoryItem(13, Bag(65), new ItemCore(10308715, boxTemplate.Kind, 2));
        var group3Material = exchange.ExchangeReward(current with { Inventory = new([group3Box]) }, 10308715, 1, 10308559, now);
        That(group3Material.Accepted &&
            group3Material.Plan!.After.Find(Bag(65))?.Core.Count == 1 &&
            group3Material.Plan.After.AccountWallet.Material(10308559).Balance +
                group3Material.Plan.After.Items.Where(item => item.Core.ItemId == 10308559).Sum(item => item.Core.Count) == 114,
            "group 3 weapon box consumes 1 unit and grants 114 of 10308559");

        int selectableWeapon = itemResources.Boosters.Get(10308715).Selection!.Categories.SelectMany(c => c.Rewards).First().ItemId;
        var group3Weapon = exchange.ExchangeReward(current with { Inventory = new([group3Box]) }, 10308715, 0, selectableWeapon, now);
        That(group3Weapon.Accepted &&
            group3Weapon.Plan!.After.Find(Bag(65))?.Core.Count == 1 &&
            group3Weapon.Plan.After.Items.Any(item => item.Core.ItemId == selectableWeapon),
            "group 3 weapon box mode 0 exchanges for a selectable weapon from the box");

        That(EpicExchangeRequests.RewardExchange([0x81, 0xa7, 0xf6, 0x05, 0x01, 0xaf, 0x45, 0x9d, 0x00]) ==
            new RewardEpicExchangeRequest(100050817, 1, 10306991),
            "reward exchange request parses 9-byte sourceItemId, mode and targetItemId");
        Reject(() => EpicExchangeRequests.RewardExchange(new byte[8]), "truncated reward exchange request rejected");

        var compoundReceipt = new MonsterCardBindReceipt(65, 0, 1, 2, true, [66, 67], [new(66, 10001, 1, 0)]);
        var compoundPacket = MonsterCardBindPackets.CompoundSuccess(compoundReceipt);
        That(compoundPacket.Length == 21 && compoundPacket[0] == 1 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(compoundPacket.AsSpan(5)) == 10001 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(compoundPacket.AsSpan(13)) == 1,
            "CMD 1122 compound success packet is 21 bytes with reward item id and rarity upgraded flag");

        var cargoService = new InventoryCargoService(catalog, itemResources.Cargo, itemResources.GoldLimits);
        var freeTemplate = catalog.Get(3037);
        var accountItem = new InventoryItem(14, Bag(169), new ItemCore(3037, freeTemplate.Kind, 2));
        var withAccountCargo = current with
        {
            Inventory = new([accountItem]) { AccountCargo = new(0, 8) }
        };
        var movedCargo = cargoService.MoveToAccountCargo(withAccountCargo, InventoryListType.Main, 169, 1);
        That(movedCargo.Accepted &&
            movedCargo.Plan!.After.Find(Bag(169))?.Core.Count == 1 &&
            movedCargo.Plan.After.Find(new(InventoryListType.AccountCargo, 0))?.Core.Count == 1,
            "CMD 935 MoveToAccountCargo moves requested count into AccountCargo");

        // 配置夹具只覆盖模板契约，不改运行 PVF。
        var fixture = new EpicExchangeCatalog(BaseFixture);
        That(fixture.Groups.Length == 2 && fixture.Rewards.Count == 3 && fixture.TryCost(2, out int fixtureCost) && fixtureCost == 7_000_000,
            "fixture etc parses repeated blocks without touching the running PVF");
        Reject(() => new EpicExchangeCatalog(MissingExchangeRow), "missing [exchange item] group is rejected instead of defaulting");
        Reject(() => new EpicExchangeCatalog(UnsortedScale), "unsorted cost scale index is rejected");
        Reject(() => new EpicExchangeCatalog(IncompleteRequiredItem), "incomplete required item is rejected");

        Console.WriteLine($"PASS {Count - checks} epic exchange assertions");
    }

    const string BaseFixture = """
        [required item]
        10158124 5
        [require default gold]
        5000000
        [cost scale]
        1 1.00 2 1.20 3 1.50 4 2.00
        [/cost scale]
        [reward epic item]
        1 100050817 100250681
        [/reward epic item]
        [exchange item]
        1 10306750 48
        [/exchange item]
        [reward epic item]
        2 100301034
        [/reward epic item]
        [exchange item]
        2 10308559 95
        [/exchange item]
        """;

    const string MissingExchangeRow = """
        [required item]
        10158124 5
        [require default gold]
        5000000
        [cost scale]
        1 1.00
        [/cost scale]
        [reward epic item]
        1 100050817
        [/reward epic item]
        """;

    const string UnsortedScale = """
        [required item]
        10158124 5
        [require default gold]
        5000000
        [cost scale]
        2 1.00 3 1.20
        [/cost scale]
        [reward epic item]
        1 100050817
        [/reward epic item]
        [exchange item]
        1 10306750 48
        [/exchange item]
        """;

    const string IncompleteRequiredItem = """
        [required item]
        10158124
        [require default gold]
        5000000
        [cost scale]
        1 1.00
        [/cost scale]
        [reward epic item]
        1 100050817
        [/reward epic item]
        [exchange item]
        1 10306750 48
        [/exchange item]
        """;

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
