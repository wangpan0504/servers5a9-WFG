using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal static class DungeonDropCreation
{
    public static bool Supports(ItemKind kind) => kind is ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material or
        ItemKind.Quest or ItemKind.CreatureConsumable or ItemKind.Emblem or ItemKind.Profession or ItemKind.GuardianGem;
    // 使用既有物品创建合同，掉落装备参考端采用完整非负int随机种子，不沿用初始装备满品级。
    // count、耐久、封装与期限在死亡冻结时确定，拾取和SQL重试不重新创建。
    public static ItemCore Create(ItemTemplate template, int count, Func<int, int> draw, DateTimeOffset now,
        bool referenceQuality = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(draw);
        if (!Supports(template.Kind))
            throw new NotSupportedException("普通死亡掉落创建尚未绑定该资源类别");
        var core = InventoryRewardGrantService.Create(template, count, now);
        if (template.Kind == ItemKind.Equipment)
        {
            int bound = referenceQuality ? 999999997 : int.MaxValue;
            int quality = draw(bound);
            if (quality < 0 || quality >= bound)
                throw new InvalidDataException("掉落装备随机种子不在声明的均匀区间");
            core = core with { Value = quality + (referenceQuality ? 1 : 0) };
        }
        core.Validate();
        if (core.IsExpired(now.ToUnixTimeSeconds()))
            throw new InvalidDataException("已过期的资源模板不能生成地面奖励");
        return core;
    }
}
