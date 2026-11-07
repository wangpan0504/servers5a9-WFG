using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEquipmentTransmissionService(ItemCatalog items, EquipmentTransmissionCatalog transmissions,
    EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;

    // Handler在owner锁外预热本次实际需要的模板；规划只读已准备的目录和角色快照。
    public void Prepare(EquipmentTransmissionRequest request)
    {
        _ = items.Get(request.TargetId);
        _ = items.Get(request.DonorId);
    }

    public InventoryResult Transmit(Character character, EquipmentTransmissionRequest request)
    {
        var now = time.GetUtcNow();
        var before = character.Inventory;
        var target = before.Find(new(InventoryListType.Main, request.TargetSlot));
        var donor = before.Find(new(InventoryListType.Main, request.DonorSlot));
        if (target is null || donor is null || target.Uid <= 0 || donor.Uid <= 0 || target.Uid == donor.Uid ||
            target.Position == donor.Position || target.Core.ItemId != request.TargetId || donor.Core.ItemId != request.DonorId ||
            !Eligible(before, target, now) || !Eligible(before, donor, now))
            return InventoryResult.Reject(1);
        if (target.Core.EquipmentLockId != 0 || donor.Core.EquipmentLockId != 0)
            return InventoryResult.Reject(0xD6);
        var recipient = items.Get(request.TargetId);
        var material = items.Get(request.DonorId);
        if (recipient.Kind != ItemKind.Equipment || material.Kind != ItemKind.Equipment ||
            !transmissions.Targets.Contains(request.TargetId) || !transmissions.DonorRarities.Contains(material.Rarity) ||
            recipient.EquipmentSlot != material.EquipmentSlot || material.AttachType.Trim().Trim('`') != "[trade]" ||
            donor.Core.SealFlag != 0 || donor.Core.EnchantCardId == 0 && donor.Core.EmblemSocketCount == 0)
            return InventoryResult.Reject(1);

        var paid = InventoryMaterialConsumptionService.Plan(before,
            transmissions.Materials.Where(cost => cost.ItemId != 0).ToArray(), pieces, now);
        long gold = transmissions.Materials.Where(cost => cost.ItemId == 0).Sum(cost => (long)cost.Count);
        if (!paid.Accepted || CurrencyWallet.Get(paid.Plan!.After, CurrencyKind.Gold) < gold)
            return InventoryResult.Reject(0x16);
        var after = paid.Plan.After;
        if (after.Find(target.Position) != target || after.Find(donor.Position) != donor)
            return InventoryResult.Reject(1);
        after = CurrencyWallet.Set(after, CurrencyKind.Gold, CurrencyWallet.Get(after, CurrencyKind.Gold) - gold);
        // 本版窗口只承诺附魔、徽章及镶嵌栏覆盖；不能复制供体的强化、品级或未知扩展。
        var inherited = target.Core with
        {
            Rune = 0,
            EnchantCardId = donor.Core.EnchantCardId,
            EnchantUpgrade = donor.Core.EnchantUpgrade,
            EmblemSocketCount = donor.Core.EmblemSocketCount,
            Emblem1 = donor.Core.Emblem1,
            Emblem2 = donor.Core.Emblem2
        };
        after = after with { Items = after.Items.Remove(donor).Replace(target, target with { Core = inherited }) };
        // 包括实际消费的材料，避免任何有限期资产在规划后到期仍被提交。
        long? deadline = before.Items.Where(item => item.ExpireTime > 0 &&
                (item == target || item == donor || paid.Plan.ChangedSlots.Contains(item.Position)))
            .Select(item => (long?)item.ExpireTime).Min();
        return new(true, 0, new(before, after, "equipment_transmission", new(character, deadline, time)));
    }

    static bool Eligible(InventorySnapshot before, InventoryItem item, DateTimeOffset now) =>
        item.Core.Kind == ItemKind.Equipment && !item.Core.IsRental && !item.IsExpired(now.ToUnixTimeSeconds()) &&
        ItemSlotBoundService.Allows(before, item.Position, ItemKind.Equipment);
}
