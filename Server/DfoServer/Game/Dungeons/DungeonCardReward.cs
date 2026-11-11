using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace DfoServer.Game.Dungeons;

// 免费牌在首次通关报告时冻结；入库、邮件和展示共用这份完整核心，不在选牌时重抽。
internal sealed record DungeonCardReward(int Gold, ItemCore? Item, string RuleId, string ResourceSource, long CreatedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ItemCore? ExtraItem { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemCore? HopeEquipment { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemCore? HopeSealingItem { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemCore? TayberrsWish { get; init; }

    public IEnumerable<ItemCore> Items()
    {
        if (Item is not null) yield return Item;
        if (ExtraItem is not null) yield return ExtraItem;
        if (HopeEquipment is not null) yield return HopeEquipment;
        if (HopeSealingItem is not null) yield return HopeSealingItem;
        if (TayberrsWish is not null) yield return TayberrsWish;
    }

    public void Validate()
    {
        if (Gold < 0 || CreatedAt < 0 || string.IsNullOrWhiteSpace(RuleId) || RuleId.Length > 128 ||
            string.IsNullOrWhiteSpace(ResourceSource) || ResourceSource.Length > 1024)
            throw new InvalidDataException("免费翻牌奖励声明无效");
        Item?.Validate();
        ExtraItem?.Validate();
        HopeEquipment?.Validate();
        HopeSealingItem?.Validate();
        TayberrsWish?.Validate();
        if (TayberrsWish is not null && (TayberrsWish.ItemId != 10305617 ||
            TayberrsWish.Kind != ItemKind.Material || Item is not { ItemId: 10305616 or 10309040 } ||
            Item.ItemId == 10305616 && Item.Kind != ItemKind.Material ||
            Item.ItemId == 10309040 && Item.Kind != ItemKind.Consumable))
            throw new InvalidDataException("天空之愿只能随泰波尔斯苍穹碎片牌冻结");
        if (HopeSealingItem is not null && (HopeEquipment is null || HopeSealingItem.Kind!=ItemKind.Material))
            throw new InvalidDataException("希望封装材料只能随本次装备牌冻结");
        if (HopeEquipment is not null && (HopeEquipment.Kind!=ItemKind.Equipment || Item?.ItemId!=10307438))
            throw new InvalidDataException("希望通关装备必须随专属材料牌冻结");
        if (ExtraItem is not null && (Item is null || Item.IsStackable || ExtraItem != Item))
            throw new InvalidDataException("双倍装备必须保留两件相同完整核心");
    }
}

// 与普通掉落共用PVF引用表及候选目录；倍率采用本服参考规则，不声称还原原服抽样。
internal sealed class DungeonCardRewardPolicy(DungeonDropRuleCatalog rules, DungeonItemDropCatalog candidates,
    ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, Func<int, int>? random = null,
    DungeonCardPriceCatalog? prices = null, OperationHopeRewardPolicy? hopeRewards = null, DimensionRewardPolicy? dimensionRewards = null,
    TayberrsCardRewardPolicy? tayberrsRewards = null)
{
    public const string Rule = "pvf-free-clear-card-v1";
    static readonly decimal[] DifficultyRates = [1.02m, 1.38m, 1.60m, 1.90m, 2.00m];
    public ClearRewardLimitCatalog? ClearRewardLimits => tayberrsRewards?.Limits;
    public int? LimitedContainer(int dungeon) => tayberrsRewards?.LimitedContainer(dungeon);

    public DungeonCardReward Prepare(DungeonDefinition dungeon, byte difficulty, int confirmedKills,
        Func<int, int>? draw = null, DateTimeOffset? utcNow = null, byte job = 0, byte grow = 0, bool includeHopeEquipment = true,
        bool ancientBonus = false, DungeonIncountUsage? clearRewardUsage = null, DungeonIncountUsage? itemRewardUsage = null,
        EquipmentRarityBonus equipmentRarityBonus = default, DungeonDropMultipliers? dropMultipliers = null)
    {
        if (dungeon.BasisLevel is not int level || level <= 0 || difficulty >= DifficultyRates.Length || confirmedKills < 0)
            throw new InvalidDataException("免费翻牌需要明确的副本等级、难度和非负的已提交击杀数");
        // 英雄专表不参与普通装备和金币抽样，限次只限制领奖，不阻止再次入场。
        if (dungeon.IsEvildomWarChallenge)
            return EvildomWarChallengeRewardPolicy.Prepare(dungeon, prices, ClearRewardLimits, items,
                utcNow ?? DateTimeOffset.UtcNow, clearRewardUsage ?? default);
        var gold = rules.GoldReferences.SingleOrDefault(row => row.Level == level);
        if (gold.Level == 0)
            throw new NotSupportedException("当前副本等级缺少翻牌金币引用");
        draw ??= random ?? Random.Shared.Next;
        int Next(int bound)
        {
            int value = draw(bound);
            return value >= 0 && value < bound ? value : throw new InvalidDataException("翻牌随机值越界");
        }
        long amount = GoldAmount(level, difficulty, confirmedKills, Next);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (DisasterDungeonPolicy.HasCrystalCard(dungeon, difficulty))
        {
            var crystalCard = new DungeonCardReward(checked((int)Math.Max(1, amount)),
                DungeonDropCreation.Create(items.Get(DisasterDungeonPolicy.CrystalBoxId), 1, Next, now),
                "disaster-crystal-card-v1", "etc/dungeonetc/disasterdungeon.etc;" + dungeon.Path, now.ToUnixTimeSeconds());
            crystalCard.Validate();
            return crystalCard;
        }
        if (dungeon.IsTayberrs)
            return (tayberrsRewards ?? throw new NotSupportedException("泰波尔斯缺少材料奖励策略"))
                .Prepare(dungeon, checked((int)Math.Max(1, amount)), Next, now, clearRewardUsage, itemRewardUsage);
        if (dungeon.IsModernDimension || dungeon.IsAncientInfinite)
        {
            var policy = dimensionRewards ?? throw new NotSupportedException("异界远古缺少专属翻牌策略");
            var item = dungeon.IsModernDimension ? policy.Card(dungeon.Id, job, grow, Next, now) : policy.AncientCard(dungeon.Id, Next, now, ancientBonus);
            string cardSource = (dungeon.IsAncientInfinite ? "etc/ancientgate.etc" : DimensionRewardCatalog.PoolPath) + ";" + dungeon.Path;
            if (dungeon.IsModernDimension) cardSource += ";" + DimensionRewardPolicy.CardSetRule;
            return new(checked((int)Math.Max(1, amount)), item, DimensionRewardPolicy.Rule,
                cardSource, now.ToUnixTimeSeconds());
        }
        // 已解释的专表优先于通用装备池。安徒恩故事副本（227/233–238）同样带 anton dungeon 与
        // raid normal dungeon，但没有入场券，也不在类型 45 专表里。缺行时继续走下面的普通基准等级翻牌，
        // 不借用团本魔能石，也不把 NotSupportedException 抛出连接循环。
        if (prices?.SpecificRewards.ContainsKey(dungeon.Id) == true ||
            dungeon.DungeonType == "crackofdaybreak dungeon" || dungeon.IsOperationHope || dungeon.IsNormalFiendwar ||
            dungeon.IsOrdinaryAnton && (prices is null || dungeon.OrdinaryEntryTicket is not null ||
                prices.SpecificRewards.ContainsKey(dungeon.Id)))
        {
            if (prices is null || !prices.SpecificRewards.TryGetValue(dungeon.Id, out var specific))
                throw new NotSupportedException("副本缺少已解释的专用翻牌行，不能退回普通装备池");
            int roll = Next(10000);
            var cumulative = specific.Select(row => row.Weight).ToArray();
            for (int i = 1; i < cumulative.Length; i++) cumulative[i] = checked(cumulative[i] + cumulative[i - 1]);
            var adjusted = DungeonEpicRateAdjustment.Adjust([..cumulative],
                [..specific.Select(row => DungeonEpicDropAdjustment.IsEpic(items.Get(row.ItemId)))], 10000, dropMultipliers?.Equipment ?? 1);
            int previous = 0;
            for (int i = 0; i < specific.Length; i++)
            {
                var row = specific[i];
                int width = adjusted[i] - previous; previous = adjusted[i];
                if (roll >= width) { roll -= width; continue; }
                var item = DungeonDropCreation.Create(items.Get(row.ItemId), row.Count, Next, now);
                var declared = new DungeonCardReward(checked((int)Math.Max(1, amount)), item, Rule,
                    $"{DungeonCardPriceCatalog.ResourcePath};{dungeon.Path}", now.ToUnixTimeSeconds())
                {
                    HopeEquipment = dungeon.IsOperationHope && includeHopeEquipment
                        ? (hopeRewards ?? throw new NotSupportedException("希望通关未装配专属装备策略")).Create(job,grow,Next,now) : null,
                    HopeSealingItem = dungeon.IsOperationHope && includeHopeEquipment
                        ? hopeRewards!.CreateSealingItem(Next,now) : null
                };
                if (declared.HopeEquipment is not null) declared=declared with
                    { ResourceSource=declared.ResourceSource+";"+OperationHopeRewardPolicy.Rule+";"+OperationHopeRewardCatalog.PoolPath };
                declared.Validate();
                return declared;
            }
            if (adjusted[^1] < 10000)
                return new(checked((int)Math.Max(1, amount)), null, Rule,
                    $"{DungeonCardPriceCatalog.ResourcePath};{dungeon.Path};gm-epic-rate", now.ToUnixTimeSeconds());
            throw new InvalidDataException("副本翻牌奖励未命中已验证权重池");
        }
        // 专用牌先按原专表返回；普通等级引用不能成为专用奖励的新门禁。
        var references = prices is { ItemReferences.IsEmpty: false } ? prices.ItemReferences : rules.ItemReferences;
        var reference = references.SingleOrDefault(row => row.Level == level);
        if (reference.Level == 0 || reference.Value0 < 0 || reference.Value1 < 0)
            throw new NotSupportedException("当前副本等级缺少普通翻牌物品引用");
        int rarityRoll = Next(DungeonDropRatePlan.RarityDenominator);
        var rarityThresholds = prices is { RarityDecision.IsEmpty: false } ? prices.RarityDecision : rules.RarityDecision[..7];
        if (!equipmentRarityBonus.IsEmpty)
            rarityThresholds = EquipmentRarityDistribution.Adjust(rarityThresholds, equipmentRarityBonus,
                candidates.ReferencePool(true, level - reference.Value0, level + reference.Value1 - 1, 2).TotalWeight > 0,
                candidates.ReferencePool(true, level - reference.Value0, level + reference.Value1 - 1, 3).TotalWeight > 0);
        if (dropMultipliers is not null && candidates.ReferencePool(true, level - reference.Value0, level + reference.Value1 - 1, 4).TotalWeight > 0)
            rarityThresholds = DungeonEpicRateAdjustment.Adjust(rarityThresholds, [false, false, false, false, true, false, false],
                DungeonDropRatePlan.RarityDenominator, dropMultipliers.Equipment);
        int rarity = DungeonDropRatePlan.Rarity(rarityThresholds.AsSpan(), rarityRoll);
        var pool = candidates.ReferencePool(true, level - reference.Value0, level + reference.Value1 - 1, rarity);
        ItemCore? core = pool.TotalWeight == 0 ? null : DungeonDropCreation.Create(pool.Select(Next(pool.TotalWeight)), 1, Next, now, referenceQuality: true);
        string itemSource = prices switch
        {
            { RarityDecision.IsEmpty: false, ItemReferences.IsEmpty: false } => DungeonCardPriceCatalog.ResourcePath,
            { RarityDecision.IsEmpty: false } or { ItemReferences.IsEmpty: false } =>
                $"{DungeonDropRuleCatalog.MonsterPath};{DungeonCardPriceCatalog.ResourcePath}",
            _ => DungeonDropRuleCatalog.MonsterPath
        };
        var reward = new DungeonCardReward(checked((int)Math.Max(1, amount)), core, Rule,
            $"{DungeonDropRuleCatalog.CommonPath};{itemSource};{dungeon.Path}", now.ToUnixTimeSeconds());
        reward.Validate();
        return reward;
    }

    // 强袭普通/隐藏免费牌统一为固定55个黑夜穿行之证，不参与普通牌随机池。
    public DungeonCardReward PrepareAssault(int gold, DateTimeOffset now)
    {
        if (gold < 0) throw new InvalidDataException("强袭免费牌金币不能为负");
        var core = DungeonDropCreation.Create(items.Get(AssaultMaterialDropPolicy.CertificateId),
            AssaultMaterialDropPolicy.FreeCardCertificateCount, _ => 0, now);
        var reward = new DungeonCardReward(gold, core, "assault-free-card-local-v1",
            AssaultModeCatalog.ClearRewardPath, now.ToUnixTimeSeconds());
        reward.Validate();
        return reward;
    }

    long GoldAmount(int level, byte difficulty, int kills, Func<int, int> draw)
    {
        if (difficulty >= DifficultyRates.Length || kills < 0)
            throw new InvalidDataException("翻牌金币难度或击杀数无效");
        var gold = rules.GoldReferences.SingleOrDefault(row => row.Level == level);
        if (gold.Level == 0) throw new NotSupportedException("副本等级缺少翻牌金币引用");
        long basis = Math.Max(1, (long)gold.BaseAmount * 175 / 1000);
        long amount = checked((long)decimal.Floor(basis * DifficultyRates[difficulty] * kills));
        int variance = draw(gold.VariancePercent * 2 + 1);
        if (variance < 0 || variance > gold.VariancePercent * 2) throw new InvalidDataException("翻牌金币随机值越界");
        return amount + (variance - gold.VariancePercent) * amount / 100;
    }

    public DungeonTournamentReward PrepareTournament(DungeonDefinition dungeon, byte difficulty,
        Func<int, int>? draw = null, DateTimeOffset? utcNow = null, int clearedRounds = DungeonTournamentPlan.RoundCount)
    {
        var definition = dungeon.TournamentRewards ?? throw new InvalidDataException("大会缺少专属奖励表");
        if (dungeon.BasisLevel is not > 0) throw new InvalidDataException("大会缺少基准等级");
        if (clearedRounds is < 0 or > DungeonTournamentPlan.RoundCount)
            throw new InvalidDataException("大会已通关轮数越界");
        var randomSource = draw ?? random ?? Random.Shared.Next;
        int Next(int bound)
        {
            int value = randomSource(bound);
            return value >= 0 && value < bound ? value : throw new InvalidDataException("大会奖励随机值越界");
        }
        var now = utcNow ?? DateTimeOffset.UtcNow;
        // df_game_r 的大会生成引用只生成一次金币基准，再乘本副本倍率，不乘四轮击杀数量。
        int gold = checked((int)Math.Min(int.MaxValue, decimal.Floor(
            GoldAmount(dungeon.BasisLevel.Value, difficulty, 1, Next) * definition.GoldRate)));
        ImmutableArray<DungeonCardReward> Row(int round)
        {
            var result = ImmutableArray.CreateBuilder<DungeonCardReward>(2);
            for (int i = 0; i < 2; i++)
            {
                int amount = 0;
                ItemCore? item = null;
                if (definition.Cards.TryGetValue(round, out var weights))
                {
                    int roll = Next(weights.Total);
                    if (roll < weights.Gold) amount = gold;
                    else if (roll < weights.Gold + weights.Item)
                    {
                        int itemRoll = Next(definition.Items.Sum(row => row.Weight));
                        foreach (var row in definition.Items)
                        {
                            if (itemRoll >= row.Weight) { itemRoll -= row.Weight; continue; }
                            item = DungeonDropCreation.Create(items.Get(row.ItemId), row.Count, Next, now);
                            break;
                        }
                    }
                }
                result.Add(new(amount, item, DungeonTournamentRewards.Rule, dungeon.Path, now.ToUnixTimeSeconds()));
            }
            return result.MoveToImmutable();
        }
        ImmutableArray<DungeonCardReward> EmptyRow() =>
            [new(0, null, DungeonTournamentRewards.Rule, dungeon.Path, now.ToUnixTimeSeconds()),
             new(0, null, DungeonTournamentRewards.Rule, dungeon.Path, now.ToUnixTimeSeconds())];
        return new(Row(clearedRounds), clearedRounds == DungeonTournamentPlan.RoundCount ? Row(5) : EmptyRow());
    }

    public DungeonCardOptions? PrepareOptions(DungeonDefinition dungeon, byte difficulty, int confirmedKills,
        CardMembership membership, DateTimeOffset now, Events.ServerEventState? goldCardEvent = null, byte job = 0, byte grow = 0,
        DungeonIncountUsage? clearRewardUsage = null, DungeonIncountUsage? itemRewardUsage = null,
        EquipmentRarityBonus equipmentRarityBonus = default, DungeonDropMultipliers? dropMultipliers = null)
    {
        if (prices is null) return null;
        if (dungeon.BasisLevel is not > 0 || difficulty >= DifficultyRates.Length || confirmedKills < 0)
            throw new InvalidDataException("翻牌选项需要明确的副本等级、难度和非负的已提交击杀数");
        if (goldCardEvent is not null)
        {
            if (goldCardEvent.Schedule.EventId != Events.ServerEventService.DoubleGoldCardId)
                throw new InvalidDataException("黄金卡牌双倍奖励只接受活动64");
            goldCardEvent.Schedule.Validate();
        }
        bool golden = !dungeon.DisablePaidCard && membership.GoldCardAvailable(now.ToUnixTimeSeconds());
        bool doubled = !dungeon.DisablePaidCard && goldCardEvent?.Active == true;
        var paid = dungeon.IsTayberrs ? PrepareTayberrsPaid(dungeon, now)
            : Prepare(dungeon,difficulty,confirmedKills,utcNow:now,job:job,grow:grow,includeHopeEquipment:false,
                clearRewardUsage:clearRewardUsage,itemRewardUsage:itemRewardUsage,equipmentRarityBonus:equipmentRarityBonus,dropMultipliers:dropMultipliers) with { Gold = 0 };
        if (doubled) paid = DoubleItems(paid);
        return new(paid,
            golden ? 0 : prices.Price(dungeon.BasisLevel ?? throw new InvalidDataException("付费牌没有基准等级")), golden,membership,
            membership.BlackDiamondActive(now.ToUnixTimeSeconds()) && !dungeon.DisableBlackDiamondCardReward
                ? Prepare(dungeon,difficulty,confirmedKills,utcNow:now,job:job,grow:grow,includeHopeEquipment:false,
                    clearRewardUsage:clearRewardUsage,itemRewardUsage:itemRewardUsage,equipmentRarityBonus:equipmentRarityBonus,dropMultipliers:dropMultipliers) : null)
        {
            PaidDisabled = dungeon.DisablePaidCard,
            DoublePaidItems = doubled,
            GoldCardEventRevision = goldCardEvent?.Schedule.Revision ?? 0
        };
    }

    DungeonCardReward PrepareTayberrsPaid(DungeonDefinition dungeon, DateTimeOffset now)
    {
        if (prices is null || prices.TayberrsPaidRewards.IsEmpty)
            throw new NotSupportedException("泰波尔斯缺少类型71金币奖励组，不能重复免费奖励");
        var draw = random ?? Random.Shared.Next;
        int roll = draw(10000);
        if (roll is < 0 or >= 10000) throw new InvalidDataException("金币翻牌随机值越界");
        foreach (var row in prices.TayberrsPaidRewards)
        {
            if (roll >= row.Weight) { roll -= row.Weight; continue; }
            var reward = new DungeonCardReward(0, DungeonDropCreation.Create(items.Get(row.ItemId), row.Count, draw, now),
                Rule, DungeonCardPriceCatalog.ResourcePath + ";" + dungeon.Path, now.ToUnixTimeSeconds());
            reward.Validate();
            return reward;
        }
        throw new InvalidDataException("金币翻牌未命中完整奖励组");
    }

    public static DungeonCardReward DoubleItems(DungeonCardReward reward)
    {
        reward.Validate();
        if (reward.Item is not { } item) return reward;
        if (!item.IsStackable) return reward with { ExtraItem = item };
        if (item.Value > int.MaxValue / 2) throw new InvalidDataException("双倍翻牌数量超界");
        if (reward.TayberrsWish?.Value > int.MaxValue / 2) throw new InvalidDataException("双倍天空之愿数量超界");
        return reward with { Item = item with { Value = item.Value * 2 },
            TayberrsWish = reward.TayberrsWish is { } wish ? wish with { Value = wish.Value * 2 } : null };
    }

    public DungeonCardDelivery PlanDelivery(Character character, IReadOnlyList<DungeonCardReward> rewards, byte targetLevel,
        int cost = 0, DateTimeOffset? utcNow = null)
    {
        if (cost < 0 || rewards.Count is < 1 or > 2) throw new InvalidDataException("翻牌费用或奖励批次无效");
        var grown = character with { Level = targetLevel };
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var inventory = character.Inventory;
        if (inventory.Gold < cost) throw new InvalidOperationException("付费翻牌金币不足");
        inventory = inventory with { Gold = checked(inventory.Gold - (uint)cost) };
        var mails = ImmutableArray.CreateBuilder<ItemCore>();
        var actual = ImmutableArray.CreateBuilder<DungeonCardReward>();
        foreach (var reward in rewards)
        {
            reward.Validate();
            foreach (var core in reward.Items())
            {
                var grant = InventoryRewardGrantService.TryGrantCore(grown with { Inventory = inventory },core,items.Get(core.ItemId),pieces,
                    now);
                if (!grant.Accepted && grant.Error != 17) throw new InvalidDataException("冻结翻牌物品不能发放: " + grant.Error);
                if (grant.Accepted) inventory = grant.Plan!.After;
                else mails.Add(core);
            }
            uint before = inventory.Gold;
            var gold = new CurrencyService(goldLimits).Grant(grown with { Inventory = inventory },CurrencyKind.Gold,reward.Gold);
            inventory = gold.Plan!.After;
            actual.Add(reward with { Gold = checked((int)(inventory.Gold-before)) });
        }
        return new(new(character.Inventory,inventory,"dungeon_card_reward"),actual.ToImmutable(),mails.ToImmutable(),cost);
    }
}
