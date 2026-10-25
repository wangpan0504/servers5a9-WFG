using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 休亚原生史诗交换：一次业务内扣金币、扣材料、消耗源装备并发放目标装备。
// 来源资格看实例位 bit0（只有休亚兑换来的装备带这一位），交换次数取源实例位 bits1..5。
// 目标规则取自客户端说明「选择的装备不限部位，可以交换所有同稀有度的装备」：只要求目标是与源同稀有度的装备，
// etc 的三组 [reward epic item] 并集属于 190307_preyraid 的史诗交换窗，与本命令无关。
// 确认窗构包首字段是源实例的主背包槽位（实机抓包 18/19/9 与库里槽位一致），优先按它定位，槽位不可用时退回最小槽位。
internal sealed class EpicExchangeService(EpicExchangeCatalog catalog, ItemCatalog items, EpicPieceCatalog pieces,
    GoldLimitCatalog goldLimits)
{
    public InventoryResult Exchange(Character character, int sourceItemId, int targetItemId, int sourceSlot,
        DateTimeOffset? utcNow = null)
    {
        if (sourceItemId <= 0 || targetItemId <= 0)
            return InventoryResult.Reject(4);
        ItemTemplate target;
        int sourceRarity;
        try
        {
            target = items.Get(targetItemId);
            sourceRarity = items.Get(sourceItemId).Rarity;
        }
        catch (InvalidDataException)
        {
            return InventoryResult.Reject(4);
        }
        catch (IOException)
        {
            return InventoryResult.Reject(4);
        }
        // 同编号也放行：客户端把它当作同稀有度装备的一种，换回同名即重掷品级。
        if (target.Kind != ItemKind.Equipment || target.Rarity != sourceRarity)
            return InventoryResult.Reject(4);

        var now = utcNow ?? DateTimeOffset.UtcNow;
        var before = character.Inventory;
        if (FindSource(before, sourceItemId, sourceSlot, now.ToUnixTimeSeconds()) is not { } source)
            return InventoryResult.Reject(4);
        int count = (source.Core.Extension119 >> 1) & 0x1F;
        // 次数上限由 FindSource 的候选过滤保证，这里只让 etc 费用表拒绝未配置的档位。
        if (!catalog.TryCost(count, out int cost))
            return InventoryResult.Reject(4);
        if (before.Gold < (uint)cost)
            return InventoryResult.Reject(10);

        var after = before with { Gold = before.Gold - (uint)cost };
        var spent = InventoryMaterialConsumptionService.Plan(after, [catalog.RequiredItem], pieces, now);
        if (!spent.Accepted)
            return InventoryResult.Reject(4);
        after = spent.Plan!.After;

        var consumed = after.Find(source.Position);
        if (consumed is null || consumed.Core.ItemId != sourceItemId ||
            !InventoryDeleteService.TryDelete(consumed, consumed.Core.Value, consumed.Core.Count, out var remaining) ||
            remaining is not null)
            return InventoryResult.Reject(4);
        after = after with { Items = after.Items.Remove(consumed) };

        ItemCore core;
        try
        {
            // 交换次数 +1 并保持交换资格位；bits6..7 保持 0，强化/增幅/锻造/附魔不继承。
            core = InventoryRewardGrantService.Create(target, utcNow: now);
            core = core with { Extension119 = (byte)(((count + 1) << 1) | 1) };
        }
        catch (InvalidDataException)
        {
            return InventoryResult.Reject(4);
        }
        catch (IOException)
        {
            return InventoryResult.Reject(4);
        }

        var granted = InventoryRewardGrantService.TryGrantItems(character with { Inventory = after },
            [new(0, new(InventoryListType.Main, 0), core)], items, goldLimits, pieces, now);
        if (!granted.Accepted)
            return InventoryResult.Reject(granted.Error == 0 ? (ushort)4 : granted.Error);
        return new(true, 0, new(before, granted.Plan!.After, "epic_exchange"));
    }

    // 优先用请求里的槽位定位；该槽位不是同编号的合格实例时退回主背包内最小槽位的合格实例。
    // 没有 bit0 的实例（副本掉落、非休亚来源）客户端连绿字都不显示，服务端同样不接受；
    // 已满4次的副本不再参与，同编号的其他副本仍可交换。
    static InventoryItem? FindSource(InventorySnapshot inventory, int itemId, int slot, long utcSeconds)
    {
        InventoryItem? fallback = null;
        foreach (var item in inventory.Items)
        {
            if (item.Position.List != InventoryListType.Main || item.Core.ItemId != itemId ||
                item.Core.Kind != ItemKind.Equipment || (item.Core.Extension119 & 0x01) == 0 ||
                (item.Core.Extension119 >> 1 & 0x1F) >= EpicExchangeCatalog.CountLimit ||
                item.Core.EquipmentLockId != 0 || item.IsExpired(utcSeconds))
                continue;
            if (item.Position.Index == slot)
                return item;
            if (fallback is null || item.Position.Index < fallback.Position.Index)
                fallback = item;
        }
        return fallback;
    }
}
