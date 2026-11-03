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
    SpringTreasureAvatarCatalog? springAvatars = null, SpringTreasureDropPolicy? springDrops = null)
{
    public string? Validate(PromotionRules rules)
    {
        var pending = new Queue<(int ItemId, string Source)>();
        var expanded = new HashSet<int>();
        var errors = new List<string>();
        var freeEdges = new Dictionary<int, HashSet<int>>();
        var materialInputs = new HashSet<int>();
        var materialLeaves = new List<(int ItemId, string Source)>();
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
                    if (box.SetsEpicExchangeInstanceType || box.Selection is null || template.Kind != ItemKind.Consumable ||
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

        void Expand(int id, string source)
        {
            var template = items.Get(id);
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
                        if (InventoryAvatarPackageService.EtcRowDefinitionError(row, child, pieces) is { } reason)
                        { Error(source, row.ItemId, reason); continue; }
                        string kind = child.Kind == ItemKind.Consumable ? "stackable" : "material";
                        Edge(new(row.ItemId, row.Count), $"{source}/{id}:{kind}:{category.Category}:{category.Subcategory}", child.Kind, freeSource: id);
                    }
                }
                return;
            }
            var booster = boosters.Get(id);
            bool isBooster = booster.Available || new[] { "[booster]", "[booster selection]", "[cera package]" }
                .Any(type => template.StackableType.StartsWith(type, StringComparison.Ordinal));
            if (isBooster)
            {
                if (!booster.Available) throw new InvalidDataException(booster.PendingRule ?? "booster_unavailable");
                if (attributePairs.Resolve(id, AttributePairEntryKind.Booster).Kind == AttributePairResolutionKind.RequiredUnavailable)
                    throw new InvalidDataException("attribute_pair_policy_unavailable");
                if (booster.LotteryUseNeedItem is { } lotteryCost)
                {
                    if (!booster.Lottery) throw new InvalidDataException("lottery_material_entry");
                    MaterialInput(lotteryCost);
                }
                // 符文盒的真实候选需要 runesystem 表；本中间实现尚未接完整表，绝不按 Available 通行。
                if (booster.RuneBox is not null) throw new InvalidDataException("rune_box_consumer_not_connected");
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
                    foreach (var row in pool.Rewards.Where(row => row.Weight > 0))
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
            if (items.CashShopCouponKey(id) is { } coupon)
            {
                if (!shop.CouponMappings.TryGetValue(coupon, out var mapping) || mapping.Count == 0)
                    throw new InvalidDataException("coupon_mapping_missing");
                // 每次是另一次真实付费购买，回到旧节点只去重展开，不把优惠券当免费发奖环。
                foreach (int commodity in mapping.Values.Distinct())
                    Product(commodity, $"{source}/{id}:paid-coupon:{coupon}", paidCoupon: true);
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
                    if (template.EquipmentSlot != 26 || creature is null || creature.ItemId != id || creature.Egg ||
                        creature.Species <= 0 || creature.MaximumLevel is < 1 or > 255 || creature.ItemsPerLevel <= 0 ||
                        creature.ArtifactSlots > 7 || template.CreatureInitialMode > 1)
                        throw new InvalidDataException("creature_definition");
                    return;
                case ItemKind.Emblem:
                    // 与真实装扮/装备镶嵌消费者相同；没有孔型的徽章不能只凭 Kind 通行。
                    if (template.EmblemTargetSockets == 0) throw new InvalidDataException("emblem_socket_definition");
                    return;
                case ItemKind.CreatureEquipment:
                    if (template.EquipmentSlot is < 27 or > 29) throw new InvalidDataException("creature_equipment_slot");
                    return;
            }
            // 积分 owner 仅接实际支持的 typed 动作，其它 label 不借此放行。
            if (items.ActionConsumable(id) is { Label: "[add mileage]", Parameters: [> 0] } mileage)
            {
                if (mileage.Item != template) throw new InvalidDataException("mileage_template_changed");
                return;
            }
            if (template.OpensCreatureSkinSlot || template.OpensAuraSkinSlot)
            {
                if (template.UseForbidden || template.Jobs == 0 || template.MinimumLevel > template.MaximumLevel ||
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
            if (consumables.TryGet(id, out var consumable))
            {
                if (template.Kind != ItemKind.Consumable || consumable.ItemId != id || consumable.UseForbidden ||
                    consumable.Jobs == 0 || consumable.MinimumLevel > consumable.MaximumLevel)
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
            if (template.Kind == ItemKind.Material)
            {
                // 材料的用途可由队列稍后到达的真实付费/开罐消费者声明，不能依出队顺序判断未知。
                materialLeaves.Add((id, source));
                return;
            }
            // 其它真实使用 owner 尚未连接；持有/发放成功不足以证明可使用。
            throw new InvalidDataException("consumer_not_connected:" + template.StackableType);
        }
    }
}
