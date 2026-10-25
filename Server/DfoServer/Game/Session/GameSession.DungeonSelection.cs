using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    long dungeonSelectionId;
    public DungeonSelectionContext? CurrentDungeonSelection { get; private set; }

    public (int Layer, bool ClearedToday)? GriefTowerSelection(DungeonCatalog catalog)
    {
        int entrance = catalog.GriefTower.FloorsByDungeon.Values.Single(floor => floor.Layer == 1).DungeonId;
        return owner.Execute<(int, bool)?>(() =>
        {
            if (!Ready || Selected is not { } character ||
                !towns.Areas.TryGetValue((character.Position.Town,character.Position.Area),out var area) ||
                area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world,out var map) ||
                !map.Options.Any(option => option.DungeonId == entrance)) return null;
            var state = repository.ReadGriefTower(owner,character.Id).State;
            int day = QuestCycle.Day((timeProvider ?? TimeProvider.System).GetUtcNow());
            // 客户端楼层setter只接受1..100；通完100层的持久终止值101仍显示末层。
            return (Math.Min(state.Layer,catalog.GriefTower.TopLayer),state.CharacterDay == day && state.CharacterCleared);
        });
    }

    public int PrepareLegendDungeonSelection(DungeonCatalog dungeons, Func<int, int>? draw = null, DateTimeOffset? localNow = null)
    {
        var before = owner.Execute(() => (Selected, CurrentDungeonSelection, SelectionEpoch, Phase));
        if (before.Selected is not { } character || before.CurrentDungeonSelection is not { } selection ||
            before.Phase != SessionPhase.MapSelect || selection.IsTutorial || selection.IsClassExperience) return 0;
        if (selection.HellLegendDungeonId is { } frozen) return frozen;
        var done = QuestEligibility.Completed(character);
        var candidates = new List<int>();
        int worldMap = 0;
        if (towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) &&
            area.DungeonWorldMap is { } world && towns.DungeonMaps.TryGetValue(world, out var map) &&
            map.Error is null && map.HellDungeon && map.HellLegendDungeon && map.HellQuests.Concat(map.HellLegendQuests).All(done.Contains))
        {
            worldMap = world;
            foreach (int id in map.Options.Where(option => option.QuestId == -1 ||
                (option.InProgress ? character.ActiveMainQuests.Any(quest => quest.QuestId == option.QuestId) : done.Contains(option.QuestId)))
                .Select(option => option.DungeonId).Distinct().Order())
            {
                DfoServer.GameWorld.Dungeons.DungeonDefinition definition;
                try { definition = dungeons.Get(id); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException) { continue; }
                if (definition.MinimumLevel <= character.Level && definition.Mazes.Any(maze =>
                    maze.QuestConnectionTokens.Count == 0 && maze.HellParty is { LegendMapId: > 0 } && maze.HellLegendPath is not null))
                    candidates.Add(id);
            }
        }
        int selected = 0;
        if (candidates.Count > 0)
        {
            int index = draw?.Invoke(candidates.Count) ?? DungeonLegendSelection.DailyIndex(candidates.Count, worldMap,
                localNow ?? (timeProvider ?? TimeProvider.System).GetLocalNow());
            if (index < 0 || index >= candidates.Count) throw new InvalidDataException("星空裂缝目标随机索引越界");
            selected = candidates[index];
        }
        return owner.Execute(() =>
        {
            if (SelectionEpoch != before.SelectionEpoch || Phase != SessionPhase.MapSelect ||
                !ReferenceEquals(Selected, character) || !ReferenceEquals(CurrentDungeonSelection, selection)) return 0;
            CurrentDungeonSelection = selection with { HellLegendDungeonId = selected };
            return selected;
        });
    }

    internal bool CanBeginClearedHellSelection(uint dungeonIndex) => owner.Execute(() =>
        Ready && formalDungeon is { } dungeon && Selected is not null &&
        (dungeonIndex == 0 || dungeonIndex == dungeon.Entry.DungeonId) &&
        !dungeon.Ground.HasPendingOperation && !HasPendingFormalCard && pendingQuestCompletion is null &&
        !dungeon.HasPendingFatigue && !HasPendingQuestScene && !dungeon.Run.HasPendingDeath &&
        !dungeon.Run.HasPendingSettlement && dungeon.PendingTutorialClear is null &&
        (Phase == SessionPhase.Dungeon && FormalReceipt().HellPartyRoomCleared ||
            Phase == SessionPhase.Settlement && dungeon.Entry.IsCosmo && HasFinishedDungeon(dungeon)));

    public DungeonSelectionReceipt? BeginClearedHellSelection(uint dungeonIndex = 0) => owner.Execute<DungeonSelectionReceipt?>(() =>
    {
        // 时空祭坛回传当前副本编号；0兼容原选图入口，其他副本编号不能消费本次深渊出口。
        if (!CanBeginClearedHellSelection(dungeonIndex)) return null;
        if (ReturnFromFormalDungeonCore(true) is null) return null;
        // 原入口安全点已保存，直接建立选图上下文；不把深渊房间伪造成Boss结算。
        CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, Selected!.Id, Selected.Position, Selected.Position);
        SetState(SessionPhase.MapSelect);
        return new(Selected, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

    public DungeonSelectionReceipt? BeginDungeonSelection(bool partyAdmission = false) => owner.Execute<DungeonSelectionReceipt?>(() =>
    {
        if (!partyAdmission && HasMultiplePartyMembers?.Invoke() == true) return null;
        if (pendingRental is not null || HasPendingTownQuestCommit) return null;
        if (!Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.MapSelect))
            throw new InvalidDataException("Invalid dungeon-selection state");
        if (CurrentDungeonSelection is null || CurrentDungeonSelection.IsClassExperience)
        {
            var back = towns.SelectionReturn(Selected.Position, completed);
            if (back is null)
                return null;
            CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, Selected.Id, Selected.Position, back);
        }
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return new DungeonSelectionReceipt(Selected, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

    public DungeonSelectionReceipt? ReturnFromDungeonSelection(bool restorePosition = true) => owner.Execute(() =>
    {
        if (pendingRental is not null || HasPendingTownQuestCommit || CurrentDungeonSelection is null)
            return null;
        if (!Ready || Phase != SessionPhase.MapSelect)
            throw new InvalidOperationException("Dungeon-selection phase changed");
        ValidateDungeonSelection();
        // 先提交入口外落点，再消费选择身份；提交失败仍保留原MapSelect，不发送回城成功。
        if (CurrentDungeonSelection.IsTutorial && Selected!.Tutorial is { Completed: false })
            Selected = repository.CommitTutorialCheckpoint(owner, TutorialCheckpoint(Selected, "skip", Guid.NewGuid(), 0, 0,
                null, 0, true)).Character;
        else if (restorePosition && Selected!.Position != CurrentDungeonSelection.ReturnPosition)
            Selected = repository.Save(owner, Selected, CurrentDungeonSelection.ReturnPosition, Selected.Return);
        CurrentDungeonSelection = null;
        pendingQuestReplay = null;
        CancelDungeonContinuation();
        SetState(SessionPhase.Town);
        return new DungeonSelectionReceipt(Selected!, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

    public bool CanPublishDungeonSelection(DungeonSelectionReceipt receipt, bool returning = false) => owner.Execute(() =>
    {
        if (!Ready || receipt.Generation != owner.Generation || SelectionEpoch != receipt.SelectionEpoch ||
            Selected?.Id != receipt.Character.Id || dungeonSelectionId != receipt.SelectionId ||
            (returning ? Phase != SessionPhase.Town || CurrentDungeonSelection is not null :
                Phase != SessionPhase.MapSelect || CurrentDungeonSelection?.Id != receipt.SelectionId))
            return false;
        // 同阶段的 GM/资产更新也替换快照；不能用旧回城覆盖其投影，关闭后按存档恢复。
        if (!ReferenceEquals(Selected, receipt.Character))
            throw new InvalidOperationException("Dungeon-selection snapshot changed before publication");
        return true;
    });

    void ValidateDungeonSelection()
    {
        var selection = CurrentDungeonSelection!;
        if (selection.SelectionEpoch != SelectionEpoch || Selected?.Id != selection.CharacterId || Selected.Position != selection.OriginPosition)
            throw new InvalidOperationException("Dungeon-selection return position changed");
    }
}
