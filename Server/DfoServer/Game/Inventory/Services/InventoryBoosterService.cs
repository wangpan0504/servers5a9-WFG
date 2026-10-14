using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryBoosterService(ItemCatalog catalog, BoosterCatalog boosters, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    Random? random = null, GameDebugSettings? debugSettings = null, PremiumContractCatalog? contracts = null)
{
    public InventoryResult Open(Character character, BoosterOpenRequest request, out BoosterReceipt? receipt, DateTimeOffset? utcNow = null)
        => OpenCore(character, request, out receipt, utcNow, ceraPackage: false);

    public InventoryResult OpenCeraPackage(Character character, CeraPackageOpenRequest request, out BoosterReceipt? receipt,
        DateTimeOffset? utcNow = null)
    {
        receipt = null;
        // 非零列表是独立的时装选择合同；在资格和属性未实现前保留整包，不能忽略玩家选择。
        if (request.Choices.Length != 0)
            return InventoryResult.Reject(19);
        return OpenCore(character, new(request.Slot, 1), out receipt, utcNow, ceraPackage: true);
    }

    InventoryResult OpenCore(Character character, BoosterOpenRequest request, out BoosterReceipt? receipt,
        DateTimeOffset? utcNow, bool ceraPackage)
    {
        receipt = null;
        bool shopExpiration = debugSettings?.IgnoreCashShopAbsoluteExpiration == true;
        var before = character.Inventory;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var source = before.Find(new(InventoryListType.Main, request.Slot));
        if (source is null || !source.Core.IsStackable || request.Count <= 0 || request.Count > source.Core.Count ||
            source.Core.EquipmentLockId != 0 || source.Core.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(19);
        var definition = boosters.Get(source.Core.ItemId);
        if (!definition.Available || ceraPackage != !definition.PackageRewards.IsDefaultOrEmpty)
            return InventoryResult.Reject(19);
        BoosterChoice[]? choices = null;
        if (definition.Selection is { } selection)
        {
            if (request.Selection is null || !TrySelect(selection, request.Selection, out choices))
                return InventoryResult.Reject(19);
        }
        else if (request.Selection is not null)
        {
            return InventoryResult.Reject(19);
        }

        // 随机池预检全部有权重的候选，选择型仅预检实际所选奖励，不过滤候选或改换玩家选择。
        try
        {
            var ids = choices is not null ? choices.Select(row => row.ItemId) : ceraPackage
                ? definition.PackageRewards.Select(row => row.ItemId)
                : definition.Pools.SelectMany(pool => pool.Rewards).Where(row => row.Weight > 0).Select(row => row.ItemId);
            foreach (int id in ids.Distinct())
            {
                if (CurrencyWallet.RewardKind(id) is not null || pieces.ByPiece.ContainsKey(id))
                    continue;
                if (contracts?.TryAcquisitionContract(id, out _) == true || catalog.Get(id).AcquisitionContract is not null)
                    continue;
                // 仅已实现的普通契约可直接写入账号期限；其他premium服务可能还有即时奖励，
                // 必须拒绝整包，不能只记期限，也不能跳过实体模板与期限的预检。
                if (contracts?.Recognizes(id) == true || boosters.DirectActivationItems.Contains(id))
                    return InventoryResult.Reject(19);
                var template = catalog.Get(id);
                if (template.IsPremiumService) return InventoryResult.Reject(19);
                if (InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: shopExpiration).IsExpired(now.ToUnixTimeSeconds()))
                    return InventoryResult.Reject(19);
            }
            if (choices is not null && choices.Any(row => (row.Upgrade != 0 || row.Refinement != 0) &&
                (CurrencyWallet.RewardKind(row.ItemId) is not null || pieces.ByPiece.ContainsKey(row.ItemId) ||
                    catalog.Get(row.ItemId).Kind != ItemKind.Equipment)))
                return InventoryResult.Reject(19);
        }
        catch (InvalidDataException)
        {
            return InventoryResult.Reject(19);
        }

        var totals = new Dictionary<int, int>();
        try
        {
            if (choices is not null)
                foreach (var choice in choices)
                    Add(choice.ItemId, choice.Count, request.Count);
            foreach (var pool in definition.Pools)
            {
                var candidates = pool.Rewards.Where(row => row.Weight > 0).ToArray();
                long draws = (long)pool.Draws * request.Count;
                if (candidates.Length == 1)
                {
                    Add(candidates[0].ItemId, candidates[0].Count, draws);
                    continue;
                }
                long weight = candidates.Sum(row => row.Weight);
                // 每次至少产出一件；超过回执行数与每行数量的共同上限时，不存在可提交结果。
                if (draws > (long)BoosterReceipt.MaximumItems * int.MaxValue)
                    return InventoryResult.Reject(4);
                if (draws > 64)
                {
                    // 独立加权抽取的次数向量服从多项分布；按条件二项分布聚合，避免按开箱数量循环。
                    // 保留每一资源行（含同编号不同数量）、原权重与奖池，不过滤或重抽不合适的结果。
                    foreach (var row in candidates)
                    {
                        long hits = BoosterBatchRandom.Next(draws, row.Weight, weight, random ?? Random.Shared);
                        if (hits != 0)
                            Add(row.ItemId, row.Count, hits);
                        draws -= hits;
                        weight -= row.Weight;
                    }
                    continue;
                }
                for (long draw = 0; draw < draws; draw++)
                {
                    long value = (random ?? Random.Shared).NextInt64(weight);
                    foreach (var row in candidates)
                    {
                        if (value < row.Weight)
                        {
                            Add(row.ItemId, row.Count, 1);
                            break;
                        }
                        value -= row.Weight;
                    }
                }
            }
            if (!definition.PackageRewards.IsDefaultOrEmpty)
                foreach (var row in definition.PackageRewards)
                    Add(row.ItemId, row.Count, request.Count);
        }
        catch (OverflowException)
        {
            return InventoryResult.Reject(4);
        }
        if (totals.Count is 0 or > BoosterReceipt.MaximumItems)
            return InventoryResult.Reject(4);
        // 标准消耗释放源格后再规划所有奖励；全包、钱包超限等失败均保留整批礼包。
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, request.Count).Plan!.After;
        foreach (var rewards in totals.GroupBy(pair => CurrencyWallet.RewardKind(pair.Key)))
        {
            if (rewards.Key is not { } kind)
                continue;
            int limit = kind == CurrencyKind.Gold ? new CurrencyService(goldLimits).CarryLimit(character) : int.MaxValue;
            if (CurrencyWallet.Get(paid, kind) + rewards.Sum(pair => (long)pair.Value) > limit)
                return InventoryResult.Reject(4);
        }
        var grants = new List<InventoryReward>();
        if (choices is not null)
        {
            if (choices.Any(row => row.Upgrade == 255))
            {
                // 按实际装备页容量预检，避免为不可能容纳的批量产物逐件抽取；快捷栏不改变自动入包落点。
                var range = ItemSlotBoundService.Range(ItemKind.Equipment, paid.Expansion);
                int occupied = paid.Items.Count(item => item.Position.List == InventoryListType.Main &&
                    item.Position.Index >= range.Start && item.Position.Index <= range.End);
                long equipmentCount = totals.Where(pair => CurrencyWallet.RewardKind(pair.Key) is null &&
                    !pieces.ByPiece.ContainsKey(pair.Key) && catalog.Get(pair.Key).Kind == ItemKind.Equipment)
                    .Sum(pair => (long)pair.Value);
                if (equipmentCount > range.End - range.Start + 1 - occupied)
                    return InventoryResult.Reject(4);
            }
            foreach (var choice in choices)
            {
                int count = checked(choice.Count * request.Count);
                if (choice.Upgrade != 255)
                {
                    grants.Add(new(choice.ItemId, count, choice.Upgrade, choice.Refinement));
                    continue;
                }
                long weight = choice.UpgradeChances.Sum(row => (long)row.Weight);
                for (int i = 0; i < count; i++)
                {
                    long value = (random ?? Random.Shared).NextInt64(weight);
                    foreach (var chance in choice.UpgradeChances)
                    {
                        if (value < chance.Weight)
                        {
                            grants.Add(new(choice.ItemId, 1, chance.Level, choice.Refinement));
                            break;
                        }
                        value -= chance.Weight;
                    }
                }
            }
        }
        else
            grants.AddRange(totals.Select(pair => new InventoryReward(pair.Key, pair.Value)));
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, grants, catalog, goldLimits, pieces, now,
            shopExpiration: shopExpiration, contracts: contracts, allowOverflowMail: !definition.PackageRewards.IsDefaultOrEmpty);
        if (!grant.Accepted)
            return InventoryResult.Reject(grant.Error == 19 ? (ushort)19 : (ushort)4);
        receipt = new(source.Core.ItemId, source.Position.Index, totals.Select(pair => new BoosterGrantedItem(pair.Key, pair.Value)).ToArray());
        return new(true, 0, new(before, grant.Plan!.After, "booster_open", overflowItems: grant.Plan.OverflowItems));

        void Add(int id, int count, long draws) => totals[id] = checked(totals.GetValueOrDefault(id) + checked((int)(count * draws)));
    }

    static bool TrySelect(BoosterSelectionDefinition definition, BoosterSelectionRequest request, out BoosterChoice[] rewards)
    {
        rewards = [];
        var category = definition.Categories.FirstOrDefault(row => row.Category == request.Category && row.Subcategory == request.Subcategory);
        if (category is null || category.PendingRule is not null || category.Rewards.IsEmpty)
            return false;
        if (definition.SelectionCount == 0)
        {
            // 客户端在全领模式回显该分类的完整清单，重复资源行也必须逐项计入。
            if (!request.ItemIds.Order().SequenceEqual(category.Rewards.Select(row => row.ItemId).Order()))
                return false;
            rewards = category.Rewards.ToArray();
            return true;
        }
        if (request.ItemIds.Length == 0 || request.ItemIds.Length > definition.SelectionCount ||
            request.ItemIds.Distinct().Count() != request.ItemIds.Length)
            return false;
        var selected = new List<BoosterChoice>();
        foreach (int id in request.ItemIds)
        {
            var entries = category.Rewards.Where(row => row.ItemId == id).ToArray();
            if (entries.Length == 0 || entries.Any(row => row.Count != entries[0].Count || row.Upgrade != entries[0].Upgrade ||
                row.Refinement != entries[0].Refinement || !row.UpgradeChances.SequenceEqual(entries[0].UpgradeChances)))
                return false;
            selected.Add(entries[0]);
        }
        rewards = selected.ToArray();
        return true;
    }
}
