using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal readonly record struct EquipmentGoldReward(long Amount, long? ExpiresAt);

internal static class EquipmentGoldBonusPolicy
{
    public static EquipmentGoldReward Capture(Character character, int basis, long now)
    {
        if (basis <= 0) throw new ArgumentOutOfRangeException(nameof(basis));
        long amount = basis;
        long? deadline = null;
        foreach (var item in character.Inventory.Items)
        {
            if (EquipmentGoldBonusDefinition.Get(item.Core.ItemId) is not { } definition ||
                item.Position != new InventorySlot(InventoryListType.Equipment, definition.Slot) ||
                item.Core.Kind != definition.Kind || item.IsExpired(now) ||
                definition.Kind == ItemKind.Creature && item.Creature is not { Satiety: > 0 }) continue;
            // 旧端称号、戒指和宠物各按原金币计算、截尾后相加，不递乘，也不先合并比例。
            long extra = (long)basis * definition.Percent / 100;
            amount += extra;
            if (extra > 0 && item.ExpireTime > 0)
                deadline = Math.Min(deadline ?? long.MaxValue, item.ExpireTime);
        }
        return new(amount, deadline);
    }
}
