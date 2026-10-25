using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// Material references are list38 slots. Rejections never return a consumption plan.
// 未抽中升档不是失败：材料照常删除并产出同档随机一件；末档（神话勋章/神器守护珠）只做同档重抽。
// 已镶宝珠的勋章不可消耗。
internal sealed class InventoryGuildCompoundService(ItemCatalog items, GuildDungeonCompoundCatalog compounds,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, Func<long, long>? random = null)
{
    const ushort Rejection = 1;
    const ushort InventoryFull = 95;
    readonly Func<long, long> draw = random ?? Random.Shared.NextInt64;

    public InventoryResult Apply(Character character, GuildCompoundKind kind, GuildCompoundRequest request,
        out GuildCompoundReceipt? receipt, out string? rejection, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        rejection = null;
        int count = request.Count;
        if (count is < 2 or > 4)
            return Reject("invalid-count", out rejection);

        var ladder = compounds.Ladder(kind);
        var recipe = ladder.Recipe(request.Grade);
        bool medal = kind == GuildCompoundKind.GuildMedal;
        bool terminal = recipe is { RatePerMaterial: 0 } && request.Grade == ladder.Rows[^1].Grade;
        if (recipe is null || (!recipe.Compoundable && !terminal))
            return Reject("invalid-grade", out rejection);
        if (!recipe.Accepts(count))
            return Reject("invalid-count", out rejection);
        int? targetGrade = terminal ? request.Grade : ladder.NextGrade(request.Grade);
        if (targetGrade is not { } nextGrade)
            return Reject("invalid-grade", out rejection);

        ItemKind itemKind = medal ? ItemKind.GuildMedal : ItemKind.GuardianGem;
        (int Start, int End) page = medal ? (0, 48) : (49, 97);
        int successRate = recipe.SuccessRateFor(count);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        long seconds = now.ToUnixTimeSeconds();
        var before = character.Inventory;
        var requirements = new List<(InventoryItem Item, int Quantity)>();
        foreach (var group in request.Slots.GroupBy(slot => slot))
        {
            int slot = group.Key;
            int quantity = group.Count();
            if (slot < page.Start || slot > page.End)
                return Reject("material-page", out rejection);
            // 勋章一件一格、一次只能用一格一次；守护珠允许同一格重复引用，每次消耗 1 件。
            if (medal && quantity != 1)
                return Reject("material-duplicate", out rejection);
            var position = request.Position(slot);
            InventoryItem? source = before.Find(position);
            if (source is null || source.Position != position || source.Core.ItemId <= 0 ||
                source.Core.Kind != itemKind || source.Core.Count < quantity ||
                source.Core.EquipmentLockId != 0 || source.IsExpired(seconds) ||
                !ItemSlotBoundService.Allows(before, position, itemKind))
                return Reject("material-state", out rejection);
            // 勋章不是堆叠品，核心数量字段必须就是 1；Value>1 属于异常存档，不能静默按一件删。
            if (medal && source.Core.Value != 1)
                return Reject("material-stack", out rejection);
            if (medal && source.Core.GuardianGemKeys != 0)
                return Reject("material-socketed", out rejection);
            ItemTemplate template;
            try { template = items.Get(source.Core.ItemId); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
            {
                return Reject("material-metadata", out rejection);
            }
            if (template.Id != source.Core.ItemId || template.Kind != itemKind || template.Rarity != request.Grade)
                return Reject("material-template", out rejection);
            requirements.Add((source, quantity));
        }

        var paid = before;
        foreach (var requirement in requirements)
        {
            var deleted = InventoryDeleteService.Plan(paid, requirement.Item.Position,
                requirement.Item.Core.ItemId, medal ? 1 : requirement.Quantity);
            if (!deleted.Accepted)
                return Reject("material-changed", out rejection);
            paid = deleted.Plan!.After;
        }

        long roll = draw(GuildCompoundLadder.RateDenominator);
        if (roll < 0 || roll >= GuildCompoundLadder.RateDenominator)
            return Reject("roll", out rejection);
        bool upgraded = terminal || roll < successRate;
        // 未抽中升档仍是一次成功合成：按同档随机产出，材料照常删除。
        int productGrade = upgraded ? nextGrade : request.Grade;
        var candidates = items.GuildCompoundCandidates(itemKind, productGrade);
        if (candidates.Count == 0)
            return Reject("candidate-pool", out rejection);
        long pick = draw(candidates.Count);
        if (pick < 0 || pick >= candidates.Count)
            return Reject("roll", out rejection);
        int productId = candidates[checked((int)pick)];
        var granted = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid },
            [new(productId, 1)], items, goldLimits, pieces, now);
        if (!granted.Accepted)
        {
            rejection = granted.Error == 17 ? "guild-compound-capacity" : "reward-grant";
            return InventoryResult.Reject(granted.Error == 17 ? InventoryFull : Rejection);
        }
        var after = granted.Plan!.After;

        var assets = requirements.Select(requirement => requirement.Item).ToList();
        assets.AddRange(after.Items.Where(item => item.Core.ItemId == productId && paid.Find(item.Position) != item));
        receipt = new(kind, request.Grade, count, productGrade, successRate, upgraded, productId);
        return new(true, 0, new InventoryPlan(before, after, "guild_compound",
            new InventoryCharacterPrecondition(character, EarliestDeadline(assets))));
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
