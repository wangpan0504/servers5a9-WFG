using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestClearEligibility
{
    // 地图号是第一项。末尾 -1 与 dungeon info 的不限难度相同，仍是这一张图。
    public static bool IsSingleMap(StoryQuest quest) => quest.Condition == "[clear map]" &&
        (quest.SubType == -1 && (quest.IntData is [> 0] or [> 0, -1]) || ProtectsActors(quest));

    // 0201C410的sub0分支进入0200F350：首项为地图，后续每个APC均须仍存活且属于友方。
    internal static bool ProtectsActors(StoryQuest quest) => quest.Condition == "[clear map]" && quest.SubType == 0 &&
        quest.IntData.Count >= 2 && quest.IntData.All(value => value > 0) &&
        quest.IntData.Skip(1).Distinct().Count() == quest.IntData.Count - 1;

    internal static bool HasRequiredActors(StoryQuest quest, SinglePlayerDungeonRun run, DungeonRunSnapshot snapshot) =>
        !ProtectsActors(quest) || snapshot.CurrentMap == run.CurrentMonsterPlan.Room.Map &&
        quest.IntData.Skip(1).All(id => run.HasLivingFriendlyActor(id,snapshot));

    // 任务声明验证目标可以是基础Boss格；物件末层的物理ClearMap仍记真实层。
    internal static bool MatchesClearMap(StoryQuest quest, DungeonEntryPlan entry, DungeonClearFact clear) =>
        IsSingleMap(quest) && clear.ClearMap is { } map &&
        (quest.IntData[0] == map.Id || entry.PassiveClearTerminalMap == map && quest.IntData[0] == entry.BaseBossMap.Id);

    internal static bool MatchesClearMap(StoryQuest quest, QuestClearReceipt clear) =>
        IsSingleMap(quest) && (clear.MapId == quest.IntData[0] ||
            clear.SourceType == QuestClearSourceType.PassiveObject && quest.DungeonIndex is >= 0 &&
            clear.DungeonId == quest.DungeonIndex && clear.MapId > 0);

    // 候选仍须在死亡/成长同事务核验完整事件账本，不使用客户端清图上报生成事实。
    public static bool TryValidate(DungeonEntryPlan entry, PreparedDungeonDeath prepared)
    {
        var clear = prepared.Result.ClearFact;
        var death = prepared.Death;
        return clear is { PassiveSource: null } && !entry.IsPractice && entry.CharacterId != 0 && entry.DungeonId > 0 &&
            (entry.QuestId == 0 ? entry.ActiveQuestSlot == byte.MaxValue && entry.QuestAcceptanceId == Guid.Empty : entry.ActiveQuestSlot <= 29 && entry.QuestAcceptanceId != Guid.Empty) &&
            entry.BaseBossMap.Kind == "boss" &&
            clear.DungeonId == entry.DungeonId && clear.ClearMap == prepared.Result.Snapshot.CurrentMap &&
            entry.IsTerminalMap(prepared.Result.Snapshot.CurrentMap) &&
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
            character.CompletedQuests.Contains((int)entry.QuestId))
            return false;
        var matches = character.ActiveMainQuests.Where(quest => quest.Slot == entry.ActiveQuestSlot || quest.QuestId == entry.QuestId).ToArray();
        return matches.Length == 1 && matches[0].Slot == entry.ActiveQuestSlot && matches[0].QuestId == entry.QuestId &&
            matches[0].ConditionState == 1 && matches[0].AcceptanceId == entry.QuestAcceptanceId;
    }

    internal static bool TryValidateRoom(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared)
    {
        var entry = run.Entry; var death = prepared.Death; var clear = prepared.Result.RoomClearFact;
        return (run.IsPendingDeath(prepared) || run.IsConfirmedDeath(prepared)) &&
            prepared.Result.ClearFact is null && clear is not null && !entry.IsTutorial && !entry.IsClassExperience && !entry.IsPractice &&
            !entry.IsPractice && entry.CharacterId != 0 && entry.DungeonId > 0 && entry.BaseBossMap.Kind == "boss" &&
            (entry.QuestId == 0 ? entry.ActiveQuestSlot == byte.MaxValue && entry.QuestAcceptanceId == Guid.Empty :
                entry.ActiveQuestSlot <= 29 && entry.QuestAcceptanceId != Guid.Empty) &&
            prepared.Monster.IsBlocking && entry.QuestClearMap == clear.Map && !entry.IsTerminalMap(clear.Map) &&
            clear.Map == prepared.Result.Snapshot.CurrentMap && clear.DungeonId == entry.DungeonId &&
            prepared.Result.Snapshot.Phase == SinglePlayerDungeonPhase.Dungeon &&
            clear.Identity.CharacterId == entry.CharacterId && clear.Identity.QuestId == entry.QuestId &&
            clear.Identity.TaskAttempt > 0 && clear.Identity.InstanceId != Guid.Empty && death.Identity.Epoch < long.MaxValue &&
            clear.Identity == prepared.Result.Snapshot.Identity && clear.Identity == (death.Identity with { Epoch = death.Identity.Epoch + 1 }) &&
            death.EventId != Guid.Empty && clear.ConfirmedDeathEvents.Contains(death.EventId) &&
            !clear.ConfirmedDeathEvents.Contains(Guid.Empty) &&
            clear.ConfirmedDeathEvents.Count == clear.ConfirmedDeathEvents.Distinct().Count();
    }

    internal static bool TryValidateScene(SinglePlayerDungeonRun run, PreparedDungeonPassiveClear prepared, QuestSceneEvent scene)
    {
        var entry = run.Entry; var clear = prepared.Result.ClearFact; var source = prepared.Source;
        return (run.IsPendingPassiveClear(prepared) || run.IsCommittedPassiveClear(prepared)) &&
            entry.CharacterId != 0 && entry.DungeonId > 0 && entry.BaseBossMap.Kind == "boss" &&
            (entry.QuestId == 0 ? entry.ActiveQuestSlot == byte.MaxValue && entry.QuestAcceptanceId == Guid.Empty :
                entry.QuestId < 30000 && entry.ActiveQuestSlot <= 29 && entry.QuestAcceptanceId != Guid.Empty &&
                entry.QuestAcceptances.Any(row => row.QuestId == entry.QuestId && row.Slot == entry.ActiveQuestSlot && row.AcceptanceId == entry.QuestAcceptanceId)) &&
            (entry.IsEvildomWarHidden && entry.PartyRoute is not null && entry.QuestId == 0 && entry.RouteQuestId == 0 ||
             entry.RaidEntry is { Kind: DfoServer.Game.Raids.RaidKind.Luke, Phase: 1 } && entry.QuestId == 0 &&
             entry.RouteQuestId == 0 && clear?.ClearMap is { } energyMap && entry.LukeEnergyObject?.MatchesEnergy(
                 entry.DungeonId, entry.Maze, energyMap, scene.TemplateId, scene.SourceOrdinal) == true ||
             entry.RouteQuestId is > 0 and < 30000 &&
            (entry.PartyRoute is not null || entry.QuestAcceptances.Count(row => row.QuestId == entry.RouteQuestId) == 1 &&
            entry.QuestAcceptances.Any(row => row.QuestId == entry.RouteQuestId && row.Slot <= 29 && row.AcceptanceId != Guid.Empty))) &&
            source.OperationId != Guid.Empty && source.OperationId == scene.OperationId && source.Identity == scene.Identity &&
            source.TemplateId == scene.TemplateId && source.SourceOrdinal == scene.SourceOrdinal &&
            entry.Maze.ClearConditions is [{ Type: "destroy object", Count: 1 } target] && target.TargetId == scene.TemplateId &&
            scene.Identity.CharacterId == entry.CharacterId && scene.Identity.QuestId == entry.QuestId &&
            scene.Identity.InstanceId != Guid.Empty && scene.Identity.TaskAttempt > 0 && scene.Identity.Epoch < long.MaxValue &&
            scene.DungeonId == entry.DungeonId && scene.MapId > 0 && scene.Fingerprint.Length == 64 &&
            clear is { PassiveSource: not null, ClearMap: not null } && clear.PassiveSource == source &&
            clear.DungeonId == entry.DungeonId && clear.ClearMap.Id == scene.MapId && entry.IsTerminalMap(clear.ClearMap) &&
            prepared.Result.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement && clear.ClearMap == prepared.Result.Snapshot.CurrentMap &&
            clear.Identity == prepared.Result.Snapshot.Identity && clear.Identity == (scene.Identity with { Epoch = scene.Identity.Epoch + 1 }) &&
            !clear.ConfirmedDeathEvents.Contains(Guid.Empty) && clear.ConfirmedDeathEvents.Distinct().Count() == clear.ConfirmedDeathEvents.Count;
    }
}
