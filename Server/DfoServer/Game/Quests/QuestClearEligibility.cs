using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Quests;

internal static class QuestClearEligibility
{
    // 3145资源：[clear map]、sub type=-1、单int data76126。候选仍须在死亡/成长同事务核验完整事件账本。
    public static bool TryValidate(DungeonEntryPlan entry, PreparedDungeonDeath prepared)
    {
        var clear = prepared.Result.ClearFact;
        var death = prepared.Death;
        return clear is not null && entry.CharacterId != 0 && entry.QuestId == 3145 && entry.DungeonId == 3 &&
            entry.ActiveQuestSlot <= 29 && entry.QuestAcceptanceId != Guid.Empty && entry.BossMap.Kind == "boss" &&
            entry.BossMap.Id == 76126 && entry.BossMap.Path == "map/cataclysm/granflores/01_mirkwood/3145_76126.map" &&
            clear.DungeonId == entry.DungeonId && clear.ClearMap == entry.BossMap &&
            prepared.Result.Snapshot.CurrentMap == entry.BossMap &&
            prepared.Result.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
            clear.Identity.CharacterId == entry.CharacterId && clear.Identity.QuestId == entry.QuestId &&
            clear.Identity.TaskAttempt > 0 && clear.Identity.InstanceId != Guid.Empty && death.Identity.Epoch < long.MaxValue &&
            clear.Identity == prepared.Result.Snapshot.Identity &&
            clear.Identity == (death.Identity with { Epoch = death.Identity.Epoch + 1 }) &&
            death.EventId != Guid.Empty && clear.ConfirmedDeathEvents.Count > 0 &&
            clear.ConfirmedDeathEvents.Contains(death.EventId) && !clear.ConfirmedDeathEvents.Contains(Guid.Empty) &&
            clear.ConfirmedDeathEvents.Count == clear.ConfirmedDeathEvents.Distinct().Count();
    }

    public static bool MatchesActiveQuest(DungeonEntryPlan entry, Character character)
    {
        if (character.Id != entry.CharacterId || character.Revision < entry.CharacterRevision || entry.QuestAcceptanceId == Guid.Empty ||
            character.CompletedQuests.Contains(3145))
            return false;
        var matches = character.ActiveMainQuests.Where(quest => quest.Slot == entry.ActiveQuestSlot || quest.QuestId == entry.QuestId).ToArray();
        return matches.Length == 1 && matches[0].Slot == entry.ActiveQuestSlot && matches[0].QuestId == entry.QuestId &&
            matches[0].ConditionState == 1 && matches[0].AcceptanceId == entry.QuestAcceptanceId;
    }
}
