using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Tutorials;

internal static class TutorialRewards
{
    public static TutorialRewardRow[] Rows(InventoryPlan plan, InventoryIndex actual)
    {
        var rows = new List<TutorialRewardRow>();
        foreach (var slot in plan.ChangedSlots)
        {
            var item = actual.Find(slot);
            if (item is null || item.Core.Kind != ItemKind.Consumable) throw new InvalidDataException("训练奖励缺少实际物品结果");
            var old = plan.BeforeIndex.Find(slot);
            int increment = checked(item.Core.Value - (old?.Core.ItemId == item.Core.ItemId ? old.Core.Value : 0));
            if (slot.List != InventoryListType.Main || increment <= 0) throw new InvalidDataException("训练奖励不是背包正增量");
            rows.Add(new(checked((ushort)slot.Index), checked((uint)item.Core.ItemId), checked((uint)increment)));
        }
        return rows.ToArray();
    }

    public static InventoryResult Use(Character character, InventorySlot position, int itemId, ItemTemplate template)
    {
        var source = character.Inventory.Find(position);
        if (source is null || source.Core.ItemId != itemId || source.Core.Kind != ItemKind.Consumable || template.Kind != ItemKind.Consumable ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) || template.UseForbidden ||
            character.Level < template.MinimumLevel || (template.Jobs & (1 << character.Job)) == 0)
            return InventoryResult.Reject(17);
        return InventoryDeleteService.Plan(character.Inventory, position, source.Core.Value, 1);
    }

    public static InventoryResult Plan(Character character, IReadOnlyList<TutorialReward> rewards, ItemCatalog items,
        GoldLimitCatalog limits, EpicPieceCatalog pieces)
    {
        if (character.Tutorial is not { Completed: false, RewardReceived: false } || rewards.Count == 0)
            return InventoryResult.Reject(19);
        if (rewards.Any(row => items.Get(row.ItemId).Kind != ItemKind.Consumable))
            return InventoryResult.Reject(19);
        var granted = InventoryRewardGrantService.TryGrantBatch(character,
            rewards.Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray(), items, limits, pieces);
        if (!granted.Accepted) return granted;
        if (granted.Plan!.ChangedCurrencies.Length != 0 || granted.Plan.ChangedEpicPieces.Length != 0)
            return InventoryResult.Reject(19);
        var after = granted.Plan!.After;
        // 教程按键指向六个物品快捷格；只安置本次新堆，不移动已有堆或覆盖玩家设置。
        foreach (var added in after.Items.Where(item => item.Uid == 0 && item.Core.Kind == ItemKind.Consumable).ToArray())
        {
            for (short slot = 3; slot <= 8; slot++)
            {
                var position = new InventorySlot(InventoryListType.Main, slot);
                if (after.Find(position) is not null) continue;
                after = after with { Items = after.Items.Replace(added, added with { Position = position }) };
                break;
            }
        }
        return new(true, 0, new(character.Inventory, after, "tutorial_reward", new(character)));
    }
}
