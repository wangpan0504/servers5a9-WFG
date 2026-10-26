using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryBoosterService(ItemCatalog catalog, BoosterCatalog boosters, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    Random? random = null, GameDebugSettings? debugSettings = null, PremiumContractCatalog? contracts = null,
    AvatarPackageCatalog? avatars = null, TimeProvider? clock = null, RandomOptionCatalog? randomOptions = null,
    IndependentAttributePairCatalog? attributePairs = null)
{
    public BoosterUsePlacesDefinition UsePlaces(int itemId) => boosters.UsePlaces(itemId);
    public InventoryResult Open(Character character, BoosterOpenRequest request, out BoosterReceipt? receipt, DateTimeOffset? utcNow = null)
        => OpenCore(character, request, out receipt, utcNow, ceraPackage: false);

    public (int Total, int Matching) CountMatchingPools(Character character, int itemId)
    {
        var definition = boosters.Get(itemId);
        return (definition.Pools.Length, definition.Pools.Count(pool => pool.Matches(character.Job, character.GrowType, character.Level)));
    }

    public InventoryResult OpenLottery(Character character, LotteryOpenRequest request, out LotteryReceipt? receipt,
        DateTimeOffset? utcNow = null)
    {
        receipt = null;
        if (request.Mode != 0 || request.Slot < 0)
            return InventoryResult.Reject(19);
        var source = character.Inventory.Find(new(InventoryListType.Main, request.Slot));
        if (source is not null && boosters.Get(source.Core.ItemId).RuneBox is not null)
            return OpenRuneBox(character, request, source, utcNow ?? (clock ?? TimeProvider.System).GetUtcNow(), out receipt);
        var result = OpenCore(character, new(request.Slot, 1), out var opened, utcNow, ceraPackage: false, lottery: true);
        if (!result.Accepted)
            return result;
        var reward = opened!.Rewards.Single();
        var plan = result.Plan!;
        var destinations = plan.After.Items.Where(item => item.Core.ItemId == reward.ItemId &&
            item.Core.Count - PriorCount(item) > 0).ToArray();
        // ACK27展示首件；全部实例通过既有NOTI14差量同步，双倍装备不能挤进同一个UID。
        if (destinations.Length == 0 || destinations.Any(item => item.Position.List != InventoryListType.Main) ||
            destinations.Sum(item => item.Core.Count - PriorCount(item)) != reward.Count)
            return InventoryResult.Reject(4);
        receipt = new(request.Slot, destinations[0].Position, reward.ItemId, destinations[0].Core.Count - PriorCount(destinations[0]));
        return result with { Plan = new(plan.Before, plan.After, "lottery_open", plan.CharacterPrecondition) };

        int PriorCount(InventoryItem item)
        {
            var prior = plan.BeforeIndex.Find(item.Position);
            int count = prior?.Core.ItemId == reward.ItemId ? prior.Core.Count : 0;
            return count - (item.Position == new InventorySlot(InventoryListType.Main, request.Slot) &&
                prior?.Core.ItemId == reward.ItemId ? 1 : 0);
        }
    }

    // [rune random box] 与袖珍罐共用 CMD27：源物品在普通背包，产物按符文/护石分类进入容器46。
    // 概率取 etc/runesystem.etc 的 [random choose info]：先按本职业权重决定本次是否只在本职业可用产物里抽，
    // 再按品级权重抽档、在对应品级池里等概率取一件；本职业在该品级没有产物时退回全体池，不制造固定失败。
    InventoryResult OpenRuneBox(Character character, LotteryOpenRequest request, InventoryItem source,
        DateTimeOffset now, out LotteryReceipt? receipt)
    {
        receipt = null;
        var definition = boosters.Get(source.Core.ItemId);
        if (!definition.Available || definition.RuneBox is not { } box || !source.Core.IsStackable ||
            source.Core.EquipmentLockId != 0 || source.Core.IsExpired(now.ToUnixTimeSeconds()) ||
            !definition.AllowsFirstGrowType(character.GrowType))
            return RejectRuneBox("rune_box_source");
        if (HasCooldown(character.Inventory, source.Core.ItemId, definition, now))
            return InventoryResult.Reject(19);
        var draw = random ?? Random.Shared;
        RuneRandomChooseRow choose;
        try
        {
            if (!catalog.RuneRandomChoices().TryGet(box.Family, box.Tier, out choose))
                return RejectRuneBox("rune_box_table");
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return RejectRuneBox("rune_box_resource");
        }
        int productId;
        try
        {
            if (RuneBoxSelection.Select(choose,
                    grade => box.Family == 0 ? catalog.RuneCandidates((byte)grade) : catalog.TalismanCandidates(grade),
                    id => box.Family == 0
                        ? catalog.Rune(id)?.AllowsUse(character.Level, character.Job, character.GrowType) == true
                        : catalog.Talisman(id)?.AllowsUse(character.Level, character.Job, character.GrowType) == true,
                    draw.NextInt64, out _, out productId) is { } reason)
                return RejectRuneBox(reason);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return RejectRuneBox("rune_box_resource");
        }

        var before = character.Inventory;
        var deleted = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, 1);
        if (!deleted.Accepted)
            return RejectRuneBox("rune_box_source");
        var paid = deleted.Plan!.After;
        var granted = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid },
            [new InventoryReward(productId, 1)], catalog, goldLimits, pieces, now);
        if (!granted.Accepted)
            return InventoryResult.Reject(granted.Error == 19 ? (ushort)19 : (ushort)4) with { RejectReason = "rune_box_capacity" };
        var after = granted.Plan!.After;
        // 产物可能并入容器46已有堆叠，按合并前后数量差定位，不能只找新出现的实例。
        var prior = paid.Items.Where(item => item.Core.ItemId == productId)
            .ToDictionary(item => item.Position, item => item.Core.Count);
        var destinations = after.Items.Where(item => item.Core.ItemId == productId &&
            item.Core.Count - (prior.TryGetValue(item.Position, out int old) ? old : 0) > 0).ToArray();
        if (destinations.Length != 1 || destinations[0].Position.List != InventoryListType.Talisman)
            return RejectRuneBox("rune_box_product");
        var destination = destinations[0];
        var cooled = ActivateCooldown(after, catalog.Get(source.Core.ItemId), definition, now);
        if (cooled is null) return InventoryResult.Reject(19);
        after = cooled;
        receipt = new(request.Slot, destination.Position, productId,
            destination.Core.Count - (prior.TryGetValue(destination.Position, out int priorCount) ? priorCount : 0));
        return new(true, 0, new InventoryPlan(before, after, "rune_box_open",
            new InventoryCharacterPrecondition(character, source.ExpireTime > 0 ? source.ExpireTime : null, clock, requirePosition: true)));

        static InventoryResult RejectRuneBox(string reason) => InventoryResult.Reject(19) with { RejectReason = reason };
    }

    public InventoryResult OpenCeraPackage(Character character, CeraPackageOpenRequest request, out BoosterReceipt? receipt,
        DateTimeOffset? utcNow = null)
    {
        receipt = null;
        if (request.Choices.Length == 0)
            return OpenCore(character, new(request.Slot, 1), out receipt, utcNow, ceraPackage: true);
        return OpenCeraPackageAvatars(character, request, out receipt, utcNow);
    }

    InventoryResult OpenCeraPackageAvatars(Character character, CeraPackageOpenRequest request, out BoosterReceipt? receipt,
        DateTimeOffset? utcNow)
    {
        receipt = null;
        bool shopExpiration = debugSettings?.IgnoreCashShopAbsoluteExpiration == true;
        bool ignoreUsablePeriod = debugSettings?.IgnoreCashShopUsablePeriod == true;
        var before = character.Inventory;
        var now = utcNow ?? (clock ?? TimeProvider.System).GetUtcNow();
        var source = before.Find(new(InventoryListType.Main, request.Slot));
        if (source is null || !source.Core.IsStackable || source.Core.Count < 1 ||
            source.Core.EquipmentLockId != 0 || source.Core.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(19);
        ItemTemplate sourceTemplate;
        try
        {
            sourceTemplate = catalog.Get(source.Core.ItemId);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return InventoryResult.Reject(19) with { RejectReason = "cera_package_source_resource_invalid" };
        }
        if (!InventoryAvatarPackageService.SourceEligible(character, source, sourceTemplate))
            return InventoryResult.Reject(19) with { RejectReason = "cera_package_source_ineligible" };
        var definition = boosters.Get(source.Core.ItemId);
        if (!definition.Available || !definition.AllowsFirstGrowType(character.GrowType) || definition.PackageRewards.IsDefaultOrEmpty || avatars is null)
            return InventoryResult.Reject(19) with { RejectReason = "cera_package_avatar_unconfigured" };
        if (HasCooldown(before, source.Core.ItemId, definition, now))
            return InventoryResult.Reject(19);
        HashSet<int> avatarIds;
        try
        {
            avatarIds = InventoryCeraPackageAvatarRewards.AvatarIds(definition.PackageRewards, catalog);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return InventoryResult.Reject(19) with { RejectReason = "cera_package_avatar_resource_invalid" };
        }
        if (!InventoryCeraPackageAvatarRewards.Matches(avatarIds, request.Choices))
            return InventoryResult.Reject(19) with { RejectReason = "cera_package_avatar_choice_mismatch" };
        long? rewardDeadline = null;
        try
        {
            foreach (int id in definition.PackageRewards.Select(row => row.ItemId).Distinct())
            {
                if (CurrencyWallet.RewardKind(id) is not null || pieces.ByPiece.ContainsKey(id))
                    continue;
                if (contracts?.TryAcquisitionContract(id, out _) == true || catalog.Get(id).AcquisitionContract is not null)
                    continue;
                if (contracts?.Recognizes(id) == true || boosters.DirectActivationItems.Contains(id))
                    return InventoryResult.Reject(19);
                var template = catalog.Get(id);
                if (template.IsPremiumService)
                    return InventoryResult.Reject(19);
                if (template.Kind != ItemKind.Avatar)
                {
                    var core = InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: shopExpiration,
                        ignoreUsablePeriod: ignoreUsablePeriod);
                    if (core.IsExpired(now.ToUnixTimeSeconds()))
                        return InventoryResult.Reject(19);
                    if (core.ExpireTime > 0 && (rewardDeadline is null || core.ExpireTime < rewardDeadline.Value))
                        rewardDeadline = core.ExpireTime;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            return InventoryResult.Reject(19);
        }
        if (!InventoryCeraPackageAvatarRewards.TryCreate(catalog, avatars, definition.PackageRewards, request.Choices, now,
            shopExpiration, ignoreUsablePeriod, out var avatarItems, out string? reason))
            return InventoryResult.Reject(19) with { RejectReason = reason };
        long? deadline = InventoryAvatarPackageService.EarliestDeadline(source, avatarItems);
        if (rewardDeadline is not null && (deadline is null || rewardDeadline < deadline))
            deadline = rewardDeadline;
        var totals = new Dictionary<int, int>();
        try
        {
            foreach (var row in definition.PackageRewards)
                totals[row.ItemId] = checked(totals.GetValueOrDefault(row.ItemId) + row.Count);
        }
        catch (OverflowException)
        {
            return InventoryResult.Reject(4);
        }
        if (totals.Count is 0 or > BoosterReceipt.MaximumItems)
            return InventoryResult.Reject(4);
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, 1).Plan!.After;
        foreach (var rewards in totals.GroupBy(pair => CurrencyWallet.RewardKind(pair.Key)))
        {
            if (rewards.Key is not { } kind)
                continue;
            int limit = kind == CurrencyKind.Gold ? new CurrencyService(goldLimits).CarryLimit(character) : int.MaxValue;
            if (CurrencyWallet.Get(paid, kind) + rewards.Sum(pair => (long)pair.Value) > limit)
                return InventoryResult.Reject(4);
        }
        var avatarGrant = InventoryRewardGrantService.TryGrantItems(character with { Inventory = paid }, avatarItems, catalog,
            goldLimits, pieces, now, allowOverflowMail: true);
        if (!avatarGrant.Accepted)
            return InventoryResult.Reject(avatarGrant.Error == 19 ? (ushort)19 : (ushort)4);
        var rest = totals.Where(pair => catalog.Get(pair.Key).Kind != ItemKind.Avatar)
            .Select(pair => new InventoryReward(pair.Key, pair.Value)).ToArray();
        var after = avatarGrant.Plan!.After;
        IReadOnlyList<InventoryItem> overflow = avatarGrant.Plan.OverflowItems;
        if (rest.Length != 0)
        {
            var restGrant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = after }, rest, catalog,
                goldLimits, pieces, now, shopExpiration: shopExpiration, contracts: contracts, allowOverflowMail: true,
                ignoreUsablePeriod: ignoreUsablePeriod);
            if (!restGrant.Accepted)
                return InventoryResult.Reject(restGrant.Error == 19 ? (ushort)19 : (ushort)4);
            after = restGrant.Plan!.After;
            overflow = [.. overflow, .. restGrant.Plan.OverflowItems];
        }
        var cooled = ActivateCooldown(after, sourceTemplate, definition, now);
        if (cooled is null) return InventoryResult.Reject(19);
        after = cooled;
        receipt = new(source.Core.ItemId, source.Position.Index, totals.Select(pair => new BoosterGrantedItem(pair.Key, pair.Value)).ToArray());
        return new(true, 0, new(before, after, "booster_open",
            new InventoryCharacterPrecondition(character, deadline, clock, requirePosition: true), overflow));
    }

    InventoryResult OpenCore(Character character, BoosterOpenRequest request, out BoosterReceipt? receipt,
        DateTimeOffset? utcNow, bool ceraPackage, bool lottery = false)
    {
        receipt = null;
        bool shopExpiration = debugSettings?.IgnoreCashShopAbsoluteExpiration == true;
        bool ignoreUsablePeriod = debugSettings?.IgnoreCashShopUsablePeriod == true;
        var before = character.Inventory;
        var now = utcNow ?? (clock ?? TimeProvider.System).GetUtcNow();
        var source = before.Find(new(InventoryListType.Main, request.Slot));
        if (source is null || !source.Core.IsStackable || request.Count <= 0 || request.Count > source.Core.Count ||
            source.Core.EquipmentLockId != 0 || source.Core.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(19);
        var sourceTemplate = catalog.Get(source.Core.ItemId);
        if (!InventoryAvatarPackageService.SourceEligible(character, source, sourceTemplate))
            return InventoryResult.Reject(19) with { RejectReason = "booster_source_ineligible" };
        var definition = boosters.Get(source.Core.ItemId);
        if (!definition.Available || !definition.AllowsFirstGrowType(character.GrowType) ||
            ceraPackage != !definition.PackageRewards.IsDefaultOrEmpty || lottery != definition.Lottery)
            return InventoryResult.Reject(19);
        if (HasCooldown(before, source.Core.ItemId, definition, now))
            return InventoryResult.Reject(19);
        var selectionPolicy = (attributePairs ?? IndependentAttributePairCatalog.RequiredOnly)
            .Resolve(source.Core.ItemId, AttributePairEntryKind.Booster);
        if (selectionPolicy.Kind == AttributePairResolutionKind.RequiredUnavailable)
            return InventoryResult.Reject(19) with { RejectReason = "attribute_pair_policy_unavailable" };
        if (selectionPolicy.Policy is { } boundedPolicy && request.Count > boundedPolicy.MaximumBeads)
            return InventoryResult.Reject(4);
        var dailyUses = before.DailyBoosterUses;
        long? dailyDeadline = null;
        if (definition.DailyRetainedLimit is { } dailyLimit)
        {
            if (sourceTemplate.Id != source.Core.ItemId || sourceTemplate.Kind != source.Core.Kind ||
                !sourceTemplate.AllowsUse(character.Level, character.Job) ||
                !InventoryMoveService.RequiredSkillSatisfied(character, sourceTemplate) ||
                !DailyBoosterUsage.TryConsume(before.DailyBoosterUses, source.Core.ItemId, request.Count, dailyLimit, now, out dailyUses))
                return InventoryResult.Reject(19) with { RejectReason = "daily_retained_source_or_quota_ineligible" };
            dailyDeadline = DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds();
            if (source.ExpireTime > 0) dailyDeadline = Math.Min(dailyDeadline.Value, source.ExpireTime);
        }
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

        var pools = definition.Pools.Where(pool => pool.Matches(character.Job, character.GrowType, character.Level)).ToArray();
        if (pools.Length == 0 && definition.Pools.Length != 0)
            return InventoryResult.Reject(19) with { RejectReason = "unmatched_grow_type_pool" };

        var unsealDefinitions = new Dictionary<int, RandomOptionDefinition>();
        // 随机池预检匹配池的有权候选，选择型仅预检实际所选奖励；转职池不预检其他职业行。
        try
        {
            // 明确类型的随机池先检查全部有效候选，不能靠抽中项校验或钱包/契约快捷入口绕过。
            foreach (var pool in pools.Where(pool => pool.RequiredKind is not null))
                foreach (var row in pool.Rewards.Where(row => row.Weight > 0))
                {
                    var template = catalog.Get(row.ItemId);
                    if (template.Kind != pool.RequiredKind || CurrencyWallet.RewardKind(row.ItemId) is not null ||
                        pieces.ByPiece.ContainsKey(row.ItemId) || template.AcquisitionContract is not null ||
                        template.IsPremiumService || contracts?.Recognizes(row.ItemId) == true ||
                        boosters.DirectActivationItems.Contains(row.ItemId))
                        return InventoryResult.Reject(19);
                }
            if (choices is not null && choices.Any(row => row.RequiredKind is { } kind && catalog.Get(row.ItemId).Kind != kind))
                return InventoryResult.Reject(19);
            var ids = choices is not null ? choices.Select(row => row.ItemId) : ceraPackage
                ? definition.PackageRewards.Select(row => row.ItemId)
                : pools.SelectMany(pool => pool.Rewards).Where(row => row.Weight > 0).Select(row => row.ItemId);
            if (definition.UnsealsRandomOptions)
            {
                // 先核对整份声明，不能因本次恰好选中普通奖励而吞掉混合盒的未知解封语义。
                var allIds = definition.Selection is { } selectionDefinition
                    ? selectionDefinition.Categories.SelectMany(group => group.Rewards).Select(row => row.ItemId)
                    : definition.Pools.SelectMany(pool => pool.Rewards).Where(row => row.Weight > 0).Select(row => row.ItemId);
                foreach (int id in allIds.Distinct())
                {
                    var rule = randomOptions?.ResolveBoosterReward(catalog.Get(id));
                    if (rule is null) return InventoryResult.Reject(19);
                    unsealDefinitions.Add(id, rule);
                }
            }
            foreach (int id in ids.Distinct())
            {
                if (choices?.Any(row => row.ItemId == id && row.NativeCreatureSelectionRow) == true)
                {
                    // 选择行的[creature]只有编号和数量；实体类型由真实模板决定，特殊owner不可走快捷发奖。
                    var selectedTemplate = catalog.Get(id);
                    if (CurrencyWallet.RewardKind(id) is not null || pieces.ByPiece.ContainsKey(id) ||
                        selectedTemplate.AcquisitionContract is not null || selectedTemplate.IsPremiumService ||
                        contracts?.Recognizes(id) == true || boosters.DirectActivationItems.Contains(id))
                        return InventoryResult.Reject(19);
                    if (selectedTemplate.Kind == ItemKind.Avatar)
                    {
                        var avatar = avatars?.GetAvatar(id);
                        if (avatar is null || avatar.PendingRule is not null || !avatar.Types.IsEmpty ||
                            avatar.RequiresAbilityChoice || !avatar.Abilities.Contains(0))
                            return InventoryResult.Reject(19);
                    }
                }
                // 该声明只约束领取到的实例是装备；本服没有实例级“不可分解”字段，官方同款装备本身可分解，
                // 因此不按模板 DisjointForbidden 收窄，也不猜测实例标志。
                if ((definition.SetsDisjointInstanceType || definition.SetsEpicExchangeInstanceType) &&
                    catalog.Get(id).Kind != ItemKind.Equipment)
                    return InventoryResult.Reject(19);
                if (lottery && (CurrencyWallet.RewardKind(id) is not null || pieces.ByPiece.ContainsKey(id) ||
                    contracts?.Recognizes(id) == true || boosters.DirectActivationItems.Contains(id) ||
                    catalog.Get(id).AcquisitionContract is not null ||
                    ItemSlotBoundService.List(catalog.Get(id).Kind) != InventoryListType.Main))
                    return InventoryResult.Reject(19);
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
                if (InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: shopExpiration,
                    ignoreUsablePeriod: ignoreUsablePeriod).IsExpired(now.ToUnixTimeSeconds()))
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
        byte[]? trace = null;
        try
        {
            if (selectionPolicy.Policy is { } policy)
            {
                trace = new byte[request.Count];
                for (int i = 0; i < request.Count; i++)
                {
                    var sample = IndependentAttributePairSampler.Next(policy, (random ?? Random.Shared).NextInt64);
                    trace[i] = sample.Ordinal;
                    Add(sample.ItemId, 1, 1);
                }
            }
            else
            {
                if (choices is not null)
                    foreach (var choice in choices)
                        Add(choice.ItemId, choice.Count, request.Count);
                foreach (var pool in pools)
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
        }
        catch (OverflowException)
        {
            return InventoryResult.Reject(4);
        }
        catch (InvalidDataException) when (selectionPolicy.Policy is not null)
        {
            return InventoryResult.Reject(19) with { RejectReason = "attribute_pair_random_invalid" };
        }
        if (totals.Count is 0 or > BoosterReceipt.MaximumItems)
            return InventoryResult.Reject(4);
        // 标准消耗释放源格后再规划所有奖励；全包、钱包超限等失败均保留整批礼包。
        var paid = definition.DailyRetainedLimit is not null ? before :
            InventoryDeleteService.Plan(before, source.Position, source.Core.Value, request.Count).Plan!.After;
        if (lottery && boosters.AllowsDevilDouble(source.Core.ItemId) &&
            DevilContractPolicy.TryConsume(paid, 1, DevilContractPolicy.LotteryAllowance(paid, now), now, out var doubled))
        {
            // 同一随机结果双倍发放；额度、源物品、全部奖励与UID一起原子提交。
            try { foreach (int id in totals.Keys.ToArray()) totals[id] = checked(totals[id] * 2); }
            catch (OverflowException) { return InventoryResult.Reject(4); }
            paid = doubled;
        }
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
        InventoryResult grant;
        if (definition.UnsealsRandomOptions || definition.GrantsEpicExchangeOrigin)
        {
            var range = ItemSlotBoundService.Range(ItemKind.Equipment, paid.Expansion);
            int occupied = paid.Items.Count(item => item.Position.List == InventoryListType.Main &&
                item.Position.Index >= range.Start && item.Position.Index <= range.End);
            if (grants.Sum(row => (long)row.Count) > range.End - range.Start + 1 - occupied)
                return InventoryResult.Reject(4);
            var instances = new List<InventoryItem>();
            try
            {
                foreach (var reward in grants)
                    for (int index = 0; index < reward.Count; index++)
                    {
                        var core = InventoryRewardGrantService.Create(catalog.Get(reward.ItemId), utcNow: now,
                            upgrade: reward.Upgrade, refinement: reward.Refinement, shopExpiration: shopExpiration,
                            ignoreUsablePeriod: ignoreUsablePeriod);
                        // 同ID多件仍逐件创建，保留强化、锻造、封装、耐久及期限。
                        if (definition.UnsealsRandomOptions)
                            core = InventoryRandomOptionService.Initialize(core, unsealDefinitions[reward.ItemId],
                                (random ?? Random.Shared).NextInt64);
                        if (definition.GrantsEpicExchangeOrigin && core.Kind == ItemKind.Equipment)
                            // 客户端物品核心 +0xC1（123B 偏移 119）的 bit0 才是交换资格位；只记服务端来源不够。
                            core = core with { EpicExchangeOrigin = new(1, source.Core.ItemId), Extension119 = 0x01 };
                        instances.Add(new(0, default, core));
                    }
            }
            catch (InvalidDataException) { return InventoryResult.Reject(19); }
            grant = InventoryRewardGrantService.TryGrantItems(character with { Inventory = paid }, instances,
                catalog, goldLimits, pieces, now);
        }
        else
            grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, grants, catalog, goldLimits, pieces, now,
                shopExpiration: shopExpiration, contracts: contracts, allowOverflowMail: !definition.PackageRewards.IsDefaultOrEmpty,
                ignoreUsablePeriod: ignoreUsablePeriod);
        if (!grant.Accepted)
            return InventoryResult.Reject(grant.Error == 19 ? (ushort)19 : (ushort)4);
        var after = grant.Plan!.After;
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (dailyDeadline is not null && (deadline is null || dailyDeadline < deadline)) deadline = dailyDeadline;
        foreach (var item in after.Items.Concat(grant.Plan.OverflowItems))
            if (item.ExpireTime > 0 && grant.Plan.BeforeIndex.Find(item.Position) != item &&
                (deadline is null || item.ExpireTime < deadline)) deadline = item.ExpireTime;
        if (dailyDeadline is not null)
        {
            after = after with { DailyBoosterUses = dailyUses };
        }
        var cooled = ActivateCooldown(after, sourceTemplate, definition, now);
        if (cooled is null) return InventoryResult.Reject(19);
        after = cooled;
        var precondition = new InventoryCharacterPrecondition(character, deadline, clock, requirePosition: true);
        receipt = new(source.Core.ItemId, source.Position.Index, totals.Select(pair => new BoosterGrantedItem(pair.Key, pair.Value)).ToArray())
        {
            SelectionTrace = trace,
            SelectionSummary = trace is null ? null : IndependentAttributePairSampler.Describe(selectionPolicy.Policy!, trace)
        };
        return new(true, 0, new(before, after, "booster_open", precondition, overflowItems: grant.Plan.OverflowItems));

        void Add(int id, int count, long draws) => totals[id] = checked(totals.GetValueOrDefault(id) + checked((int)(count * draws)));
    }

    static bool HasCooldown(InventorySnapshot inventory, int itemId, BoosterDefinition definition, DateTimeOffset now) =>
        definition.CooltimeMaintenance is not null && inventory.ConsumableStates.Any(row =>
            row.Kind == ConsumableStateKind.Cooldown && row.CoolGroup == (definition.CoolGroup ?? -itemId) &&
            row.Active(now.ToUnixTimeMilliseconds()));

    static InventorySnapshot? ActivateCooldown(InventorySnapshot inventory, ItemTemplate source, BoosterDefinition definition, DateTimeOffset now) =>
        definition.CooltimeMaintenance is { } maintenance
            ? ConsumableStatePolicy.Activate(inventory,
                new(source.Id, source.MinimumLevel, definition.CoolTime, definition.CoolGroup ?? -source.Id)
                    { CooltimeMaintenance = maintenance }, now.ToUnixTimeMilliseconds(), sessionCooldown: true)
            : inventory;

    static bool TrySelect(BoosterSelectionDefinition definition, BoosterSelectionRequest request, out BoosterChoice[] rewards)
    {
        rewards = [];
        var category = definition.Categories.LastOrDefault(row => row.Category == request.Category && row.Subcategory == request.Subcategory);
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
                row.Refinement != entries[0].Refinement || row.RequiredKind != entries[0].RequiredKind ||
                row.NativeCreatureSelectionRow != entries[0].NativeCreatureSelectionRow ||
                !row.UpgradeChances.SequenceEqual(entries[0].UpgradeChances)))
                return false;
            selected.Add(entries[0]);
        }
        rewards = selected.ToArray();
        return true;
    }
}
