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

    public IEnumerable<ItemCore> Items()
    {
        if (Item is not null) yield return Item;
        if (ExtraItem is not null) yield return ExtraItem;
        if (HopeEquipment is not null) yield return HopeEquipment;
        if (HopeSealingItem is not null) yield return HopeSealingItem;
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
    DungeonCardPriceCatalog? prices = null, OperationHopeRewardPolicy? hopeRewards = null, DimensionRewardPolicy? dimensionRewards = null)
{
    public const string Rule = "pvf-free-clear-card-v1";
    static readonly decimal[] DifficultyRates = [1.02m, 1.38m, 1.60m, 1.90m, 2.00m];

    public DungeonCardReward Prepare(DungeonDefinition dungeon, byte difficulty, int confirmedKills,
        Func<int, int>? draw = null, DateTimeOffset? utcNow = null, byte job = 0, byte grow = 0, bool includeHopeEquipment = true, bool ancientBonus = false)
    {
        if (dungeon.BasisLevel is not int level || level <= 0 || difficulty >= DifficultyRates.Length || confirmedKills < 0)
            throw new InvalidDataException("免费翻牌需要明确的副本等级、难度和非负的已提交击杀数");
        var gold = rules.GoldReferences.SingleOrDefault(row => row.Level == level);
        var reference = rules.ItemReferences.SingleOrDefault(row => row.Level == level);
        if (gold.Level == 0 || reference.Level == 0 || reference.Value0 < 0 || reference.Value1 < 0)
            throw new NotSupportedException("当前副本等级缺少完整翻牌引用表");
        draw ??= random ?? Random.Shared.Next;
        int Next(int bound)
        {
            int value = draw(bound);
            return value >= 0 && value < bound ? value : throw new InvalidDataException("翻牌随机值越界");
        }
        long basis = Math.Max(1, (long)gold.BaseAmount * 175 / 1000);
        long amount = checked((long)decimal.Floor(basis * DifficultyRates[difficulty] * confirmedKills));
        amount += (Next(gold.VariancePercent * 2 + 1) - gold.VariancePercent) * amount / 100;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (dungeon.IsModernDimension || dungeon.IsAncientInfinite)
        {
            var policy = dimensionRewards ?? throw new NotSupportedException("异界远古缺少专属翻牌策略");
            var item = dungeon.IsModernDimension ? policy.Card(dungeon.Id, job, grow, Next, now) : policy.AncientCard(dungeon.Id, Next, now, ancientBonus);
            return new(checked((int)Math.Max(1, amount)), item, DimensionRewardPolicy.Rule,
                (dungeon.IsAncientInfinite ? "etc/ancientgate.etc" : DimensionRewardCatalog.PoolPath) + ";" + dungeon.Path, now.ToUnixTimeSeconds());
        }
        // 专表只属于资源里写了行的团本。安徒恩故事副本（227/233–238）同样带 anton dungeon 与
        // raid normal dungeon，但没有入场券，也不在类型 45 专表里。缺行时继续走下面的普通基准等级翻牌，
        // 不借用团本魔能石，也不把 NotSupportedException 抛出连接循环。
        if (dungeon.DungeonType == "crackofdaybreak dungeon" || dungeon.IsOperationHope || dungeon.IsNormalFiendwar ||
            string.Equals(dungeon.WarriorsDungeon, "Tayberrs", StringComparison.OrdinalIgnoreCase) ||
            dungeon.IsOrdinaryAnton && (prices is null || dungeon.OrdinaryEntryTicket is not null ||
                prices.SpecificRewards.ContainsKey(dungeon.Id)))
        {
            if (prices is null || !prices.SpecificRewards.TryGetValue(dungeon.Id, out var specific))
                throw new NotSupportedException("副本缺少已解释的专用翻牌行，不能退回普通装备池");
            int roll = Next(10000);
            foreach (var row in specific)
            {
                if (roll >= row.Weight) { roll -= row.Weight; continue; }
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
            throw new InvalidDataException("副本翻牌奖励未命中已验证权重池");
        }
        int rarity = DungeonDropRatePlan.Rarity(rules, 0, Next(DungeonDropRatePlan.RarityDenominator));
        var pool = candidates.ReferencePool(true, level - reference.Value0, level + reference.Value1 - 1, rarity);
        if (pool.TotalWeight == 0)
            pool = candidates.ReferencePool(false, level - reference.Value0, level + reference.Value1 - 1, rarity);
        ItemCore? core = pool.TotalWeight == 0 ? null : DungeonDropCreation.Create(pool.Select(Next(pool.TotalWeight)), 1, Next, now, referenceQuality: true);
        var reward = new DungeonCardReward(checked((int)Math.Max(1, amount)), core, Rule,
            $"{DungeonDropRuleCatalog.CommonPath};{DungeonDropRuleCatalog.MonsterPath};{dungeon.Path}", now.ToUnixTimeSeconds());
        reward.Validate();
        return reward;
    }

    public DungeonCardOptions? PrepareOptions(DungeonDefinition dungeon, byte difficulty, int confirmedKills,
        CardMembership membership, DateTimeOffset now, Events.ServerEventState? goldCardEvent = null, byte job = 0, byte grow = 0)
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
            : Prepare(dungeon,difficulty,confirmedKills,utcNow:now,job:job,grow:grow,includeHopeEquipment:false) with { Gold = 0 };
        if (doubled) paid = DoubleItems(paid);
        return new(paid,
            golden ? 0 : prices.Price(dungeon.BasisLevel ?? throw new InvalidDataException("付费牌没有基准等级")), golden,membership,
            membership.BlackDiamondActive(now.ToUnixTimeSeconds()) && !dungeon.DisableBlackDiamondCardReward
                ? Prepare(dungeon,difficulty,confirmedKills,utcNow:now,job:job,grow:grow,includeHopeEquipment:false) : null)
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
        return reward with { Item = item with { Value = item.Value * 2 } };
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
