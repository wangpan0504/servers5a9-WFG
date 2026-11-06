using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Creatures;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 启动时只证明目录和真实消费者的资源合同；不规划角色事务、不抽样、不创建随机实例。
internal sealed class PromotionConsumerValidation(ItemCatalog items, BoosterCatalog boosters,
    AvatarPackageCatalog avatars, CashShopCatalog shop, EpicPieceCatalog pieces,
    PremiumContractCatalog contracts, RandomOptionCatalog randomOptions,
    IndependentAttributePairCatalog attributePairs, CreatureCatalog creatures,
    CollectBoxCatalog collectBoxes, LimitCubeCatalog cubes, EnchantmentCatalog enchantments,
    EnhancementCatalog enhancements, RecoveryConsumableCatalog consumables,
    AmplificationCatalog amplifications, AmplifyUpgradeCatalog amplifyUpgrades,
    SpringTreasureAvatarCatalog? springAvatars = null, SpringTreasureDropPolicy? springDrops = null,
    CreatureEggCatalog? creatureEggs = null, AvatarCompoundCatalog? avatarCompounds = null,
    IReadOnlySet<int>? hellTicketIds = null)
{
    public string? Validate(PromotionRules rules)
    {
        var pending = new Queue<(int ItemId, string Source)>();
        var expanded = new HashSet<int>();
        var errors = new List<string>();
        var freeEdges = new Dictionary<int, HashSet<int>>();
        var materialInputs = new HashSet<int>();
        var materialLeaves = new List<(int ItemId, string Source)>();
        var validationNow = DateTimeOffset.UtcNow;
        bool springScope = rules.Scope == new PromotionScope("spring-2020", "replica-1", "v1", "default", "package");
        foreach (var product in rules.Products)
            Product(product.CommodityId, $"product:{product.CommodityId}", product.ItemId);
        foreach (var tier in rules.Tiers)
            Edge(new(tier.ItemId, tier.Count), $"tier:{tier.Sequence}");
        Edge(new(rules.OverflowItemId, rules.OverflowCount), "overflow");
        while (pending.TryDequeue(out var node))
        {
            // 每条来源边已在入队前校验；这里只去重同一物品的后继展开。
            if (!expanded.Add(node.ItemId)) continue;
            try { Expand(node.ItemId, node.Source); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
            { Error(node.Source, node.ItemId, error.Message); }
        }
        foreach (var leaf in materialLeaves.Where(leaf => !materialInputs.Contains(leaf.ItemId)))
            Error(leaf.Source, leaf.ItemId, "material_consumer_not_connected");
        var visiting = new HashSet<int>();
        var complete = new HashSet<int>();
        foreach (int root in freeEdges.Keys)
            if (Cycle(root)) { Error("free-grant-graph", root, "free_reward_cycle"); break; }
        return errors.Count == 0 ? null : string.Join("; ", errors);

        bool Cycle(int id)
        {
            if (visiting.Contains(id)) return true;
            if (!complete.Add(id)) return false;
            visiting.Add(id);
            if (freeEdges.TryGetValue(id, out var children))
                foreach (int child in children) if (Cycle(child)) return true;
            visiting.Remove(id);
            return false;
        }

        void Error(string source, int id, string reason) => errors.Add($"{source}/{id}: {reason}");

        void AdultCreature(int id, ItemTemplate template, CreatureDefinition? creature)
        {
            if (template.Id != id || template.Kind != ItemKind.Creature || template.EquipmentSlot != 26 ||
                creature is null || creature.ItemId != id || creature.Egg || creature.Species <= 0 ||
                creature.MaximumLevel is < 1 or > 255 || creature.ItemsPerLevel <= 0 ||
                creature.ArtifactSlots > 7 || template.CreatureInitialMode > 1)
                throw new InvalidDataException("creature_definition");
        }

        bool Special(int id, ItemTemplate template) => CurrencyWallet.RewardKind(id) is not null ||
            pieces.ByPiece.ContainsKey(id) || contracts.Recognizes(id) || template.HasAcquisitionEffect ||
            template.IsPremiumService || boosters.DirectActivationItems.Contains(id);

        void MaterialInput(ItemMaterialCost cost)
        {
            if (cost.ItemId <= 0 || cost.Count <= 0) throw new InvalidDataException("material_input_count");
            if (CurrencyWallet.RewardKind(cost.ItemId) is null && !pieces.ByPiece.ContainsKey(cost.ItemId))
            {
                var material = items.Get(cost.ItemId);
                if (material.Id != cost.ItemId || ItemSlotBoundService.List(material.Kind) != InventoryListType.Main ||
                    Special(cost.ItemId, material) || !material.Expiration.SupportsCreation)
                    throw new InvalidDataException("material_input_owner");
                InventoryRewardGrantService.ValidateCreationArguments(material);
            }
            materialInputs.Add(cost.ItemId);
        }

        void Product(int commodity, string source, int? expectedItem = null, bool paidCoupon = false)
        {
            if (shop.Product(commodity) is not { } product || product.ItemId <= 0 || product.Count <= 0 ||
                expectedItem is { } expected && expected != product.ItemId ||
                product.AvatarType != 0 || product.Section is "avatar" or "selectable character premium" or "charac premium package" or "visual" ||
                product.Section is "premium" or "privilege service" && !contracts.TryAcquisitionContract(product.ItemId, out _) ||
                product.CeraPrice < 0 || paidCoupon && product.CeraPrice == 0 || product.IsMileage ||
                product.MileagePrice != 0 || product.OtherPrice != 0 || product.GoldPrice != 0)
            { Error(source, expectedItem ?? 0, "unsupported_cash_product"); return; }
            Edge(new(product.ItemId, product.Count), source);
            foreach (var bonus in shop.Bonus(commodity))
                Edge(new(bonus.ItemId, bonus.Count), source + "/bonus");
        }

        void Edge(InventoryReward reward, string source, ItemKind? required = null,
            BoosterDefinition? parent = null, bool nativeCreatureRow = false, int? freeSource = null)
        {
            try
            {
                if (reward.Count <= 0 || reward.Upgrade > 31 || reward.Refinement > 127)
                    throw new InvalidDataException("invalid_reward_count_or_attributes");
                if (freeSource is { } from)
                {
                    if (!freeEdges.TryGetValue(from, out var targets)) freeEdges[from] = targets = [];
                    targets.Add(reward.ItemId);
                }
                if (CurrencyWallet.RewardKind(reward.ItemId) is not null || pieces.ByPiece.ContainsKey(reward.ItemId))
                {
                    if (required is not null || reward.Upgrade != 0 || reward.Refinement != 0 ||
                        nativeCreatureRow || parent is { Lottery: true } || parent?.SetsDisjointInstanceType == true ||
                        parent?.SetsEpicExchangeInstanceType == true || parent?.UnsealsRandomOptions == true)
                        throw new InvalidDataException("special_owner_in_typed_reward");
                    return;
                }
                var template = items.Get(reward.ItemId);
                if (template.Id != reward.ItemId || required is { } kind && template.Kind != kind)
                    throw new InvalidDataException("reward_template_kind");
                if (required is not null && Special(reward.ItemId, template))
                    throw new InvalidDataException("special_owner_in_typed_reward");
                if (parent is { UnsealsRandomOptions: true } && randomOptions.ResolveBoosterReward(template) is null)
                    throw new InvalidDataException("unsupported_random_option_unseal");
                if (contracts.Recognizes(reward.ItemId))
                {
                    if (!contracts.TryAcquisitionContract(reward.ItemId, out _) || reward.Upgrade != 0 ||
                        reward.Refinement != 0 || nativeCreatureRow || parent is { Lottery: true } ||
                        parent?.SetsDisjointInstanceType == true || parent?.SetsEpicExchangeInstanceType == true)
                        throw new InvalidDataException("unsupported_contract_reward");
                    return;
                }
                if (boosters.DirectActivationItems.Contains(reward.ItemId) || template.IsPremiumService)
                    throw new InvalidDataException("unsupported_premium_reward");
                if (template.HasAcquisitionEffect)
                {
                    if (reward.Upgrade != 0 || reward.Refinement != 0 || nativeCreatureRow ||
                        parent is { Lottery: true } || parent?.SetsDisjointInstanceType == true ||
                        parent?.SetsEpicExchangeInstanceType == true)
                        throw new InvalidDataException("unsupported_acquisition_reward");
                    // 与统一获取服务相同的三种明确 owner，未知 premium 已在上方拒绝。
                    if (template.AcquisitionContract is null && !template.Expiration.SupportsCreation)
                        throw new InvalidDataException("unsupported_acquisition_expiration");
                    if (template.AcquisitionContract is null && (reward.Count != 1 ||
                        template.PersonalCargoUpgradeTarget > 0 && !CargoCatalog.SupportsPersonalCapacity(template.PersonalCargoUpgradeTarget) ||
                        template.AcquisitionAccountCargo is { } cargo && cargo.Tier.Capacity <= cargo.PreviousCapacity))
                        throw new InvalidDataException("unsupported_acquisition_cargo");
                    return;
                }
                if (InventoryRewardGrantService.GrantDefinitionError(template, reward) is { } grantError)
                    throw new InvalidDataException("reward_grant_definition:" + grantError);
                InventoryRewardGrantService.ValidateCreationArguments(template, 1, reward.Upgrade, reward.Refinement, reward.Quality);
                if (!template.Expiration.SupportsCreation || template.Echon is not null && reward.CustomAbilityGrade is < 0 or > 3)
                    throw new InvalidDataException("unsupported_reward_creation");
                if (nativeCreatureRow && template.Kind == ItemKind.Avatar)
                {
                    var avatar = avatars.GetAvatar(reward.ItemId);
                    if (avatar.PendingRule is not null || !avatar.Types.IsEmpty || avatar.RequiresAbilityChoice || !avatar.Abilities.Contains(0))
                        throw new InvalidDataException("native_creature_row_avatar");
                }
                if (parent is { } box && (box.SetsDisjointInstanceType || box.SetsEpicExchangeInstanceType) && template.Kind != ItemKind.Equipment)
                {
                    if (box.SetsEpicExchangeInstanceType || (box.Selection is null && !box.HasFixedOrdinaryRewards) ||
                        template.Kind != ItemKind.Consumable ||
                        boosters.Get(reward.ItemId) is not { Available: true, Selection: not null })
                        throw new InvalidDataException("unsupported_instance_type_reward");
                }
                if (parent is { Lottery: true } && ItemSlotBoundService.List(template.Kind) != InventoryListType.Main)
                    throw new InvalidDataException("lottery_reward_not_main");
                pending.Enqueue((reward.ItemId, source));
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
            { Error(source, reward.ItemId, error.Message); }
        }

        void ExpandBooster(int id, string source, BoosterDefinition booster)
        {
            if (!booster.Available) throw new InvalidDataException(booster.PendingRule ?? "booster_unavailable");
            if (attributePairs.Resolve(id, AttributePairEntryKind.Booster).Kind == AttributePairResolutionKind.RequiredUnavailable)
                throw new InvalidDataException("attribute_pair_policy_unavailable");
            if (booster.LotteryUseNeedItem is { } lotteryCost)
            {
                if (!booster.Lottery) throw new InvalidDataException("lottery_material_entry");
                MaterialInput(lotteryCost);
            }
            if (booster.RuneBox is { } box)
            {
                if (!items.RuneRandomChoices().TryGet(box.Family, box.Tier, out var choose))
                    throw new InvalidDataException("rune_box_table");
                // 本职业筛选是原品级池的子集，筛空时仍回退原池；静态图不以单一角色裁掉候选。
                foreach (var (grade, weight) in choose.Grades.Where(row => row.Weight > 0))
                {
                    var candidates = box.Family == 0 ? items.RuneCandidates(grade) : items.TalismanCandidates(grade);
                    string runeSource = $"{source}/{id}:rune-box:{box.Family}:{box.Tier}:{grade}";
                    if (candidates.Count == 0) { Error(runeSource, id, "rune_box_empty"); continue; }
                    foreach (int candidate in candidates)
                        // RuneBox沿独立容器消费者；普通袖珍罐的Main限制仍由原parent门禁处理。
                        Edge(new(candidate, 1), runeSource, box.Family == 0 ? ItemKind.Rune : ItemKind.Talisman, freeSource: id);
                }
                return;
            }
            var packageTotals = new Dictionary<int, int>();
            foreach (var row in booster.PackageRewards.IsDefault ? System.Collections.Immutable.ImmutableArray<BoosterReward>.Empty : booster.PackageRewards)
            {
                packageTotals[row.ItemId] = checked(packageTotals.GetValueOrDefault(row.ItemId) + row.Count);
                if (items.Get(row.ItemId).Kind == ItemKind.Avatar && avatars.GetAvatar(row.ItemId) is { } avatar &&
                    (avatar.PendingRule is not null || avatar.Abilities.IsEmpty))
                { Error(source, row.ItemId, "package_avatar_ability_unavailable"); continue; }
                Edge(new(row.ItemId, row.Count, CustomAbilityGrade: booster.CustomAbilityGrade), $"{source}/{id}:package", parent: booster, freeSource: id);
            }
            foreach (var pool in booster.Pools)
            {
                if (pool.Draws <= 0 || !pool.Rewards.Any(row => row.Weight > 0))
                { Error(source, id, "empty_or_invalid_pool"); continue; }
                foreach (var row in pool.Rewards.Where(row => row.Weight > 0 && !row.EmptyOutcome))
                {
                    if (checked((long)row.Count * pool.Draws) > int.MaxValue)
                    { Error(source, row.ItemId, "pool_reward_count_overflow"); continue; }
                    Edge(new(row.ItemId, row.Count, CustomAbilityGrade: booster.CustomAbilityGrade),
                        $"{source}/{id}:pool:{pool.JobTag}:{pool.GrowType}:{pool.MinimumLevel}:{pool.MaximumLevel}", pool.RequiredKind, booster, freeSource: id);
                }
            }
            if (booster.Selection is { } selection)
            {
                if (selection.SelectionCount < 0 || selection.Categories.IsEmpty)
                    throw new InvalidDataException("selection_definition");
                // 与真实 TrySelect 相同，同坐标只使用最后块；原生空坐标没有合法选择请求。
                var effective = selection.Categories.GroupBy(row => (row.Category, row.Subcategory))
                    .Select(group => group.Last()).ToArray();
                if (!effective.Any(category => category.PendingRule is null && !category.Rewards.IsEmpty))
                    Error(source, id, "selection_has_no_usable_category");
                foreach (var category in effective)
                {
                    if (category.PendingRule is not null)
                    { Error(source, id, category.PendingRule); continue; }
                    if (category.Rewards.IsEmpty) continue;
                    foreach (var row in category.Rewards)
                    {
                        if (!row.UpgradeChances.IsEmpty && (row.UpgradeChances.Any(chance => chance.Weight < 0) ||
                            !row.UpgradeChances.Any(chance => chance.Weight > 0)))
                        { Error(source, row.ItemId, "selection_upgrade_chances"); continue; }
                        string selectedSource = $"{source}/{id}:selection:{category.Category}:{category.Subcategory}";
                        Edge(new(row.ItemId, row.Count, row.Upgrade, row.Refinement, booster.CustomAbilityGrade),
                            selectedSource, row.RequiredKind, booster, row.NativeCreatureSelectionRow, id);
                        foreach (var upgrade in row.UpgradeChances.Where(chance => chance.Weight > 0))
                            Edge(new(row.ItemId, row.Count, upgrade.Level, row.Refinement, booster.CustomAbilityGrade),
                                selectedSource + ":upgrade", row.RequiredKind, booster, row.NativeCreatureSelectionRow, id);
                    }
                }
            }
            return;
        }

        ImmutableHashSet<byte> CompoundOutput(AvatarCompoundJobDefinition definition, string part,
            AvatarCompoundCandidate candidate, bool target, string source)
        {
            var output = items.Get(candidate.ItemId);
            if (output.Id != candidate.ItemId ||
                !InventoryAvatarCompoundService.OutputTemplateMatches(checked((byte)(definition.Job - 1)), output, part) ||
                target && output.Grade != definition.Grade && output.Grade != definition.UpperGrade)
                throw new InvalidDataException($"avatar_compound_output_definition:{definition.Job}/{part}/{candidate.ItemId}");
            var avatar = avatars.GetAvatar(candidate.ItemId);
            if (avatar.PendingRule is not null || avatar.Abilities.IsEmpty)
                throw new InvalidDataException($"avatar_compound_output_ability:{definition.Job}/{part}/{candidate.ItemId}");
            InventoryRewardGrantService.ValidateCreationArguments(output);
            int deadline = output.Expiration.Resolve(validationNow);
            if (deadline > 0 && deadline <= validationNow.ToUnixTimeSeconds())
                throw new InvalidDataException($"avatar_compound_output_expired:{definition.Job}/{part}/{candidate.ItemId}");
            // 另消耗双源时装，不登记为从材料免费产生的边；后续用途仍沿原图完整展开。
            Edge(new(candidate.ItemId, 1), $"{source}/avatar-compound:{definition.Job}/{part}", ItemKind.Avatar);
            return avatar.Abilities;
        }

        void Expand(int id, string source)
        {
            var template = items.Get(id);
            if (template.AvatarGiftType is { } giftType)
            {
                if (template.Kind != ItemKind.Consumable || template.UseForbidden)
                    throw new InvalidDataException("avatar_gift_source");
                bool found = false;
                foreach (var product in shop.Products.Values)
                {
                    if (product.Section != "avatar" || product.IsMileage || shop.IsCouponProduct(product.CommodityId)) continue;
                    var output = items.Get(product.ItemId);
                    if (InventoryCashShopService.GiftType(output, product.AvatarType) != giftType) continue;
                    found = true;
                    var avatar = avatars.GetAvatar(product.ItemId);
                    if (output.Id != product.ItemId || output.Kind != ItemKind.Avatar || output.EquipmentSlot > 11 ||
                        !output.Expiration.SupportsCreation || avatar.PendingRule is not null ||
                        product.AvatarType >= avatar.Types.Length || avatar.Abilities.IsEmpty)
                    { Error(source, product.ItemId, "avatar_gift_definition"); continue; }
                    // 商城按精确类型行兑换，不采用装扮礼盒的越界末档回退。
                    var type = avatar.Types[product.AvatarType];
                    if (type.Cera < 0 || type.Gold < 0 || type.WinPoints < 0)
                    { Error(source, product.ItemId, "avatar_gift_cost"); continue; }
                    string childSource = $"{source}/{id}:avatar-gift:{product.CommodityId}";
                    Edge(new(product.ItemId, 1), childSource, ItemKind.Avatar, freeSource: id);
                    foreach (var bonus in shop.Bonus(product.CommodityId))
                        Edge(new(bonus.ItemId, bonus.Count), childSource + "/bonus", freeSource: id);
                }
                if (!found) throw new InvalidDataException("avatar_gift_consumer_not_connected");
                return;
            }
            // 本活动明确保留这两项的真实交付；其初夏优惠券/谋略战内部玩法不属于本活动开启验收。
            // 只在精确已批准范围内跳过内部用途；每条发放边已完整经过 Edge。
            if (springScope && id is 490705398 or 490705400) return;
            // 与 BoosterHandler 相同，已识别装扮盒不得降级到普通自选盒。
            var avatarBox = avatars.Get(id);
            if (avatarBox.Recognized)
            {
                if (!avatarBox.Available || avatarBox.SelectionCount < 0)
                    throw new InvalidDataException(avatarBox.PendingRule ?? "avatar_box_unavailable");
                foreach (var category in avatarBox.Categories)
                {
                    if (category.PendingRule is not null || category.Rows.IsEmpty)
                    { Error(source, id, category.PendingRule ?? "empty_avatar_category"); continue; }
                    foreach (var row in category.Rows)
                    {
                        var child = items.Get(row.ItemId);
                        var avatar = avatars.GetAvatar(row.ItemId);
                        if (InventoryAvatarPackageService.ValidateRowDefinition(row, child, avatar) is { } reason)
                        { Error(source, row.ItemId, reason); continue; }
                        Edge(new(row.ItemId, row.Count), $"{source}/{id}:avatar:{category.Category}:{category.Subcategory}", ItemKind.Avatar, freeSource: id);
                    }
                    foreach (var row in category.EquipmentRows)
                        Edge(new(row.ItemId, row.Count), $"{source}/{id}:equipment:{category.Category}:{category.Subcategory}", ItemKind.Equipment, freeSource: id);
                    foreach (var row in category.MaterialRows)
                    {
                        var child = items.Get(row.ItemId);
                        if (CurrencyWallet.RewardKind(row.ItemId) is not null)
                        {
                            if (InventoryAvatarPackageService.WalletRowDefinitionError(new(row.ItemId, row.Count), child) is { } walletError)
                            { Error(source, row.ItemId, walletError); continue; }
                            Edge(new(row.ItemId, row.Count), $"{source}/{id}:wallet:{category.Category}:{category.Subcategory}", freeSource: id);
                            continue;
                        }
                        if (InventoryAvatarPackageService.EtcRowDefinitionError(row, child, pieces) is { } reason)
                        { Error(source, row.ItemId, reason); continue; }
                        string kind = child.Kind == ItemKind.Consumable ? "stackable" : "material";
                        Edge(new(row.ItemId, row.Count), $"{source}/{id}:{kind}:{category.Category}:{category.Subcategory}", child.Kind, freeSource: id);
                    }
                    foreach (var row in category.CreatureRows)
                    {
                        var child = items.Get(row.ItemId);
                        if (InventoryAvatarPackageService.CreatureRowDefinitionError(row, child) is { } reason)
                        { Error(source, row.ItemId, reason); continue; }
                        Edge(new(row.ItemId, row.Count), $"{source}/{id}:creature:{category.Category}:{category.Subcategory}", ItemKind.Creature, freeSource: id);
                    }
                    foreach (var row in category.StackableRows)
                    {
                        var child = items.Get(row.ItemId);
                        if (CurrencyWallet.RewardKind(row.ItemId) is not null)
                        {
                            if (InventoryAvatarPackageService.WalletRowDefinitionError(new(row.ItemId, row.Count), child) is { } walletError)
                            { Error(source, row.ItemId, walletError); continue; }
                            Edge(new(row.ItemId, row.Count), $"{source}/{id}:wallet:{category.Category}:{category.Subcategory}", freeSource: id);
                            continue;
                        }
                        if (InventoryAvatarPackageService.StackableRowDefinitionError(row, child, pieces) is { } reason)
                        { Error(source, row.ItemId, reason); continue; }
                        Edge(new(row.ItemId, row.Count), $"{source}/{id}:stackable:{category.Category}:{category.Subcategory}", child.Kind, freeSource: id);
                    }
                }
                return;
            }
            var booster = boosters.Get(id);
            bool isBooster = booster.Available || new[] { "[booster]", "[booster selection]", "[cera package]" }
                .Any(type => template.StackableType.StartsWith(type, StringComparison.Ordinal));
            if (isBooster)
            {
                ExpandBooster(id, source, booster);
                return;
            }
            // CMD251直接调用结果礼包的奖池，不把中间礼包创建到背包。
            if (template.StackableType == "[random reward item] 0")
            {
                var effect = items.SharedEffect(id);
                if (template.Kind != ItemKind.Consumable || template.UseForbidden || template.Jobs == 0 ||
                    template.MinimumLevel > template.MaximumLevel || effect is null || effect.CoolTime < 0 ||
                    effect.CooltimeMaintenance > 7 || effect.Outcomes.IsDefaultOrEmpty || effect.Outcomes.Length > 256 ||
                    effect.Outcomes.Any(row => row.Weight < 0) || effect.Outcomes.Sum(row => (long)row.Weight) <= 0 ||
                    effect.Outcomes.Where((row, index) => row.Index != index).Any())
                    throw new InvalidDataException("shared_effect_definition");
                if (attributePairs.Resolve(id, AttributePairEntryKind.Booster).Kind == AttributePairResolutionKind.RequiredUnavailable)
                    throw new InvalidDataException("attribute_pair_policy_unavailable");
                foreach (var outcome in effect.Outcomes.Where(row => row.Weight > 0))
                {
                    // 实际消费者将非正引用作为空结果，只消费源并发布特效。
                    if (outcome.BoosterId <= 0) continue;
                    var invoked = boosters.Get(outcome.BoosterId);
                    if (!invoked.Available || invoked.Lottery || invoked.RuneBox is not null ||
                        invoked.Selection is not null || !invoked.PackageRewards.IsDefaultOrEmpty)
                        throw new InvalidDataException("shared_effect_pool_definition:" + outcome.BoosterId);
                    ExpandBooster(id, $"{source}/shared-outcome:{outcome.Index}/pool:{outcome.BoosterId}", invoked);
                }
                return;
            }
            // 普通饲料沿CMD44宠物消费者，不借同族成长道具或经验胶囊授予资格。
            if (template.StackableType == "[feed] 1")
            {
                var feed = creatures.Feed(id);
                if (template.Kind != ItemKind.CreatureConsumable || feed is null || feed.ItemId != id ||
                    feed.MinimumLevel < 0 || feed.CoolTime < 0 || feed.Satiety is <= 0 or > 100 ||
                    feed.EffectSeconds < 0 || feed.SkillRecoveryRate is < -100 or > 1000)
                    throw new InvalidDataException("creature_feed_definition");
                // Session在喂养前读取同源冷却组；缺省负ID合法，显式负组由目录拒绝。
                _ = creatures.CooldownGroup(id);
                return;
            }
            if (items.CashShopCouponKey(id) is { } coupon)
            {
                if (!shop.CouponMappings.TryGetValue(coupon, out var mapping) || mapping.Count == 0)
                    throw new InvalidDataException("coupon_mapping_missing");
                // 每次是另一次真实付费购买，回到旧节点只去重展开，不把优惠券当免费发奖环。
                foreach (int commodity in mapping.Values.Distinct())
                    Product(commodity, $"{source}/{id}:paid-coupon:{coupon}", paidCoupon: true);
                return;
            }
            if (template.StackableType == "[creature] 1" && creatures.RenameCard(id))
            {
                if (template.Kind != ItemKind.CreatureConsumable)
                    throw new InvalidDataException("creature_rename_definition");
                return;
            }
            if (cubes.Get(id) is { } cube)
            {
                if (attributePairs.Resolve(id, AttributePairEntryKind.LimitCube).Kind == AttributePairResolutionKind.RequiredUnavailable)
                    throw new InvalidDataException("attribute_pair_policy_unavailable");
                if (cube.ItemId != id || cube.Targets.IsEmpty || cube.Results.IsEmpty)
                    throw new InvalidDataException("limit_cube_definition");
                foreach (var target in cube.Targets.Concat(cube.Materials))
                    MaterialInput(target);
                foreach (var row in cube.Results.Where(row => row.Weight > 0))
                {
                    var result = items.Get(row.ItemId);
                    if (row.Count is <= 0 or > ushort.MaxValue || row.Count > result.StackLimit || Special(row.ItemId, result) ||
                        ItemSlotBoundService.List(result.Kind) != InventoryListType.Main ||
                        result.Kind == ItemKind.Equipment && cubes.IsBattleSpirit(result.Id))
                        throw new InvalidDataException("limit_cube_result");
                    Edge(new(row.ItemId, row.Count), $"{source}/{id}:cube", freeSource: id);
                }
                return;
            }
            switch (template.Kind)
            {
                case ItemKind.Equipment:
                    if (template.EquipmentSlot is < 12 or > 25) throw new InvalidDataException("equipment_slot");
                    return;
                case ItemKind.Rune:
                    if (items.Rune(id) is not { } rune || rune.Item != template)
                        throw new InvalidDataException("rune_definition");
                    return;
                case ItemKind.Talisman:
                    if (items.Talisman(id) is not { IsPlaceholder: false } talisman || talisman.Item != template)
                        throw new InvalidDataException("talisman_definition");
                    return;
                case ItemKind.Avatar:
                    if (template.EquipmentSlot > 11 || avatars.GetAvatar(id) is { PendingRule: not null })
                        throw new InvalidDataException("avatar_definition");
                    if (springScope && template.EquipmentSlot < 8)
                    {
                        if (springAvatars is null || springDrops is null)
                            throw new InvalidDataException("spring_treasure_consumer_not_connected");
                        if (springAvatars.Members.TryGetValue(id, out var member))
                        {
                            if (member != template) throw new InvalidDataException("spring_treasure_member_changed");
                            Edge(new(springDrops.DropItemId, 1), source + "/typed-suit-boss-drop", freeSource: id);
                        }
                    }
                    return;
                case ItemKind.Creature:
                    var creature = creatures.Get(id);
                    if (creature is { Egg: true })
                    {
                        if (template.EquipmentSlot != 26 || template.UseForbidden || creature.ItemId != id ||
                            creature.Species <= 0 || creatureEggs?.Get(id) is not { } egg || egg.EggItemId != id ||
                            egg.Species != creature.Species || egg.OutputItemId != creature.OutputItemId ||
                            egg.OutputItemId <= 0 || egg.OutputItemId == id)
                            throw new InvalidDataException("creature_egg_definition");
                        // 孵化转换原UID和期限，不将成宠当作新奖励创建；双方species由各自定义决定。
                        AdultCreature(egg.OutputItemId, items.Get(egg.OutputItemId), creatures.Get(egg.OutputItemId));
                    }
                    else
                        AdultCreature(id, template, creature);
                    return;
                case ItemKind.Emblem:
                    // 与真实装扮/装备镶嵌消费者相同；没有孔型的徽章不能只凭 Kind 通行。
                    if (template.EmblemTargetSockets == 0) throw new InvalidDataException("emblem_socket_definition");
                    return;
                case ItemKind.CreatureEquipment:
                    if (template.EquipmentSlot is < 27 or > 29) throw new InvalidDataException("creature_equipment_slot");
                    return;
            }
            if (items.ActionConsumable(id) is
                { Label: "[add dimension dungeon enter count]", Parameters: [> 0 and <= 5] } dimensionTicket)
            {
                if (dimensionTicket.Item != template) throw new InvalidDataException("dimension_entry_ticket_definition");
                return;
            }
            // 积分 owner 仅接实际支持的 typed 动作，其它 label 不借此放行。
            if (items.ActionConsumable(id) is { Label: "[add mileage]", Parameters: [> 0] } mileage)
            {
                if (mileage.Item != template) throw new InvalidDataException("mileage_template_changed");
                return;
            }
            if (items.LevelUpByTable(id) is { } levelTable)
            {
                if (levelTable.Item != template) throw new InvalidDataException("level_up_by_table_definition");
                return;
            }
            if (template.OpensCreatureSkinSlot || template.OpensAuraSkinSlot)
            {
                if (template.UseForbidden || template.MinimumLevel > template.MaximumLevel ||
                    template.OpensCreatureSkinSlot && template.Kind != ItemKind.Consumable ||
                    template.OpensAuraSkinSlot &&
                        (template.Kind != ItemKind.Consumable || template.StackableType != "[etc] 1"))
                    throw new InvalidDataException("skin_slot_ticket_definition");
                return;
            }
            if (amplifications.Get(AmplificationOperation.GoldInvest, id) is { } golden)
            {
                if (template.Kind != ItemKind.Consumable || template.UseForbidden || golden.Count <= 0 ||
                    golden.Operation != AmplificationOperation.GoldInvest || golden.Option is < 1 or > 5 ||
                    golden.MinimumItemLevel > golden.MaximumItemLevel ||
                    golden.Levels.IsEmpty || golden.Levels.Any(row => row.Level > 31 || row.Weight < 0) ||
                    !golden.Levels.Any(row => row.Weight > 0))
                    throw new InvalidDataException("golden_amplification_definition");
                return;
            }
            // 独立增幅动作优先于 enchant random；来源和Kind分派与真实增幅服务一致。
            if (amplifyUpgrades.Get(id) is { } amplify)
            {
                if (amplify.PendingRule is not null || template.UseForbidden ||
                    !new ItemCore(template.Id, template.Kind, 1).IsStackable ||
                    ItemSlotBoundService.List(template.Kind) != InventoryListType.Main)
                    throw new InvalidDataException(amplify.PendingRule ?? "amplify_source_definition");
                _ = amplifyUpgrades.Rules;
                switch (amplify.Kind)
                {
                    case AmplifyUseKind.Portable:
                    case AmplifyUseKind.Protection:
                        break;
                    case AmplifyUseKind.Lucky:
                        _ = amplify.Reinforce ? amplifyUpgrades.LuckyReinforce : amplifyUpgrades.Lucky;
                        break;
                    default:
                        throw new InvalidDataException("amplify_consumer_not_connected");
                }
                return;
            }
            if (enhancements.Get(id) is { } enhancement)
            {
                if (!enhancement.Available || enhancement.ItemId != id)
                    throw new InvalidDataException(enhancement.PendingRule ?? "enhancement_definition");
                return;
            }
            if (template.RefinementTicket is { } refinement)
            {
                if (!refinement.Available) throw new InvalidDataException(refinement.PendingRule ?? "refinement_definition");
                return;
            }
            if (template.QualityBox is not null || template.AvatarAbilityChange is not null || template.ResealConsumable is not null)
                return;
            // 只为疲劳动作读取专用定义，避免无关叶触发资源解析；
            // 与实际疲劳消费者保持相同的模板和源资格检查。
            if (items.StackableAction(id) is { Label: "[add fatigue]" } &&
                items.FatigueConsumable(id) is { } fatigue)
            {
                if (fatigue.Item != template || template.Kind != ItemKind.Consumable || template.UseForbidden ||
                    fatigue.Amount <= 0 || fatigue.Places.IsDefault)
                    throw new InvalidDataException("fatigue_consumable_definition");
                return;
            }
            if (consumables.TryGet(id, out var consumable))
            {
                if (template.Kind != ItemKind.Consumable || consumable.ItemId != id || consumable.UseForbidden ||
                    consumable.MinimumLevel > consumable.MaximumLevel)
                    throw new InvalidDataException("recovery_consumable_definition");
                if (consumable.TownGiftItem > 0) Edge(new(consumable.TownGiftItem, 1), source + "/town-gift", freeSource: id);
                return;
            }
            var boxes = collectBoxes.Entries.Where(entry => entry.TrySlot(id, out _)).ToArray();
            if (boxes.Length != 0)
            {
                foreach (var box in boxes)
                {
                    if (!box.IsPermanentSpring2020) throw new InvalidDataException("collect_box_permanent_contract");
                    // 一个可达珠子要求完整九槽，而不是只检查抽中/首个叶。
                    foreach (var slot in box.Slots)
                    {
                        var bead = items.Get(slot.ItemId);
                        InventoryRewardGrantService.ValidateCreationArguments(bead);
                        if (!bead.Expiration.SupportsCreation || !box.TrySlot(slot.ItemId, out var mapped) || mapped != slot)
                            throw new InvalidDataException("collect_box_slot_definition");
                    }
                }
                return;
            }
            // 卡片数值由客户端按完整卡ID/等级解释；资格必须由真实 typed 附魔目录确认。
            if (enchantments.Get(id) is { } enchantment)
            {
                if (enchantment.BeadItemId != id || enchantment.Targets.IsEmpty ||
                    enchantment.CardItemId <= 0 && enchantment.RandomTitle is null && enchantment.RandomCards is null)
                    throw new InvalidDataException("enchantment_definition");
                if (enchantment.CardItemId > 0) _ = items.Get(enchantment.CardItemId);
                if (enchantment.RandomCards is { } cards)
                    foreach (int card in cards.Positions) _ = items.Get(card);
                return;
            }
            // 只完整解析实际材料关联的职业，保留共用材料的全部职业和八部位合同。
            var compoundDefinitions = avatarCompounds?.ForMaterial(id).ToArray();
            if (compoundDefinitions is { Length: > 0 })
            {
                if (template.UseForbidden || !new ItemCore(template.Id, template.Kind, 1).IsStackable ||
                    ItemSlotBoundService.List(template.Kind) != InventoryListType.Main ||
                    Special(id, template) || !template.Expiration.SupportsCreation)
                    throw new InvalidDataException("avatar_compound_material_definition");
                InventoryRewardGrantService.ValidateCreationArguments(template);
                foreach (var definition in compoundDefinitions)
                {
                    if (definition.Job is < 1 or > 16 ||
                        InventoryAvatarCompoundService.MaterialBonus(definition, id) is not { } bonus)
                        throw new InvalidDataException("avatar_compound_material_owner");
                    foreach (var pool in definition.Parts.Values)
                    {
                        // 服务允许双高级源；两档概率非负，其组合是本材料可达到的最大稀有率。
                        bool rareReachable = !pool.RarePool.IsEmpty &&
                            InventoryAvatarCompoundService.CombinedRareRate(definition, pool.Part,
                                definition.UpperGrade, definition.UpperGrade, bonus) > 0;
                        HashSet<byte>? rareAbilities = null;
                        if (rareReachable)
                        {
                            foreach (var candidate in pool.RarePool)
                            {
                                var abilitySet = CompoundOutput(definition, pool.Part, candidate, false, source);
                                if (rareAbilities is null) rareAbilities = abilitySet.ToHashSet();
                                else rareAbilities.IntersectWith(abilitySet);
                            }
                        }
                        foreach (var candidate in pool.TargetPool)
                        {
                            var abilitySet = CompoundOutput(definition, pool.Part, candidate, true, source);
                            // 保留目标全部合法能力；仅存在一项公共能力不能保证其余选择不随机失败。
                            if (rareAbilities is not null && !abilitySet.IsSubsetOf(rareAbilities))
                                throw new InvalidDataException($"avatar_compound_ability_contract:{definition.Job}/{pool.Part}/{candidate.ItemId}");
                        }
                    }
                }
                materialInputs.Add(id);
                return;
            }
            if (template.Kind == ItemKind.Material)
            {
                // 材料的用途可由队列稍后到达的真实付费/开罐消费者声明，不能依出队顺序判断未知。
                materialLeaves.Add((id, source));
                // 调用方从同一城镇及副本目录登记具有正数费用和可达迷宫的候选。
                // 仅有worldmap声明不代表实际收费消费者已连接。
                if (hellTicketIds?.Contains(id) == true)
                {
                    if (template.UseForbidden || !new ItemCore(template.Id, template.Kind, 1).IsStackable ||
                        ItemSlotBoundService.List(template.Kind) != InventoryListType.Main)
                        throw new InvalidDataException("hell_party_ticket_definition");
                    materialInputs.Add(id);
                }
                return;
            }
            // 其它真实使用 owner 尚未连接；持有/发放成功不足以证明可使用。
            throw new InvalidDataException("consumer_not_connected:" + template.StackableType);
        }
    }
}
