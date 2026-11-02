using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 0201A490/0200BE40：string data首项优先；未知名称按匹配客户端回退到creature26。
internal static class QuestItemProgress
{
    public static int Scope(StoryQuest quest)
    {
        var strings = QuestTargetSchema.ReadStrings(quest);
        if (strings.Length == 0) return quest.SubType;
        return Scope(strings[0]);
    }

    public static int Scope(string name) => name switch
    {
        "skill weapon" => 36, "skill coat" => 37,
        _ => NativeSlot(name)
    };

    static int NativeSlot(string name)
    {
        try { return ItemCatalog.Slot(name); }
        catch (InvalidDataException) { return 26; }
    }

    public static bool HasSelector(StoryQuest quest) => quest.TargetKind is QuestTargetKind.Seeking or QuestTargetKind.SeekAndMeetNpc or QuestTargetKind.CheckItem &&
        QuestTargetSchema.Read(quest).Pairs.Any(row => row.Key < 0);

    public static void Prepare(InventorySnapshot inventory, ItemCatalog items)
    {
        foreach (int id in inventory.Items.Where(item => item.Core.Kind is ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or ItemKind.Talisman)
            .Select(item => item.Core.ItemId).Distinct()) _ = items.Get(id);
    }

    public static bool ScopeMatches(ItemTemplate template, int scope) => scope switch
    {
        -1 => template.EquipmentSlot <= 11 && template.Kind == ItemKind.Avatar,
        -2 => template.EquipmentSlot is >= 12 and <= 25 && template.Kind == ItemKind.Equipment,
        _ => template.EquipmentSlot == scope && template.Kind is ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or ItemKind.Talisman
    };

    // 品级阈值与负品级选择符不是同一算法；0200BDF0/0200BDA0对所有显式阈值排除chronicle5。
    public static bool AtLeastRarity(int actual, int threshold) => threshold == -1 || actual != 5 && actual >= threshold;
    public static bool AtMostRarity(int actual, int threshold) => threshold == -1 || actual != 5 && actual <= threshold;

    public static bool MatchesSelector(InventoryItem item, ItemTemplate template, int selector, int scope, long now)
    {
        if (item.Position.List is not (InventoryListType.Main or InventoryListType.Avatar or InventoryListType.Pet or InventoryListType.Equipment or InventoryListType.Talisman) ||
            item.IsExpired(now) || !ScopeMatches(template, scope) || selector is < -7 or >= 0 || selector != -7 && template.Rarity != -selector - 1)
            return false;
        return scope != 26 || template.CreatureSubType == 0 && item.Core.SealFlag == 0;
    }

    public static long Count(InventorySnapshot inventory, StoryQuest quest, int selector, ItemCatalog items, long now, bool mainOnly = false)
    {
        long count = 0;
        int scope = Scope(quest);
        foreach (var item in inventory.Items)
        {
            if (mainOnly && item.Position.List != InventoryListType.Main || item.IsExpired(now)) continue;
            if ((scope is -1 or >= 0 and <= 11) && item.Core.Kind != ItemKind.Avatar ||
                scope == -2 && item.Core.Kind != ItemKind.Equipment || scope == 26 && item.Core.Kind != ItemKind.Creature ||
                scope is >= 12 and <= 25 && item.Core.Kind != ItemKind.Equipment || scope == 34 && item.Core.Kind != ItemKind.Talisman) continue;
            if (selector == -7 && scope == -1 && item.Core.Kind == ItemKind.Avatar &&
                item.Position.List is InventoryListType.Avatar or InventoryListType.Equipment)
            { count = checked(count + item.Core.Count); continue; }
            if (!items.TryGetLoaded(item.Core.ItemId, out var template))
            {
                if (item.Core.Kind is ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or ItemKind.Talisman)
                    throw new NotSupportedException("任务物品事实未在规划阶段装载: " + item.Core.ItemId);
                continue;
            }
            if (MatchesSelector(item, template, selector, scope, now)) count = checked(count + item.Core.Count);
        }
        return count;
    }

    public static bool Equipped(InventorySnapshot inventory, StoryQuest quest, ItemCatalog items, long now)
    {
        var parameters = QuestTargetSchema.Read(quest);
        if (parameters.State != QuestParameterState.Parsed || quest.TargetKind != QuestTargetKind.EquippedItem) return false;
        for (int at = 0; at < parameters.Strings.Length; at++)
        {
            int slot = Scope(parameters.Strings[at]);
            var item = inventory.Items.SingleOrDefault(row => row.Position.List == InventoryListType.Equipment && row.Position.Index == slot);
            if (item is null || item.IsExpired(now)) return false;
            if (!items.TryGetLoaded(item.Core.ItemId, out var template)) throw new NotSupportedException("穿戴任务缺少已装载模板");
            if (!AtLeastRarity(template.Rarity, quest.IntData[at * 2]) || template.MinimumLevel < quest.IntData[at * 2 + 1] ||
                quest.SubType == 1 && !template.CloneAvatar) return false;
        }
        return true;
    }
}
