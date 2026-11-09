using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class DisguiseUpgradeLuckPolicy
{
    public static EnhancementSuccessBonus ActiveBonus(Character character, RecoveryConsumableCatalog consumables, long now)
    {
        if (character.Disguise is not { } appearance || !appearance.Active(now) ||
            !consumables.TryGet(appearance.SourceItemId, out var source) || source.Disguise is not { } definition ||
            !definition.Allows(appearance.Type, appearance.FormId))
            return default;
        EnhancementSuccessBonus bonus = default;
        // 本服兼容规则：原%100按十万分母取相对成功权重+0.1%，不声称原版算法。
        foreach (var appendage in definition.Appendages)
            if (appendage is { Operation: "%", Stat: "upgrade luck", Amount: > 0 })
                bonus = EnhancementSuccessBonus.Max(bonus, new(appendage.Amount, appearance.UntilMilliseconds));
        return bonus;
    }
}
