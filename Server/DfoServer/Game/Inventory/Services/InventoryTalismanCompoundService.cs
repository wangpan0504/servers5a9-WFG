using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 护石/符文合成：材料按请求逐个引用扣减，同一格可重复引用，费用与产物同一事务提交。
// 费用取 etc/runesystem.etc 的 [compound info]：族 0 符文、1 护石，模式 0 普通、1 当前转职。
// 升档概率在资源里没有可复核证据，当前只产出同品级随机一件，回执的升档位恒为 0。
internal sealed class InventoryTalismanCompoundService(ItemCatalog items, GoldLimitCatalog goldLimits,
    EpicPieceCatalog pieces, Func<long, long>? random = null)
{
    public const ushort Rejection = InventoryTalismanService.Rejection;
    public const ushort InventoryFull = InventoryTalismanService.InventoryFull;
    public const ushort NotEnoughGold = 17;
    readonly Func<long, long> draw = random ?? Random.Shared.NextInt64;

    public InventoryResult Compound(Character character, TalismanCompoundRequest request,
        TalismanCompoundCost costs, long utcSeconds, out TalismanCompoundReceipt? receipt)
    {
        receipt = null;
        // 模式 0/1 是资源表里唯一有的两组；其他取值说明请求与资源不匹配，直接拒绝。
        if (request.Mode > 1 || request.SourceSlots.IsDefaultOrEmpty || request.SourceSlots.Length > 5)
            return Reject("compound-mode");
        var before = character.Inventory;

        var requirements = new List<(InventoryItem Item, int Quantity)>();
        ItemKind? kind = null;
        byte grade = 0;
        foreach (var group in request.SourceSlots.GroupBy(slot => slot))
        {
            short slot = group.Key;
            int quantity = group.Count();
            var position = new InventorySlot(InventoryListType.Talisman, slot);
            var source = before.Find(position);
            if (source is null || source.Position != position || source.Core.ItemId <= 0 ||
                source.Core.Kind is not (ItemKind.Rune or ItemKind.Talisman) ||
                source.Core.Count < quantity || source.Core.EquipmentLockId != 0 || source.IsExpired(utcSeconds) ||
                source.Core.SortLockFlag != 0 || !ItemSlotBoundService.Allows(before, position, source.Core.Kind))
                return Reject("compound-material");
            if (kind is { } known && known != source.Core.Kind)
                return Reject("compound-mixed");
            kind = source.Core.Kind;
            byte itemGrade;
            try
            {
                if (source.Core.Kind == ItemKind.Rune)
                {
                    if (items.Rune(source.Core.ItemId) is not { } rune)
                        return Reject("compound-material");
                    itemGrade = rune.Grade;
                }
                else
                {
                    var template = items.Get(source.Core.ItemId);
                    if (template.Kind != ItemKind.Talisman || template.Rarity is < 0 or > byte.MaxValue)
                        return Reject("compound-material");
                    itemGrade = (byte)template.Rarity;
                }
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
            {
                return Reject("compound-metadata");
            }
            if (requirements.Count != 0 && itemGrade != grade)
                return Reject("compound-grade");
            grade = itemGrade;
            requirements.Add((source, quantity));
        }
        if (kind is not { } materialKind || requirements.Sum(requirement => requirement.Quantity) is < 1 or > 5)
            return Reject("compound-material");

        int family = materialKind == ItemKind.Rune ? 0 : 1;
        if (!costs.TryGet(family, request.Mode, grade, out int count, out int gold) ||
            count != requirements.Sum(requirement => requirement.Quantity))
            return Reject("compound-cost");

        var paid = before;
        foreach (var requirement in requirements)
        {
            var deleted = InventoryDeleteService.Plan(paid, requirement.Item.Position,
                requirement.Item.Core.ItemId, requirement.Quantity);
            if (!deleted.Accepted)
                return Reject("compound-material");
            paid = deleted.Plan!.After;
        }
        if (CurrencyWallet.Get(paid, CurrencyKind.Gold) < gold)
            return InventoryResult.Reject(NotEnoughGold) with { RejectReason = "compound-gold" };
        paid = CurrencyWallet.Set(paid, CurrencyKind.Gold, CurrencyWallet.Get(paid, CurrencyKind.Gold) - gold);

        IReadOnlyList<int> candidates;
        try { candidates = Candidates(character, materialKind, grade, request.Mode); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return Reject("compound-resource");
        }
        if (candidates.Count == 0)
            return Reject("compound-candidates");
        long pick = draw(candidates.Count);
        if (pick < 0 || pick >= candidates.Count)
            return Reject("compound-roll");
        int productId = candidates[checked((int)pick)];

        var granted = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid },
            [new(productId, 1)], items, goldLimits, pieces, DateTimeOffset.FromUnixTimeSeconds(utcSeconds));
        if (!granted.Accepted)
            return granted.Error == 17
                ? InventoryResult.Reject(InventoryFull) with { RejectReason = "compound-capacity" }
                : Reject("compound-grant");
        var after = granted.Plan!.After;
        // 产物可能并入已有堆叠，必须按合并前后的数量差定位，不能只找新出现的实例。
        var prior = paid.Items.Where(item => item.Core.ItemId == productId)
            .ToDictionary(item => item.Position, item => item.Core.Count);
        var product = after.Items
            .Where(item => item.Core.ItemId == productId &&
                (!prior.TryGetValue(item.Position, out int old) || item.Core.Count > old))
            .OrderByDescending(item => item.Core.Count - (prior.TryGetValue(item.Position, out int old) ? old : 0))
            .Select(item => (InventorySlot?)item.Position).FirstOrDefault();
        if (product is not { } productSlot)
            return Reject("compound-product");

        var assets = requirements.Select(requirement => requirement.Item).ToList();
        assets.AddRange(after.Items.Where(item => item.Position == productSlot));
        receipt = new(productSlot.Index, productId, false);
        return new(true, 0, new InventoryPlan(before, after,
            materialKind == ItemKind.Rune ? "rune_compound" : "talisman_compound",
            new InventoryCharacterPrecondition(character, Deadline(assets))));
    }

    // 当前转职合成只从本人可用的定义里抽；普通合成按官方规则可以从全部同品级产物里抽。
    IReadOnlyList<int> Candidates(Character character, ItemKind kind, byte grade, byte mode)
    {
        var pool = kind == ItemKind.Rune ? items.RuneCandidates(grade) : items.TalismanCandidates(grade);
        if (mode == 0)
            return pool;
        return pool.Where(id => kind == ItemKind.Rune
            ? items.Rune(id)?.AllowsUse(character.Level, character.Job, character.GrowType) == true
            : items.Talisman(id)?.AllowsUse(character.Level, character.Job, character.GrowType) == true).ToArray();
    }

    static long? Deadline(IEnumerable<InventoryItem> assets)
    {
        long? deadline = null;
        foreach (var item in assets)
            if (item.ExpireTime > 0 && (deadline is null || item.ExpireTime < deadline.Value))
                deadline = item.ExpireTime;
        return deadline;
    }

    static InventoryResult Reject(string reason) =>
        InventoryResult.Reject(Rejection) with { RejectReason = reason };
}

internal sealed record TalismanCompoundReceipt(short ProductSlot, int ItemId, bool Upgraded);
