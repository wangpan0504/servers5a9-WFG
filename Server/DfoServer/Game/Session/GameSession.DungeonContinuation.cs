using DfoServer.Game.Dungeons;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record PendingDungeonContinuation(FormalDungeonReceipt Previous, PreparedFormalDungeon? Dungeon,
        TownTravelReceipt? Return = null, FormalDungeonReceipt? Entered = null);
    PendingDungeonContinuation? pendingDungeonContinuation;

    public FormalDungeonReceipt? BeginFormalDungeonQuestContinuation(FormalDungeonReceipt current, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonMonsterDropCatalog drops, ItemCatalog items, EpicPieceCatalog? pieces, out string reason)
    {
        reason = "settlement-changed";
        if (!owner.Execute(() => Ready && CanEnterFormalDungeon(current)) || questCatalog is null) return null;
        var character = current.Character;
        bool story = current.Entry.RouteQuestId > 0 &&
            current.Entry.Maze.HasActiveQuestConnection(checked((int)current.Entry.RouteQuestId));
        DungeonQuestContinuation? next = null;
        if (!towns.Areas.TryGetValue((character.Position.Town,character.Position.Area),out var area) ||
            area.DungeonWorldMap is not { } worldMap || !towns.DungeonMaps.TryGetValue(worldMap,out var map) || map.Error is not null)
            reason = "successor-world-map-unavailable";
        else
            next = DungeonQuestContinuationRules.Resolve(character,current.Entry,questCatalog,dungeons,
                map.Options.Select(row => row.DungeonId),out reason);
        if (next is null && !story) return null;
        // 有合法已接取主线才准备下一图；剧情无合法路线按普通回城完成握手。
        PreparedFormalDungeon? prepared = null;
        try
        {
            if (next is { DungeonId: > 0 }) prepared = PrepareFormalDungeon(next.DungeonId,next.Quest.QuestId,next.Difficulty,
                dungeons,rooms,drops,items,null,pieces,character,current);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException)
        { reason = "successor-entry-unavailable"; }
        if (next is { DungeonId: > 0 } && prepared is null) reason = "successor-entry-unavailable";
        return owner.Execute(() =>
        {
            if (!Ready || !CanEnterFormalDungeon(current) || next is not null &&
                !Selected!.ActiveMainQuests.Any(row => row.AcceptanceId == next.Quest.AcceptanceId && row.QuestId == next.Quest.QuestId)) return null;
            pendingDungeonContinuation = new(current,prepared);
            return current;
        });
    }

    // 先确认72{1,3}，再发布24；反序会由ACK72覆盖24设置的下一场景选项。
    public (TownTravelReceipt? Receipt, bool Duplicate) ConfirmFormalDungeonContinuationBoundary() => owner.Execute(() =>
    {
        if (pendingDungeonContinuation is not { } pending) return ((TownTravelReceipt?)null,false);
        if (pending.Return is { } previous)
            return Ready && Phase == SessionPhase.Town && formalDungeon is null && previous.Generation == owner.Generation &&
                previous.SelectionEpoch == SelectionEpoch && ReferenceEquals(Selected,previous.Character)
                ? (previous,true) : ((TownTravelReceipt?)null,false);
        if (!Ready || !CanEnterFormalDungeon(pending.Previous)) return ((TownTravelReceipt?)null,false);
        var returning = ReturnFromFormalDungeon();
        if (returning is not null) pendingDungeonContinuation = pending with { Return = returning };
        return (returning,false);
    });

    // external 的真实24→客户端15边界：15只确认旧场景结束，不重新打开普通选图。
    public (bool Pending, FormalDungeonReceipt? Dungeon, TownTravelReceipt? Town, FormalDungeonReceipt? Replay) CompleteFormalDungeonContinuation() => owner.Execute(() =>
    {
        if (pendingDungeonContinuation is not { } pending) return (false,(FormalDungeonReceipt?)null,(TownTravelReceipt?)null,(FormalDungeonReceipt?)null);
        if (pending.Entered is { } installed)
            return Ready && Phase == SessionPhase.Loading && CanPublishFormalDungeon(installed)
                ? (true,(FormalDungeonReceipt?)null,(TownTravelReceipt?)null,installed)
                : (true,(FormalDungeonReceipt?)null,(TownTravelReceipt?)null,(FormalDungeonReceipt?)null);
        if (pending.Return is not { } returning) return (true,(FormalDungeonReceipt?)null,(TownTravelReceipt?)null,(FormalDungeonReceipt?)null);
        if (!Ready || Phase != SessionPhase.Town || formalDungeon is not null || returning.Generation != owner.Generation ||
            returning.SelectionEpoch != SelectionEpoch || !ReferenceEquals(Selected,returning.Character))
        {
            pendingDungeonContinuation = null;
            return (false,(FormalDungeonReceipt?)null,(TownTravelReceipt?)null,(FormalDungeonReceipt?)null);
        }
        if (pending.Dungeon is null)
        {
            pendingDungeonContinuation = null;
            return (true,(FormalDungeonReceipt?)null,returning,(FormalDungeonReceipt?)null);
        }
        var entered = InstallFormalDungeon(pending.Dungeon);
        if (entered is not null) pendingDungeonContinuation = pending with { Entered = entered };
        return (true,entered,(TownTravelReceipt?)null,(FormalDungeonReceipt?)null);
    });
}
