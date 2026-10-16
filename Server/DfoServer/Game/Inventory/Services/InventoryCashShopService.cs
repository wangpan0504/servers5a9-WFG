using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Inventory.Services;

// 商品、扣点券及资源明确声明的契约或实物奖励在同一个库存计划提交。
internal sealed partial class InventoryCashShopService(ItemCatalog catalog, CashShopCatalog shop, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    GameDebugSettings? debugSettings = null, PremiumContractCatalog? contracts = null, AvatarPackageCatalog? avatars = null, DevilContractService? devil = null)
{
    readonly GameDebugSettings debug = debugSettings ?? new();
    // 仅当前服务实例的调试开关；每次购买冻结一次状态，不改变其它发奖入口。
    public bool IgnoreAbsoluteExpiration
    {
        get => debug.IgnoreCashShopAbsoluteExpiration;
        set => debug.IgnoreCashShopAbsoluteExpiration = value;
    }
    public bool IgnoreUsablePeriod
    {
        get => debug.IgnoreCashShopUsablePeriod;
        set => debug.IgnoreCashShopUsablePeriod = value;
    }

    public InventoryResult Buy(Character character, CashShopPurchaseRequest request, out CashShopPurchaseReceipt? receipt,
        DateTimeOffset? utcNow = null)
    {
        receipt = null;
        bool shopExpiration = IgnoreAbsoluteExpiration;
        bool ignoreUsablePeriod = IgnoreUsablePeriod;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (request.CurrencyMode != 0 || request.GiftMode != 0 || request.Items.Count is < 1 or > 64 ||
            request.PaymentMode != request.Items[0].PaymentMode)
            return InventoryResult.Reject(17);

        var products = new List<CashShopProduct>(request.Items.Count);
        var rewards = new List<InventoryReward>(request.Items.Count);
        var avatarRewards = new List<InventoryItem>();
        var bonuses = new Dictionary<int, IReadOnlyList<CashShopBonus>>();
        var before = character.Inventory;
        var paid = before;
        ushort avatarExpansion = before.AvatarExpansion;
        long total = 0, gold = 0, winPoints = 0;
        long? deadline = null;
        InventoryItem? nameTag = null;
        ushort characterSlots = before.AccountWallet.CharacterSlotCapacity;
        ushort cargoCapacity = before.CargoCapacity;
        var serviceWallet = before.AccountWallet;
        var serviceInventory = before;
        foreach (var requested in request.Items)
        {
            var product = shop.Product(requested.CommodityId);
            if (product is null || product.Count <= 0 ||
                requested.FirstOptionCount != 0 || requested.SecondOptionCount != 0 || requested.RelatedItemId != 0 || requested.RelatedSlot != 0)
                return InventoryResult.Reject(17);
            bool devilProduct = product.Section is "selectable character premium" or "charac premium package";
            if (devilProduct)
            {
                int[]? slots = product.Section == "selectable character premium" ? [product.ContractType - 580] :
                    devil?.Catalog.PackageSlots.GetValueOrDefault(product.ItemId);
                if (slots is null || slots.Length == 0 || slots.Any(slot => slot is < 0 or > 7) || product.DurationDays <= 0 ||
                    slots.Contains(2) && devil is null) return InventoryResult.Reject(17);
                foreach (int slot in slots)
                {
                    bool active = DevilContractPolicy.Expiry(serviceWallet, slot) > now.ToUnixTimeSeconds();
                    if (slot == 1) serviceInventory = DevilContractPolicy.RenewLottery(serviceInventory, now, active);
                    if (slot == 0 && serviceWallet.ContractExpiry(58) > serviceWallet.ContractExpiry(580))
                        serviceWallet = serviceWallet with { Contracts = serviceWallet.PremiumContracts.SetItem(580, serviceWallet.ContractExpiry(58)) };
                    try { serviceWallet = serviceWallet.ActivateContract(580 + slot, (long)product.DurationDays * 86400, now); }
                    catch (OverflowException) { return InventoryResult.Reject(17); }
                    if (serviceWallet.ContractExpiry(580 + slot) > int.MaxValue) return InventoryResult.Reject(17);
                }
            }
            else if (!AllowsReward(product.ItemId)) return InventoryResult.Reject(17);

            if (product.Section == "avatar")
            {
                if (!TryAvatar(character, requested, product, paid, now, shopExpiration, ignoreUsablePeriod, out var avatar, out var type,
                    out var next, out var sourceDeadline)) return InventoryResult.Reject(17);
                paid = next!;
                avatarRewards.Add(avatar!);
                Deadline(avatar!.ExpireTime);
                Deadline(sourceDeadline);
                bool coupon = requested.PaymentMode == 1;
                total += coupon ? 0 : type!.Cera;
                gold += coupon ? 0 : type!.Gold;
                winPoints += coupon ? 0 : type!.WinPoints;
                product = product with { CeraPrice = coupon ? 0 : type!.Cera, DurationDays = type!.Days };
            }
            else
            {
                if (requested.PaymentMode != 0 || product.CeraPrice < 0) return InventoryResult.Reject(17);
                total += product.CeraPrice;
                var template = !devilProduct && CurrencyWallet.RewardKind(product.ItemId) is null && !pieces.ByPiece.ContainsKey(product.ItemId) &&
                    contracts?.Recognizes(product.ItemId) != true ? catalog.Get(product.ItemId) : null;
                if (template is { CharacterSlotMaximum: > 0 })
                {
                    if (product.Count != 1 || characterSlots >= Math.Min(62, template.CharacterSlotMaximum))
                        return InventoryResult.Reject(17);
                    characterSlots++;
                }
                else if (template is { AvatarExpansionStage: > 0 })
                {
                    if (product.Count != 1 || template.AvatarExpansionStage != avatarExpansion / 7 + 1 ||
                        template.AvatarExpansionStage > 45 || !template.AllowsUse(character.Level, character.Job))
                        return InventoryResult.Reject(17);
                    avatarExpansion = checked((ushort)(template.AvatarExpansionStage * 7));
                }
                else if (template is { PersonalCargoUpgradeTarget: > 0 })
                {
                    if (product.Count != 1 || template.PersonalCargoUpgradeTarget <= cargoCapacity || template.PersonalCargoUpgradeTarget > 264 ||
                        !template.AllowsUse(character.Level, character.Job) || !InventoryMoveService.RequiredSkillSatisfied(character, template))
                        return InventoryResult.Reject(17);
                    cargoCapacity = template.PersonalCargoUpgradeTarget;
                }
                else if (product.Section == "visual")
                {
                    var visualTemplate = catalog.Get(product.ItemId);
                    if (nameTag is not null || visualTemplate is not { Kind: ItemKind.Equipment, EquipmentSlot: 30 } ||
                        product.Count != 1 || product.DurationDays <= 0 || !visualTemplate.AllowsUse(character.Level, character.Job) ||
                        !InventoryMoveService.RequiredSkillSatisfied(character, visualTemplate))
                        return InventoryResult.Reject(17);
                    var position = new InventorySlot(InventoryListType.Equipment, 30);
                    var old = character.Inventory.Find(position);
                    // 同款延长期限，换款覆盖；商品期限不受普通过期商品调试开关影响。
                    long start = old?.Core.ItemId == product.ItemId ? Math.Max(now.ToUnixTimeSeconds(), old.ExpireTime) : now.ToUnixTimeSeconds();
                    long expires = start + (long)product.DurationDays * 86400;
                    if (expires > int.MaxValue)
                        return InventoryResult.Reject(17);
                    nameTag = old?.Core.ItemId == product.ItemId ? old with { Core = old.Core with { ExpireTime = (int)expires } } :
                        InventoryRewardGrantService.CreateItem(visualTemplate, position,
                            InventoryRewardGrantService.Create(visualTemplate, utcNow: now) with { ExpireTime = (int)expires });
                }

                else if (!devilProduct) rewards.Add(new(product.ItemId, product.Count));
            }
            if (total > int.MaxValue || gold > int.MaxValue || winPoints > int.MaxValue)
                return InventoryResult.Reject(17);
            products.Add(product);

            var productBonuses = shop.Bonus(product.CommodityId);
            if (productBonuses.Count == 0)
                continue;
            bonuses[product.CommodityId] = productBonuses;
            foreach (var bonus in productBonuses)
            {
                if (!AllowsReward(bonus.ItemId)) return InventoryResult.Reject(17);
                rewards.Add(new(bonus.ItemId, bonus.Count));
            }
        }

        foreach (var (kind, amount) in new[] { (CurrencyKind.Cera, total), (CurrencyKind.Gold, gold), (CurrencyKind.WinPoint, winPoints) })
        {
            var spent = CurrencyService.Spend(paid, kind, checked((int)amount));
            if (!spent.Accepted) return InventoryResult.Reject(10);
            paid = spent.Plan!.After;
        }
        paid = paid with { AvatarExpansion = avatarExpansion };
        // 主商品和附赠按真实货币归属合计；付费购买不能沿普通发奖的上限截断规则少发奖励。
        if (!CanGrantCurrencies(paid, rewards, new CurrencyService(goldLimits).CarryLimit(character)))
            return InventoryResult.Reject(17);
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, rewards,
            catalog, goldLimits, pieces, now, shopExpiration: shopExpiration, contracts: contracts, allowOverflowMail: true,
            ignoreUsablePeriod: ignoreUsablePeriod);
        if (!grant.Accepted)
            return grant;
        var overflow = grant.Plan!.OverflowItems;
        if (avatarRewards.Count != 0)
        {
            grant = InventoryRewardGrantService.TryGrantItems(character with { Inventory = grant.Plan.After }, avatarRewards,
                catalog, goldLimits, pieces, now, allowOverflowMail: true);
            if (!grant.Accepted) return grant;
            overflow = overflow.AddRange(grant.Plan!.OverflowItems);
        }

        var after = grant.Plan!.After with { DevilUses = serviceInventory.DevilUses };
        // 只合并本次魔王子项，保留普通商品附赠激活的其他契约。
        foreach (var contract in serviceWallet.PremiumContracts.Where(pair => DevilContractPolicy.IsService(pair.Key)))
            after = after with { AccountWallet = after.AccountWallet with { Contracts = after.AccountWallet.PremiumContracts.SetItem(contract.Key, contract.Value) } };
        if (devil is not null && DevilContractService.NeedsGift(after, now))
        {
            var gift = devil.DailyGift(character with { Inventory = after }, now);
            if (!gift.Accepted) return gift;
            after = gift.Plan!.After;
            overflow = overflow.AddRange(gift.Plan.OverflowItems);
        }
        if (nameTag is not null)
            after = after with { Items = after.Items.Where(item => item.Position != nameTag.Position).Append(nameTag).ToImmutableArray() };
        after = after with { AccountWallet = after.AccountWallet with { CharacterSlotCapacity = characterSlots } };
        receipt = new(products.ToImmutableArray(), bonuses);
        return new(true, 0, new(before, after with { CargoCapacity = cargoCapacity }, "cash_shop_buy", new(character, deadline), overflow));

        void Deadline(long? value)
        {
            if (value > 0 && (deadline is null || value < deadline)) deadline = value;
        }

        bool AllowsReward(int itemId)
        {
            if (CurrencyWallet.RewardKind(itemId) is not null || pieces.ByPiece.ContainsKey(itemId)) return true;
            if (contracts?.Recognizes(itemId) == true) return contracts.TryAcquisitionContract(itemId, out _);
            try
            {
                var template = catalog.Get(itemId);
                return !template.IsPremiumService || template.AcquisitionContract is not null;
            }
            catch (InvalidDataException) { return false; }
        }
    }

    internal static bool CanGrantCurrencies(InventorySnapshot inventory, IReadOnlyList<InventoryReward> rewards, int goldLimit)
    {
        foreach (var group in rewards.GroupBy(reward => CurrencyWallet.RewardKind(reward.ItemId)))
        {
            if (group.Key is not { } kind)
                continue;
            long limit = kind == CurrencyKind.Gold ? goldLimit : int.MaxValue;
            if (CurrencyWallet.Get(inventory, kind) + group.Sum(reward => (long)reward.Count) > limit)
                return false;
        }
        return true;
    }
}
