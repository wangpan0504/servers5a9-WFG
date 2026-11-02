using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 只消费本次已经规划并与领域事务一起提交的事实；普通33只回读，不能制造使用/获取历史。
internal static class QuestEventProgress
{
    public static bool Supports(StoryQuest quest) => QuestTargetSchema.Read(quest).State == QuestParameterState.Parsed && quest.TargetKind switch
    {
        QuestTargetKind.QuestAccept or QuestTargetKind.ClearQuestByGrade or QuestTargetKind.UseItem or QuestTargetKind.GetItemCheckIndex => true,
        QuestTargetKind.GetItem => quest.IntData[0] == -1,
        _ => false
    };

    public static bool Compare(int actual, int required, int subtype) => subtype switch
    {
        -1 or 0 => actual == required,
        1 => actual >= required,
        2 => actual <= required,
        _ => false
    };

    public static uint Initial(StoryQuest quest) => Supports(quest) ? QuestTargetSchema.Read(quest).Initial :
        throw new NotSupportedException("此事件目标尚未接入");

    static ImmutableArray<ActiveMainQuest> Apply(IReadOnlyList<ActiveMainQuest> active, QuestCatalog quests, Func<StoryQuest, bool> matches) =>
        active.Select(row => row.AcceptanceId != Guid.Empty && row.ConditionState > 0 && quests.TryGet(row.QuestId, out var quest) &&
            Supports(quest) && QuestCoverage.HasOrdinaryDeclarations(quest) && matches(quest)
            ? row with { ConditionState = row.ConditionState - 1 } : row).ToImmutableArray();

    public static ImmutableArray<ActiveMainQuest> Accepted(IReadOnlyList<ActiveMainQuest> active, QuestCatalog quests) =>
        Apply(active, quests, quest => quest.TargetKind == QuestTargetKind.QuestAccept);

    public static ImmutableArray<ActiveMainQuest> Completed(IReadOnlyList<ActiveMainQuest> active, QuestCatalog quests, StoryQuest completed)
    {
        int? grade = EffectiveGrade(completed, quests);
        return Apply(active, quests, quest => quest.TargetKind == QuestTargetKind.ClearQuestByGrade &&
            (quest.IntData[0] == -1 || quest.IntData[0] == grade));
    }

    static int? EffectiveGrade(StoryQuest quest, QuestCatalog quests)
    {
        var visited = new HashSet<int>();
        while (quest.MainQuest is > 0 && visited.Add(quest.Id) && quests.TryGet(quest.MainQuest.Value, out var parent)) quest = parent;
        return quest.Grade switch
        {
            "[epic]" => 0, "[training]" => 1, "[achievement]" => 2, "[daily]" or "[special daily]" => 3,
            "[normaly repeat]" => 4, "[common unique]" => 5, "[special]" => 6, "[title]" => 7, "[urgent]" => 8,
            "[daily random]" => 9, "[wedding]" => 10, "[sub]" => 11, "[system]" => 12, "[side]" => 13,
            "[crack]" => 14, "[circle]" => 15, "[account achievement]" => 17, "[realization]" => 18,
            "[blackhole]" => 19, "[ancient]" => 20, "[requiem]" => 21, "[anton]" => 22, "[hidden]" => 23,
            "[mission]" => 24, "[daily mission]" => 25, "[auto daily mission]" => 26, "[returnee]" => 27,
            "[assault]" => 28, "[op hope mission]" => 29, "[invisible]" => 30, "[episode]" => 31,
            _ => null
        };
    }

    public static ImmutableArray<ActiveMainQuest> Inventory(IReadOnlyList<ActiveMainQuest> active, QuestCatalog quests, InventoryPlan plan)
    {
        if (plan.PreserveMaterials) return active.ToImmutableArray();
        var relevant = active.Where(row => row.AcceptanceId != Guid.Empty && row.ConditionState > 0 &&
            quests.TryGet(row.QuestId, out var quest) && quest.TargetKind is QuestTargetKind.UseItem or QuestTargetKind.GetItemCheckIndex or QuestTargetKind.GetItem).ToArray();
        if (relevant.Length == 0) return active.ToImmutableArray();
        var wanted = relevant.Select(row => quests.Get(row.QuestId)).Where(quest => quest.TargetKind == QuestTargetKind.GetItemCheckIndex)
            .SelectMany(quest => quest.IntData).ToHashSet();
        bool anyEquipment = relevant.Any(row => quests.Get(row.QuestId).TargetKind == QuestTargetKind.GetItem);
        var before = Totals(plan.Before, wanted, anyEquipment);
        var after = Totals(plan.After, wanted, anyEquipment);
        var gained = after.Where(pair => pair.Value > before.GetValueOrDefault(pair.Key)).Select(pair => pair.Key).ToHashSet();
        return Apply(active, quests, quest => quest.TargetKind switch
        {
            QuestTargetKind.UseItem => QuestTargetSchema.Read(quest).Pairs.Any(pair => plan.UsedItems.Any(use =>
                use.ItemId == pair.Key && Compare(use.Count, pair.Value, quest.SubType))),
            QuestTargetKind.GetItemCheckIndex => quest.IntData.Any(gained.Contains),
            QuestTargetKind.GetItem => quests.Items is { } items && gained.Any(id => items.TryGetLoaded(id, out var template) &&
                template.Kind == ItemKind.Equipment && (quest.SubType == 1
                    ? QuestItemProgress.AtLeastRarity(template.Rarity, quest.IntData[1]) && template.MinimumLevel >= quest.IntData[2]
                    : QuestItemProgress.AtMostRarity(template.Rarity, quest.IntData[1]) && (quest.IntData[2] == -1 || template.MinimumLevel <= quest.IntData[2]))),
            _ => false
        });
    }

    static Dictionary<int, long> Totals(InventorySnapshot inventory, HashSet<int> wanted, bool equipment)
    {
        var totals = new Dictionary<int, long>();
        foreach (var item in inventory.Items)
            if (wanted.Contains(item.Core.ItemId) || equipment && item.Core.Kind == ItemKind.Equipment)
                totals[item.Core.ItemId] = checked(totals.GetValueOrDefault(item.Core.ItemId) + item.Core.Count);
        foreach (int id in wanted)
            if (CurrencyWallet.RewardKind(id) is { } kind) totals[id] = CurrencyWallet.Get(inventory, kind);
        return totals;
    }
}
