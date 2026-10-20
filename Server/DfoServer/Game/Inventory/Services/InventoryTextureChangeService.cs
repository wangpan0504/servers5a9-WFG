using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 1546 装备材质转换：目标材质特殊材料 80 个 + 500,000 金币 + 传说灵魂 1 个，费用与物品替换同一事务提交。
// 只替换物品编号；强化、增幅、附魔、徽章、随机词条、锁、耐久与原核心的品级 Value 都原样保留。
internal sealed class InventoryTextureChangeService(ItemCatalog items, EquipmentTextureChangeCatalog textures,
    EpicPieceCatalog pieces)
{
    public const int GoldCost = 500_000;
    public const int MaterialCount = 80;
    public const int LegendarySoulItemId = 10099775;
    // 与 etc 输出顺序一致：0 布甲 3137 高级布片 / 1 皮甲 3141 上等皮革 / 2 轻甲 3146 上等碎骨 /
    // 3 重甲 3160 特级砥石 / 4 板甲 3150 钢铁片。
    static readonly int[] MaterialItemIds = [3137, 3141, 3146, 3160, 3150];

    public InventoryResult Apply(Character character, EquipmentTextureChangeRequest request, DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var before = character.Inventory;
        var slot = new InventorySlot(InventoryListType.Main, request.Slot);
        var target = before.Find(slot);
        if (request.Material >= MaterialItemIds.Length || target is null || target.Core.Kind != ItemKind.Equipment ||
            target.Core.IsRental || target.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(19);
        if (target.Core.EquipmentLockId != 0)
            return InventoryResult.Reject(0xD5);
        if (!textures.TryGet(target.Core.ItemId, out var group) || group.Material == request.Material)
            return InventoryResult.Reject(19);
        int replacementId = group.Variants[request.Material];
        ItemTemplate original, replacement;
        try
        {
            original = items.Get(target.Core.ItemId);
            replacement = items.Get(replacementId);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return InventoryResult.Reject(19);
        }
        // 同族只换材质：物品类别或部位不一致的映射按资源错误拒绝，不生成半成品装备。
        if (original.Kind != ItemKind.Equipment || replacement.Kind != ItemKind.Equipment ||
            original.EquipmentSlot != replacement.EquipmentSlot)
            return InventoryResult.Reject(19);

        var paid = InventoryMaterialConsumptionService.Plan(before,
            [new ItemMaterialCost(MaterialItemIds[request.Material], MaterialCount),
                new ItemMaterialCost(LegendarySoulItemId, 1)], pieces, now);
        if (!paid.Accepted) return InventoryResult.Reject(17);
        var after = paid.Plan!.After;
        if (CurrencyWallet.Get(after, CurrencyKind.Gold) < GoldCost) return InventoryResult.Reject(17);
        after = CurrencyWallet.Set(after, CurrencyKind.Gold, CurrencyWallet.Get(after, CurrencyKind.Gold) - GoldCost);
        // 品级（Value）原样保留：官方没有公开转换后的品级算法，本仓库按可逐字段复核的方式实现。
        after = after with
        {
            Items = after.Items.Replace(target, target with { Core = target.Core with { ItemId = replacementId } })
        };
        return new(true, 0, new InventoryPlan(before, after, "equipment_texture_change"));
    }
}
