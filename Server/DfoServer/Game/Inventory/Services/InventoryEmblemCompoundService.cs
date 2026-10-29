using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEmblemCompoundService(ItemCatalog items, EmblemCompoundCatalog compounds,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, Func<long, long>? random = null)
{
    const ushort Rejection = 22;
    const ushort InventoryFull = 23;
    readonly Func<long, long> draw = random ?? Random.Shared.NextInt64;

    public InventoryResult Apply(Character character, EmblemCompoundRequest request,
        out EmblemCompoundReceipt? receipt, out string? rejection, DateTimeOffset? utcNow = null, int guildLevel = 0)
    {
        receipt = null;
        rejection = null;
        if (request.Inputs.Length is < 2 or > 5)
            return Reject("invalid-count", out rejection);

        var now = utcNow ?? DateTimeOffset.UtcNow;
        long seconds = now.ToUnixTimeSeconds();
        var before = character.Inventory;
        var requirements = new List<(InventoryItem Item, int Quantity, ItemTemplate Template)>();
        int minimumGrade = int.MaxValue;
        foreach (var group in request.Inputs.GroupBy(input => input.Slot))
        {
            if (group.Key is < 289 or > 351 || group.Select(input => input.ItemId).Distinct().Count() != 1)
                return Reject("invalid-input", out rejection);
            var input = group.First();
            int quantity = group.Count();
            InventoryItem? source = before.Find(input.Position);
            if (input.ItemId <= 0 || source is null || source.Position.List != InventoryListType.Main ||
                source.Core.ItemId != input.ItemId || source.Core.Kind != ItemKind.Emblem ||
                source.Core.Count < quantity || source.Core.EquipmentLockId != 0 || source.IsExpired(seconds) ||
                !ItemSlotBoundService.Allows(before, source.Position, ItemKind.Emblem))
                return Reject("material-state", out rejection);
            ItemTemplate template;
            try { template = items.Get(input.ItemId); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
            {
                return Reject("material-metadata", out rejection);
            }
            if (template.Id != source.Core.ItemId || template.Kind != ItemKind.Emblem || template.Grade <= 0)
                return Reject("material-template", out rejection);
            minimumGrade = Math.Min(minimumGrade, template.Grade);
            requirements.Add((source, quantity, template));
        }

        EmblemCompoundPool pool;
        try { pool = compounds.ForGuild(guildLevel, minimumGrade, request.Inputs.Length) ?? compounds.For(minimumGrade, request.Inputs.Length); }
        catch (InvalidDataException) { return Reject("grade-mapping", out rejection); }
        if (pool.Rewards.IsDefaultOrEmpty)
            return Reject("reward-pool", out rejection);

        EmblemCompoundRewardDefinition selected;
        bool platinumReroll = false;
        try
        {
            selected = Pick(pool.Rewards);
            if (selected.Rarity == 6)
            {
                var eligible = pool.Rewards.Where(candidate => candidate.Rarity == 6 &&
                    candidate.PlatinumJobs.Contains(character.Job)).ToArray();
                if (eligible.Length == 0)
                    return Reject("platinum-job-pool", out rejection);
                selected = Pick(eligible);
                platinumReroll = true;
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return Reject("reward-roll", out rejection);
        }
        try
        {
            var selectedTemplate = items.Get(selected.ItemId);
            if (selectedTemplate.Kind != ItemKind.Emblem || selectedTemplate.Rarity != selected.Rarity)
                return Reject("reward-template", out rejection);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return Reject("reward-metadata", out rejection);
        }

        var paid = before;
        foreach (var requirement in requirements)
        {
            var deleted = InventoryDeleteService.Plan(paid, requirement.Item.Position,
                requirement.Item.Core.ItemId, requirement.Quantity);
            if (!deleted.Accepted)
                return Reject("material-changed", out rejection);
            paid = deleted.Plan!.After;
        }

        var granted = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid },
            [new(selected.ItemId, selected.Count)], items, goldLimits, pieces, now);
        if (!granted.Accepted)
        {
            rejection = granted.Error == 17 ? "emblem-capacity" : "reward-grant";
            return InventoryResult.Reject(granted.Error == 17 ? InventoryFull : Rejection);
        }

        var after = granted.Plan!.After;
        long? deadline = EarliestDeadline(requirements.Select(requirement => requirement.Item)
            .Concat(after.Items.Where(item => item.Core.ItemId == selected.ItemId &&
                paid.Find(item.Position) != item)));
        receipt = new(pool.BoosterItemId, minimumGrade, request.Inputs.Length,
            selected.ItemId, selected.Count, platinumReroll);
        return new(true, 0, new InventoryPlan(before, after, "emblem_compound",
            new InventoryCharacterPrecondition(character, deadline)));
    }

    public bool UsesGuildPool(EmblemCompoundReceipt receipt, int level) =>
        compounds.ForGuild(level, receipt.MinimumGrade, receipt.InputCount)?.BoosterItemId == receipt.BoosterItemId;

    EmblemCompoundRewardDefinition Pick(IReadOnlyList<EmblemCompoundRewardDefinition> candidates)
    {
        long total = candidates.Aggregate(0L, (sum, candidate) => checked(sum + candidate.Weight));
        if (total <= 0)
            throw new InvalidDataException("徽章合成奖池总权重无效");
        long value = draw(total);
        if (value < 0 || value >= total)
            throw new InvalidDataException("徽章合成随机值越界");
        foreach (var candidate in candidates)
        {
            if (value < candidate.Weight)
                return candidate;
            value -= candidate.Weight;
        }
        throw new InvalidDataException("徽章合成加权抽取失败");
    }

    static long? EarliestDeadline(IEnumerable<InventoryItem> assets)
    {
        long? deadline = null;
        foreach (var item in assets)
            if (item.ExpireTime > 0 && (deadline is null || item.ExpireTime < deadline.Value))
                deadline = item.ExpireTime;
        return deadline;
    }

    static InventoryResult Reject(string reason, out string? rejection)
    {
        rejection = reason;
        return InventoryResult.Reject(Rejection);
    }
}
