using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonAltarRewardDraw(int Gold, InventoryItem? Item);
internal sealed record DungeonAltarRewardPlan(int CompletedRounds, int MaximumRounds, uint Experience,
    ImmutableArray<DungeonAltarRewardDraw> Draws, long CreatedAt)
{
    public const string RuleId = "pvf-altar-clear-reference-v1";
    public const string ResourceSource = DungeonAltarRewardCatalog.ResourcePath + ";" + DungeonAltarClearItemCatalog.ResourcePath +
        ";" + DungeonDropRuleCatalog.CommonPath + ";" + DungeonKillExperienceCatalog.ResourcePath;

    public void Validate()
    {
        if (MaximumRounds is not (7 or 15) || CompletedRounds < 0 || CompletedRounds > MaximumRounds || Experience > 1000000 ||
            Draws.IsDefault || Draws.Length != Math.Max(1, CompletedRounds * 10 / MaximumRounds) || CreatedAt < 0)
            throw new InvalidDataException("祭坛冻结奖励维度无效");
        foreach (var draw in Draws)
        {
            if (draw.Gold < 0 || draw.Gold != 0 && draw.Item is not null || draw.Item is { Uid: not 0 })
                throw new InvalidDataException("祭坛奖励必须为金币、空位或尚未分配UID的完整物品");
            draw.Item?.Core.Validate();
            draw.Item?.ValidateDetails(allocating: true);
        }
    }
}

// 组合当前PVF与历史公开端的专用结算解释；规则身份明确保留参考边界，不声称V31逐位原服还原。
internal sealed class DungeonAltarRewardPolicy(DungeonAltarRewardCatalog rewards, DungeonAltarClearItemCatalog clear,
    DungeonItemDropCatalog candidates, DungeonKillExperienceCatalog experience, ItemCatalog items, GoldLimitCatalog limits,
    EpicPieceCatalog pieces)
{
    public DungeonAltarRewardPlan Prepare(int dungeonId, int level, int completedRounds, int maximumRounds, int enteredToday,
        Func<int, int> draw, DateTimeOffset now)
    {
        if (dungeonId is not (11006 or 11007) || maximumRounds != (dungeonId == 11006 ? 15 : 7) ||
            completedRounds < 0 || completedRounds > maximumRounds || enteredToday < 1)
            throw new InvalidDataException("祭坛结算缺少本次已完成回合和已提交入场序号");
        var probability = clear.Probabilities.Single(row => level >= row.MinimumLevel && level <= row.MaximumLevel);
        var reference = clear.ItemReferences.Single(row => row.Level == level);
        var gold = clear.GoldReferences.Single(row => row.Level == level);
        // 本版default额外列为0；非零语义与堆叠品通道未迁移时不能悄悄发放或改为装备。
        if (probability.RawExtra != 0 || clear.KindWeights[0] != 0 || clear.KindWeights[2] != 0 ||
            rewards.UltimateRewards.Any(row => row[1] != 0 || row[2] != 0))
            throw new NotSupportedException("祭坛奖励存在未绑定的额外分支");
        int Next(int bound)
        {
            int value = draw(bound);
            return value >= 0 && value < bound ? value : throw new InvalidDataException("祭坛奖励随机值越界");
        }
        var result = ImmutableArray.CreateBuilder<DungeonAltarRewardDraw>();
        int grade = Math.Min(10, completedRounds);
        for (int index = 0; index < Math.Max(1, completedRounds * 10 / maximumRounds); index++)
        {
            int roll = Next(1000);
            if (roll < rewards.ItemProbabilities[0] * grade)
            {
                int amount = GoldAmount(gold, rewards.GoldWeight, clear.SoloGoldRate, clear.RankGoldRate,
                    Next(gold.VariancePercent * 2 + 1) - gold.VariancePercent);
                result.Add(new(amount, null));
            }
            else if (roll < (rewards.ItemProbabilities[0] + rewards.ItemProbabilities[1]) * grade)
            {
                // generateRandomItem的首抽范围为1..10000；等于阈值时不走随机装备分支。
                if (Next(10000) + 1 >= clear.RandomItemProbability || Next(10000) >= probability.Probability)
                { result.Add(new(0, null)); continue; }
                int kind = Next(10000) < clear.KindWeights[1] ? 2 : 4;
                int rarityRoll = Next(1000000) + 1;
                int rarity = 0;
                while (rarity < clear.RarityThresholds.Length && rarityRoll > clear.RarityThresholds[rarity]) rarity++;
                var pool = candidates.AltarPool(kind == 4, level - reference.Value0, level + reference.Value1 - 1, rarity);
                if (pool.TotalWeight == 0) { result.Add(new(0, null)); continue; }
                var template = pool.Select(Next(pool.TotalWeight));
                var core = InventoryRewardGrantService.Create(template, 1, now);
                if (template.Kind == ItemKind.Equipment) core = core with { Value = Next(int.MaxValue) };
                var item = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core);
                result.Add(new(0, item));
            }
            else result.Add(new(0, null));
        }
        uint xp = Math.Min(1000000, rewards.Experience(completedRounds, experience.GetExperience(level), enteredToday, dungeonId == 11007));
        var plan = new DungeonAltarRewardPlan(completedRounds, maximumRounds, xp, result.ToImmutable(), now.ToUnixTimeSeconds());
        plan.Validate();
        return plan;
    }

    internal static int GoldAmount(DungeonDropGoldReference reference, float goldWeight, float soloRate, float rankRate, int variance)
    {
        if (reference.BaseAmount <= 0 || reference.VariancePercent is < 0 or > 100 || Math.Abs((long)variance) > reference.VariancePercent ||
            !float.IsFinite(goldWeight) || goldWeight < 0 || !float.IsFinite(soloRate) || soloRate < 0 || !float.IsFinite(rankRate) || rankRate < 0)
            throw new InvalidDataException("祭坛金币基值或波动越界");
        float rate = (float)((1d + rankRate) * soloRate);
        long basis = checked((long)reference.BaseAmount * 175 / 1000);
        long combined = checked((long)Math.Truncate((basis * 7 / 2) * (double)rate));
        long one = checked((long)Math.Truncate(combined / 2d * rate));
        long varied = checked(one * (100 + variance) / 100);
        return checked((int)Math.Truncate(varied * (double)goldWeight));
    }

    public InventoryPlan Delivery(Character character, byte targetLevel, DungeonAltarRewardPlan frozen, DateTimeOffset now)
    {
        frozen.Validate();
        var grown = character with { Level = targetLevel };
        var grants = frozen.Draws.Where(row => row.Item is not null).Select(row => row.Item!).ToArray();
        var result = InventoryRewardGrantService.TryGrantItems(grown, grants, items, limits, pieces, now, allowOverflowMail: true);
        if (!result.Accepted || result.Plan is not { } inventory) throw new InvalidOperationException("祭坛完整奖励无法规划入库");
        var currency = new CurrencyService(limits);
        var after = inventory.After;
        foreach (var reward in frozen.Draws)
            if (reward.Gold > 0) after = currency.Grant(grown with { Inventory = after }, CurrencyKind.Gold, reward.Gold).Plan!.After;
        return new(character.Inventory, after, "altar_clear_reward", overflowItems: inventory.OverflowItems);
    }
}
