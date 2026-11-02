using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 寻物进度由真实持有量推导，不累计拾取/删除次数；共享目录不保存在线状态。
internal static class QuestInventoryProgress
{
    public static ImmutableArray<ActiveMainQuest> Refresh(IReadOnlyList<ActiveMainQuest> active, InventorySnapshot inventory,
        QuestCatalog quests, long utcSeconds, IReadOnlyCollection<int>? completed = null)
    {
        var result = ImmutableArray.CreateBuilder<ActiveMainQuest>(active.Count);
        foreach (var row in active)
        {
            if (row.AcceptanceId != Guid.Empty && quests.TryGet(row.QuestId, out var equipped) &&
                equipped.TargetKind == QuestTargetKind.EquippedItem && quests.Items is { } facts && QuestTargetRules.Supports(equipped, quests))
            {
                result.Add(row with { ConditionState = QuestItemProgress.Equipped(inventory, equipped, facts, utcSeconds) ? 0u : 1u });
                continue;
            }
            if (row.AcceptanceId == Guid.Empty || !quests.TryGet(row.QuestId, out var quest) || quest.TargetKind is not (QuestTargetKind.Seeking or QuestTargetKind.SeekAndMeetNpc or QuestTargetKind.CheckItem) ||
                !QuestLifecyclePlan.SeekingSubtype(quest) || quest.Definition is not { Diagnostics.Length: 0 } || !QuestLifecyclePlan.ValidSeeking(quest) ||
                QuestCoverage.Unconfirmed(quest).Length != 0)
            { result.Add(row); continue; }
            uint material = QuestLifecyclePlan.MaterialState(inventory, quest, utcSeconds, quests.Items);
            // Full03 0202C422..537：type0全部材料满足时将非零条件降到0；不足且当前为0时恢复非零。
            // type25两通道分别表示材料与会面是否满足；库存变化不得替代NPC事件。
            result.Add(row with { ConditionState = quest.Condition == "[seek n meet npc]"
                ? (row.ConditionState & 512u) | material : material });
        }
        var refreshed = result.ToImmutable();
        return completed is null ? refreshed : QuestRelationProgress.Refresh(refreshed,completed,quests);
    }
}
