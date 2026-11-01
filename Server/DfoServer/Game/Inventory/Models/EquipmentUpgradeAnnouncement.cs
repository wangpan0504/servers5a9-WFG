using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal enum EquipmentUpgradeKind { Reinforcement, Amplification, Refinement }

internal sealed record EquipmentUpgradeAnnouncement(EquipmentUpgradeKind Kind, ItemCore Item, byte Level, bool Succeeded)
{
    public int AttemptedLevel => Succeeded ? Level : Level + 1;

    public static EquipmentUpgradeAnnouncement? Enhancement(InventoryResult result, EnhancementReceipt receipt, int? minimumLevel)
    {
        if (!result.Accepted || result.Receipt is null || minimumLevel is null || !receipt.UsesLevelProbability ||
            result.Plan?.BeforeIndex.Find(receipt.Target) is not { Core.Kind: ItemKind.Equipment } before)
            return null;
        // 原服先公告、再处理失败惩罚：失败用原等级判断，不能因碎装或归零漏掉高等级失败。
        byte level = receipt.Succeeded ? receipt.NewLevel : receipt.OldLevel;
        if (level < minimumLevel) return null;
        var item = receipt.Succeeded ? result.Receipt.Index.Find(receipt.Target)?.Core : before.Core;
        if (item is null) return null;
        return new(receipt.Mode is EnhancementMode.Amplify or EnhancementMode.LuckyAmplify
            ? EquipmentUpgradeKind.Amplification : EquipmentUpgradeKind.Reinforcement, item, level, receipt.Succeeded);
    }

    public static EquipmentUpgradeAnnouncement? Refinement(InventoryResult result, RefinementReceipt receipt)
    {
        if (!result.Accepted || result.Receipt is null || !receipt.Announce ||
            result.Receipt.Index.Find(receipt.Position) is not { Core.Kind: ItemKind.Equipment } item)
            return null;
        return new(EquipmentUpgradeKind.Refinement, item.Core, receipt.NewLevel, receipt.Succeeded);
    }
}
