using DfoServer.Game.Dungeons;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record PendingDungeonContinuation(FormalDungeonReceipt Previous, PreparedFormalDungeon? Dungeon,
        TownTravelReceipt? Return = null, FormalDungeonReceipt? Entered = null)
    {
        public DungeonSelectionContext? Selection { get; init; }
        public bool Cancelled { get; init; }
    }
    PendingDungeonContinuation? pendingDungeonContinuation;
    bool HasActiveDungeonContinuation => pendingDungeonContinuation is { Cancelled: false };
    public bool HasDungeonContinuationRequest() => owner.Execute(() => pendingDungeonContinuation is not null);

    // 保留一个有界终态拦截迟到16；下一次合法15或换角才消费，普通城镇操作不受阻。
    void CancelDungeonContinuation()
    {
        if (pendingDungeonContinuation is { } pending)
            pendingDungeonContinuation = pending with { Cancelled = true, Dungeon = null, Return = null, Entered = null, Selection = null };
    }

    public bool ConfirmNewDungeonSelection(DungeonSelectionReceipt receipt) => owner.Execute(() =>
    {
        if (!CanPublishDungeonSelection(receipt)) return false;
        if (pendingDungeonContinuation is { Cancelled: true })
            pendingDungeonContinuation = null;
        return true;
    });

    public FormalDungeonReceipt? BeginFormalDungeonQuestContinuation(FormalDungeonReceipt current, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonMonsterDropCatalog drops, ItemCatalog items, EpicPieceCatalog? pieces,
        out bool returnToTown, out string reason)
    {
        returnToTown = false;
        reason = "settlement-changed";
        var original = owner.Execute(() => Ready && CanEnterFormalDungeon(current) ? formalDungeon : null);
        if (original is null || questCatalog is null) return null;
        var character = current.Character;
        bool taskRoute = current.Entry.RouteQuestId > 0;
        DungeonQuestContinuation? next = null;
        if (!towns.Areas.TryGetValue((character.Position.Town,character.Position.Area),out var area) ||
            area.DungeonWorldMap is not { } worldMap || !towns.DungeonMaps.TryGetValue(worldMap,out var map) || map.Error is not null)
            reason = "successor-world-map-unavailable";
        else
            next = DungeonQuestContinuationRules.Resolve(character,current.Entry,questCatalog,dungeons,
                map.Options.Select(row => row.DungeonId),out reason);
        if (next is null && !taskRoute && character.ActiveMainQuests.Length != 0) return null;
        // 任务路线或没有活动任务时，无合法后继直接回城；普通选其他副本保留原入口。
        PreparedFormalDungeon? prepared = null;
        try
        {
            if (next is { DungeonId: > 0 }) prepared = PrepareFormalDungeon(next.DungeonId,next.Quest.QuestId,next.Difficulty,
                dungeons,rooms,drops,items,original.Draw,pieces,character,current,original.PrepareDrops);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException)
        { reason = "successor-entry-unavailable"; }
        if (next is { DungeonId: > 0 } && prepared is null) reason = "successor-entry-unavailable";
        var receipt = owner.Execute(() =>
        {
            if (!Ready || !CanEnterFormalDungeon(current) || next is not null &&
                !Selected!.ActiveMainQuests.Any(row => row.AcceptanceId == next.Quest.AcceptanceId && row.QuestId == next.Quest.QuestId)) return null;
            if (prepared is not null) pendingDungeonContinuation = new(current,prepared);
            return current;
        });
        returnToTown = receipt is not null && prepared is null;
        return receipt;
    }

    // 在72{1,1}结束旧场景，保留后继规划交给客户端24后的真实15。
    public TownTravelReceipt? LeaveFormalDungeonForContinuation() => owner.Execute(() =>
    {
        if (pendingDungeonContinuation is not { } pending) return null;
        if (pending.Return is { } previous)
            return Ready && Phase == SessionPhase.Town && formalDungeon is null && previous.Generation == owner.Generation &&
                previous.SelectionEpoch == SelectionEpoch && ReferenceEquals(Selected,previous.Character)
                ? previous : null;
        if (!Ready || !CanEnterFormalDungeon(pending.Previous)) return null;
        var returning = ReturnFromFormalDungeon();
        if (returning is not null) pendingDungeonContinuation = pending with { Return = returning };
        return returning;
    });

    // 连图退场中的1,3是旧结算页尾包；回复ACK72会重新写入旧页面的操作状态。
    public bool HasFormalDungeonContinuation() => owner.Execute(() =>
        pendingDungeonContinuation is { Cancelled: false, Return: { } returning } && Ready &&
        returning.Generation == owner.Generation && returning.SelectionEpoch == SelectionEpoch &&
        (Phase is SessionPhase.Town or SessionPhase.MapSelect && ReferenceEquals(Selected,returning.Character) ||
            pendingDungeonContinuation.Entered is { } entered && CanPublishFormalDungeon(entered)));

    // 15只建立客户端选图上下文；真实16复验原规划后才提交attempt并发布28/29。
    public (bool Pending, DungeonSelectionReceipt? Selection, bool Opened, FormalDungeonReceipt? Replay) CompleteFormalDungeonContinuation(uint dungeonId = 0) => owner.Execute(() =>
    {
        if (pendingDungeonContinuation is not { Cancelled: false } pending) return (false,(DungeonSelectionReceipt?)null,false,(FormalDungeonReceipt?)null);
        if (pending.Entered is { } installed)
            return Ready && Phase == SessionPhase.Loading && CanPublishFormalDungeon(installed)
                ? (true,(DungeonSelectionReceipt?)null,false,installed)
                : (true,(DungeonSelectionReceipt?)null,false,(FormalDungeonReceipt?)null);
        if (pending.Return is not { } returning) return (true,(DungeonSelectionReceipt?)null,false,(FormalDungeonReceipt?)null);
        if (!Ready || Phase is not (SessionPhase.Town or SessionPhase.MapSelect) || formalDungeon is not null || returning.Generation != owner.Generation ||
            returning.SelectionEpoch != SelectionEpoch || !ReferenceEquals(Selected,returning.Character) ||
            pending.Dungeon is not { } prepared || !ReferenceEquals(prepared.Character,Selected) || prepared.SelectionEpoch != SelectionEpoch)
        {
            CancelDungeonContinuation();
            return (true,(DungeonSelectionReceipt?)null,false,(FormalDungeonReceipt?)null);
        }
        if (dungeonId != 0 && dungeonId != prepared.Dungeon.Entry.DungeonId)
            return (true,(DungeonSelectionReceipt?)null,false,(FormalDungeonReceipt?)null);
        bool opened = pending.Selection is null;
        if (opened)
        {
            if (Phase != SessionPhase.Town || CurrentDungeonSelection is not null)
                return (true,(DungeonSelectionReceipt?)null,false,(FormalDungeonReceipt?)null);
            CurrentDungeonSelection = new(++dungeonSelectionId,SelectionEpoch,Selected!.Id,Selected.Position,Selected.Position);
            pendingDungeonContinuation = pending with { Selection = CurrentDungeonSelection };
        }
        else if (Phase != SessionPhase.MapSelect || !ReferenceEquals(CurrentDungeonSelection,pending.Selection))
            return (true,(DungeonSelectionReceipt?)null,false,(FormalDungeonReceipt?)null);
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return (true,new DungeonSelectionReceipt(Selected!,owner.Generation,SelectionEpoch,dungeonSelectionId),opened,(FormalDungeonReceipt?)null);
    });

    public FormalDungeonReceipt? EnterFormalDungeonContinuation(uint dungeonId,uint questId,byte difficulty) => owner.Execute(() =>
    {
        if (pendingDungeonContinuation is not { Cancelled: false, Dungeon: { } prepared, Return: { } returning, Selection: { } selection } pending ||
            !Ready || Phase != SessionPhase.MapSelect || formalDungeon is not null || returning.Generation != owner.Generation ||
            returning.SelectionEpoch != SelectionEpoch || prepared.SelectionEpoch != SelectionEpoch ||
            !ReferenceEquals(Selected,returning.Character) || !ReferenceEquals(Selected,prepared.Character) ||
            !ReferenceEquals(CurrentDungeonSelection,selection) || selection.Id != dungeonSelectionId ||
            selection.SelectionEpoch != SelectionEpoch || selection.CharacterId != Selected!.Id ||
            selection.OriginPosition != Selected.Position) return null;
        var entry = prepared.Dungeon.Entry;
        if (entry.DungeonId != dungeonId || entry.QuestId != questId || entry.Difficulty != difficulty ||
            !Selected.ActiveMainQuests.Any(active => active.QuestId == entry.QuestId && active.Slot == entry.ActiveQuestSlot &&
                active.AcceptanceId == entry.QuestAcceptanceId && active.AcceptanceId != Guid.Empty)) return null;
        var entered = InstallFormalDungeon(prepared);
        if (entered is not null) pendingDungeonContinuation = pending with { Entered = entered };
        return entered;
    });
}
