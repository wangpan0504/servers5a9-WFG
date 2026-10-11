using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 免费牌在首次通关报告时冻结；入库、邮件和展示共用这份完整核心，不在选牌时重抽。
internal sealed record DungeonCardReward(int Gold, ItemCore? Item, string RuleId, string ResourceSource, long CreatedAt)
{
    public void Validate()
    {
        if (Gold < 0 || CreatedAt < 0 || string.IsNullOrWhiteSpace(RuleId) || RuleId.Length > 128 ||
            string.IsNullOrWhiteSpace(ResourceSource) || ResourceSource.Length > 1024)
            throw new InvalidDataException("免费翻牌奖励声明无效");
        Item?.Validate();
    }
}

// 与普通掉落共用PVF引用表及候选目录；倍率采用本服参考规则，不声称还原原服抽样。
internal sealed class DungeonCardRewardPolicy(DungeonDropRuleCatalog rules, DungeonItemDropCatalog candidates,
    ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, Func<int, int>? random = null,
    DungeonCardPriceCatalog? prices = null)
{
    public const string Rule = "pvf-free-clear-card-v1";
    static readonly decimal[] DifficultyRates = [1.02m, 1.38m, 1.60m, 1.90m, 2.00m];

    public DungeonCardReward Prepare(DungeonDefinition dungeon, byte difficulty, int confirmedKills,
        Func<int, int>? draw = null, DateTimeOffset? utcNow = null)
    {
        if (dungeon.BasisLevel is not int level || level <= 0 || difficulty >= DifficultyRates.Length || confirmedKills <= 0)
            throw new InvalidDataException("免费翻牌需要明确的副本等级、难度和已提交死亡事实");
        var gold = rules.GoldReferences.SingleOrDefault(row => row.Level == level);
        var items = rules.ItemReferences.SingleOrDefault(row => row.Level == level);
        if (gold.Level == 0 || items.Level == 0 || items.Value0 < 0 || items.Value1 < 0)
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
        int rarity = DungeonDropRatePlan.Rarity(rules, 0, Next(DungeonDropRatePlan.RarityDenominator));
        var pool = candidates.ReferencePool(true, level - items.Value0, level + items.Value1 - 1, rarity);
        if (pool.TotalWeight == 0)
            pool = candidates.ReferencePool(false, level - items.Value0, level + items.Value1 - 1, rarity);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        ItemCore? core = pool.TotalWeight == 0 ? null : DungeonDropCreation.Create(pool.Select(Next(pool.TotalWeight)), 1, Next, now, referenceQuality: true);
        var reward = new DungeonCardReward(checked((int)Math.Max(1, amount)), core, Rule,
            $"{DungeonDropRuleCatalog.CommonPath};{DungeonDropRuleCatalog.MonsterPath};{dungeon.Path}", now.ToUnixTimeSeconds());
        reward.Validate();
        return reward;
    }

    public (InventoryPlan Inventory, bool MailItem) PlanInventory(Character character, DungeonCardReward reward, byte targetLevel)
    {
        var delivered = PlanDelivery(character,[reward],targetLevel);
        return (delivered.Inventory,!delivered.MailItems.IsEmpty);
    }

    public DungeonCardOptions? PrepareOptions(DungeonDefinition dungeon, byte difficulty, int confirmedKills,
        CardMembership membership, DateTimeOffset now)
    {
        if (prices is null) return null;
        bool golden = membership.GoldCardAvailable(now.ToUnixTimeSeconds());
        return new(Prepare(dungeon,difficulty,confirmedKills,utcNow:now),
            golden ? 0 : prices.Price(dungeon.BasisLevel ?? throw new InvalidDataException("付费牌没有基准等级")), golden,membership,
            membership.BlackDiamondActive(now.ToUnixTimeSeconds()) ? Prepare(dungeon,difficulty,confirmedKills,utcNow:now) : null);
    }

    public DungeonCardDelivery PlanDelivery(Character character, IReadOnlyList<DungeonCardReward> rewards, byte targetLevel, int cost = 0)
    {
        if (cost < 0 || rewards.Count is < 1 or > 2) throw new InvalidDataException("翻牌费用或奖励批次无效");
        var grown = character with { Level = targetLevel };
        var inventory = character.Inventory;
        if (inventory.Gold < cost) throw new InvalidOperationException("付费翻牌金币不足");
        inventory = inventory with { Gold = checked(inventory.Gold - (uint)cost) };
        var mails = ImmutableArray.CreateBuilder<ItemCore>();
        var actual = ImmutableArray.CreateBuilder<DungeonCardReward>();
        foreach (var reward in rewards)
        {
            reward.Validate();
            if (reward.Item is { } core)
            {
                var grant = InventoryRewardGrantService.TryGrantCore(grown with { Inventory = inventory },core,items.Get(core.ItemId),pieces,
                    DateTimeOffset.FromUnixTimeSeconds(reward.CreatedAt));
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
