using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.ExpertJob;

internal readonly record struct ExpertJobProduct(int ItemId, int Count);

internal sealed record ExpertJobCraftPlan(InventoryPlan Inventory, int Experience, ExpertJobProduct[] Products,
    int SuccessCount, int FailureCount);

internal static class ExpertJobCraft
{
    public static bool TryPlan(Character character, ExpertJobScriptCatalog scripts, ItemCatalog items,
        GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, uint recipeId, ushort count, ushort cardSlot,
        Func<int, int> next, DateTimeOffset now, out ExpertJobCraftPlan plan, out ushort error)
    {
        plan = null!;
        error = 19;
        if (character.ExpertJobType is < 1 or > 4 || count is 0 or > 100 || recipeId > int.MaxValue)
            return false;
        if (cardSlot is not (0 or ushort.MaxValue))
            return TryPlanCard(character, scripts, items, goldLimits, pieces, (int)recipeId, count, cardSlot, next, now,
                out plan, out error);
        if (!scripts.TryRecipe(character.ExpertJobType, (int)recipeId, out var recipe))
            return false;
        int level = scripts.Level(character.ExpertJobType, character.ExpertJobExperience);
        if (level < recipe.RequiredLevel) return false;
        var (successPercent, experienceMin, experienceMax) = scripts.Outcome(character.ExpertJobType, recipe, level);
        var inventory = character.Inventory;
        foreach (var material in recipe.Materials)
        {
            long need = (long)material.Count * count;
            if (need > int.MaxValue) return false;
            if (!Spend(inventory, material.ItemId, (int)need, now, out inventory)) return false;
        }
        int success = 0, gained = 0;
        for (int attempt = 0; attempt < count; attempt++)
        {
            if (next(100) >= successPercent) continue;
            success++;
            int gain = experienceMin;
            if (experienceMax > experienceMin) gain += next(experienceMax - experienceMin + 1);
            gained = checked(gained + gain);
        }
        ExpertJobProduct[] products = success == 0 ? [] : [new(recipe.ProductId, success)];
        if (success > 0)
        {
            var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = inventory },
                [new InventoryReward(recipe.ProductId, success)], items, goldLimits, pieces, now);
            if (!grant.Accepted || grant.Plan is null) { error = grant.Error == 0 ? (ushort)4 : grant.Error; return false; }
            inventory = grant.Plan.After;
        }
        int experience = character.ExpertJobExperience;
        if (gained > 0 && experience <= int.MaxValue - gained) experience += gained;
        plan = new(new(character.Inventory, inventory, "expert_job_compound"), experience, products, success, count - success);
        return true;
    }

    static bool TryPlanCard(Character character, ExpertJobScriptCatalog scripts, ItemCatalog items,
        GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, int recipeId, ushort count, ushort cardSlot,
        Func<int, int> next, DateTimeOffset now, out ExpertJobCraftPlan plan, out ushort error)
    {
        plan = null!;
        error = 19;
        if (character.ExpertJobType != 1 || cardSlot > short.MaxValue) return false;
        var card = character.Inventory.Find(new(InventoryListType.Main, (short)cardSlot));
        if (card is null || card.Uid <= 0 || card.Core.Count <= 0 || card.Core.EquipmentLockId != 0 ||
            card.IsExpired(now.ToUnixTimeSeconds()) || !scripts.IsKnownCard(1, card.Core.ItemId))
            return false;
        var consumed = InventoryDeleteService.Plan(character.Inventory, card.Position, card.Core.ItemId, 1);
        if (!consumed.Accepted || consumed.Plan is null) return false;
        var inventory = consumed.Plan.After;
        var cardExp = scripts.MonsterCardExp(1, scripts.Level(1, character.ExpertJobExperience));
        int gained = cardExp.Min;
        if (cardExp.Max > cardExp.Min) gained += next(cardExp.Max - cardExp.Min + 1);
        var products = Array.Empty<ExpertJobProduct>();
        int success = 1, failure = 0;
        if (recipeId > 0 && scripts.TryRecipe(1, recipeId, out var recipe))
        {
            int level = scripts.Level(1, character.ExpertJobExperience);
            if (level < recipe.RequiredLevel) return false;
            foreach (var material in recipe.Materials)
            {
                long need = (long)material.Count * count;
                if (need > int.MaxValue || !Spend(inventory, material.ItemId, (int)need, now, out inventory)) return false;
            }
            var (successPercent, experienceMin, experienceMax) = scripts.Outcome(1, recipe, level);
            success = 0;
            failure = count;
            for (int attempt = 0; attempt < count; attempt++)
            {
                if (next(100) >= successPercent) continue;
                success++;
                int gain = experienceMin;
                if (experienceMax > experienceMin) gain += next(experienceMax - experienceMin + 1);
                gained = checked(gained + gain);
            }
            failure = count - success;
            if (success > 0)
            {
                var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = inventory },
                    [new InventoryReward(recipe.ProductId, success)], items, goldLimits, pieces, now);
                if (!grant.Accepted || grant.Plan is null) { error = grant.Error == 0 ? (ushort)4 : grant.Error; return false; }
                inventory = grant.Plan.After;
                products = [new(recipe.ProductId, success)];
            }
        }
        int experience = character.ExpertJobExperience;
        if (gained > 0 && experience <= int.MaxValue - gained) experience += gained;
        plan = new(new(character.Inventory, inventory, "expert_job_card"), experience, products, success, failure);
        return true;
    }

    public static bool TryPlanExtract(Character character, ExpertJobScriptCatalog scripts, ItemCatalog items,
        GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, short extractorSlot, byte targetListByte, short targetSlot,
        Func<int, int> next, DateTimeOffset now, out ExpertJobCraftPlan plan, out ushort error)
    {
        plan = null!;
        error = 19;
        if (character.ExpertJobType is not (1 or 2 or 4) || extractorSlot < 0 || targetSlot < 0 ||
            !TryList(targetListByte, out var targetList))
            return false;
        var extractor = character.Inventory.Find(new(InventoryListType.Main, extractorSlot));
        var target = character.Inventory.Find(new(targetList, targetSlot));
        if (extractor is null || target is null || extractor.Uid <= 0 || target.Uid <= 0 ||
            extractor.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 ||
            extractor.IsExpired(now.ToUnixTimeSeconds()) || target.IsExpired(now.ToUnixTimeSeconds()) ||
            target.Core.Kind != ItemKind.Equipment || target.Core.IsStackable)
            return false;
        if (!scripts.TryExtractExp(character.ExpertJobType, extractor.Core.ItemId, out int expMin, out int expMax))
            return false;
        var template = items.Get(target.Core.ItemId);
        int state = EquipmentState(target.Core);
        if (!scripts.TryExtractResult(character.ExpertJobType, extractor.Core.ItemId, template.Rarity, state, out var row))
            return false;
        int sell = Math.Max(1, template.ShopPrice.GetSellGold(target.Core.Durability, template.Durability));
        int count = Math.Max(1, (int)Math.Floor(sell * row.Multiplier));
        var deleted = InventoryDeleteService.Plan(character.Inventory, target.Position, target.Core.ItemId, 1);
        if (!deleted.Accepted || deleted.Plan is null) return false;
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = deleted.Plan.After },
            [new InventoryReward(row.ItemId, count)], items, goldLimits, pieces, now);
        if (!grant.Accepted || grant.Plan is null) { error = grant.Error == 0 ? (ushort)4 : grant.Error; return false; }
        int gained = expMin;
        if (expMax > expMin) gained += next(expMax - expMin + 1);
        int experience = character.ExpertJobExperience;
        if (gained > 0 && experience <= int.MaxValue - gained) experience += gained;
        plan = new(new(character.Inventory, grant.Plan.After, "expert_job_extract"), experience,
            [new(row.ItemId, count)], 1, 0);
        return true;
    }

    public static bool TryPlanEnchant(Character character, InventoryEnchantmentService enchantments,
        ExpertJobScriptCatalog scripts, byte targetListByte, ushort targetSlot, byte sourceListByte, ushort sourceSlot,
        long utcSeconds, out InventoryPlan inventory, out int experience, out ushort error)
    {
        inventory = null!;
        experience = character.ExpertJobExperience;
        error = 19;
        if (character.ExpertJobType != 1 || !TryList(targetListByte, out var targetList) ||
            !TryList(sourceListByte, out var sourceList) || targetSlot > short.MaxValue || sourceSlot > short.MaxValue)
            return false;
        var source = new InventorySlot(sourceList, (short)sourceSlot);
        var target = new InventorySlot(targetList, (short)targetSlot);
        var use = new EnchantmentRequest(source, target, 0);
        var result = enchantments.Plan(character, use, utcSeconds);
        if (!result.Accepted || result.Plan is null)
        {
            // 卡片路径还需配方、材料、费用和目标适用约束，不能把任意已知卡直接写进装备冒充附魔成功。
            // 已有宝珠服务的完整校验保持不变；未闭合卡片结算不扣源、不增长熟练度。
            error = result.Error == 0 ? (ushort)19 : result.Error;
            return false;
        }

        inventory = result.Plan;
        var bead = character.Inventory.Find(source);
        if (bead is not null)
            experience = AddCardExp(character, scripts, experience, _ => 0);
        return true;
    }

    static int AddCardExp(Character character, ExpertJobScriptCatalog scripts, int current, Func<int, int> next)
    {
        var cardExp = scripts.MonsterCardExp(character.ExpertJobType, scripts.Level(character.ExpertJobType, current));
        int gained = cardExp.Min;
        if (cardExp.Max > cardExp.Min) gained += next(cardExp.Max - cardExp.Min + 1);
        if (gained > 0 && current <= int.MaxValue - gained) current += gained;
        return current;
    }

    public static InventoryReward[] DisjointProducts(int machineGrade, ExpertJobDisjointRules rules, ItemTemplate template,
        ItemCore core, Func<int, int> next, bool ownMachine = false)
    {
        int state = (core.AmplifyType & 0x80) != 0 ? 1 : template.Rarity == 3 && template.BossDrop ? 2 : 0;
        if (machineGrade <= 0 || !rules.TryGet(machineGrade - 1, template.Rarity, state, out var row)) return [];
        // 分解采用模板分解价值，不使用NPC出售的耐久与90%折价。
        long adjusted = checked((long)(Math.Max(1, template.DisjointValue) * 1.1f));
        long thousandths = checked(adjusted * 1000 / rules.BaseConst);
        int count = Math.Max(1, checked((int)(thousandths * row.Multiplier / 1000.0f)));
        var rewards = new List<InventoryReward> { new(row.ItemId, count) };
        int bigWinPercent = row.BigWinPercent + (ownMachine ? rules.OwnMachineBigWinPercent(machineGrade) : 0);
        bool bigWin = next(10000) < Math.Min(100, bigWinPercent) * 100;
        var pool = rules.Pool(bigWin ? row.BigWinTable : row.AdditionalTable, bigWin);
        int roll = next(10000);
        foreach (var candidate in pool)
        {
            if (candidate.Weight == 0 || template.Grade < candidate.MinimumGrade || template.Grade > candidate.MaximumGrade) continue;
            // df_game_r 0896D6AB采用万分域绝对权重和包含边界；空区间不补偿或归一化。
            if (roll <= candidate.Weight)
            {
                int amount = Math.Max(1, checked((int)(template.Grade / candidate.Divisor)));
                rewards.Add(new(candidate.ItemId, amount));
                break;
            }
            roll -= candidate.Weight;
        }
        return rewards.ToArray();
    }

    public static bool TryPlanGiveUp(Character character, ExpertJobScriptCatalog scripts, out InventoryPlan wallet,
        out int giveUpState, out ushort error)
    {
        wallet = null!;
        giveUpState = character.ExpertGiveUpState;
        error = 19;
        if (character.ExpertJobType is < 1 or > 4) { error = 10; return false; }
        int[] costs = scripts.GiveUpCosts(character.ExpertJobType);
        int index = Math.Clamp(character.ExpertGiveUpState, 0, costs.Length - 1);
        int cost = costs[index];
        var spend = CurrencyService.Spend(character.Inventory, CurrencyKind.Gold, cost);
        if (!spend.Accepted || spend.Plan is null) return false;
        wallet = spend.Plan;
        giveUpState = Math.Min(index + 1, costs.Length - 1);
        error = 0;
        return true;
    }

    public static bool TryPlanUpgrade(Character character, ExpertJobScriptCatalog scripts, out InventoryPlan wallet,
        out int grade, out int endurance, out ushort error)
    {
        wallet = null!;
        grade = character.ExpertMachineGrade;
        endurance = character.ExpertMachineEndurance;
        error = 19;
        if (character.ExpertJobType != 3) return false;
        int target = checked(character.ExpertMachineGrade + 1);
        if (!scripts.TryUpgradeCost(3, target, out int gold)) return false;
        if (!scripts.TryRepair(3, character.ExpertMachineGrade, out _, out int currentMax) ||
            character.ExpertMachineEndurance < currentMax)
        {
            error = 22;
            return false;
        }
        int level = scripts.Level(3, character.ExpertJobExperience);
        if (level < target || character.Level < 10) return false;
        var spend = CurrencyService.Spend(character.Inventory, CurrencyKind.Gold, gold);
        if (!spend.Accepted || spend.Plan is null) return false;
        scripts.TryRepair(3, target, out _, out int nextMax);
        wallet = spend.Plan;
        grade = target;
        endurance = nextMax > 0 ? nextMax : scripts.EnduranceInitial(3);
        error = 0;
        return true;
    }

    public static bool TryPlanRepair(Character character, ExpertJobScriptCatalog scripts, out InventoryPlan wallet,
        out int endurance, out ushort error)
    {
        wallet = null!;
        endurance = character.ExpertMachineEndurance;
        error = 19;
        if (character.ExpertJobType is not (1 or 3)) return false;
        int grade = Math.Max(1, character.ExpertMachineGrade);
        if (!scripts.TryRepair(character.ExpertJobType, grade, out int fullCost, out int max) || max <= 0)
            return false;
        int missing = max - character.ExpertMachineEndurance;
        if (missing <= 0) { error = 60; return false; }
        int cost = Math.Max(1, (int)((long)missing * fullCost / max));
        var spend = CurrencyService.Spend(character.Inventory, CurrencyKind.Gold, cost);
        if (!spend.Accepted || spend.Plan is null) return false;
        wallet = spend.Plan;
        endurance = max;
        error = 0;
        return true;
    }

    public static int EquipmentState(ItemCore core)
    {
        if (core.Chronicle0.Id > 0 || core.Chronicle1.Id > 0) return 2;
        if ((core.AmplifyType & 0x80) != 0) return 1;
        return 0;
    }

    public static bool TryList(byte value, out InventoryListType list)
    {
        list = (InventoryListType)value;
        return list is InventoryListType.Main or InventoryListType.PersonalCargo or InventoryListType.Equipment;
    }

    static bool Spend(InventorySnapshot inventory, int itemId, int count, DateTimeOffset now, out InventorySnapshot after)
    {
        after = inventory;
        int left = count;
        foreach (var item in inventory.Items.Where(row => row.Position.List == InventoryListType.Main && row.Core.ItemId == itemId &&
            row.Core.EquipmentLockId == 0 && !row.Core.IsExpired(now.ToUnixTimeSeconds())).OrderBy(row => row.Position.Index))
        {
            int take = Math.Min(left, item.Core.Count);
            var spent = InventoryDeleteService.Plan(after, item.Position, item.Core.ItemId, take);
            if (!spent.Accepted || spent.Plan is null) return false;
            after = spent.Plan.After;
            left -= take;
            if (left == 0) return true;
        }
        return false;
    }
}
