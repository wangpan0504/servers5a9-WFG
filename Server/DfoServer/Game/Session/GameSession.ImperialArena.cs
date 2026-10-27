using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.ImperialArena;
using DfoServer.Game.Quests;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed record ImperialArenaPublishedPair(ImperialArenaCharacters Characters,
    TagEvildomAppearance PrimaryAppearance, TagEvildomAppearance StandbyAppearance);

internal sealed partial class GameSession
{
    bool IsImperialArenaCombat => formalDungeon?.Entry.IsImperialArena == true;

    void CompleteCurrentImperialArena()
    {
        if (formalDungeon?.ImperialArenaEntry is { } entry)
            entry.Store.CompleteImperialArenaRuns(owner, formalDungeon.Run.Snapshot.Identity.InstanceId);
    }

    ImperialArenaPublishedPair? imperialArenaPair;
    sealed record ImperialArenaEntrySelection(ImperialArenaAdmission Admission,
        DungeonSelectionContext Selection, IImperialArenaStore Store, ImperialArenaCatalog Catalog);
    ImperialArenaEntrySelection? imperialArenaSelection;

    public ImperialArenaProjection? ReadImperialArena(IImperialArenaStore store, DateTimeOffset now) => owner.Execute(() =>
    {
        if (ChannelEntry?.Type != 39 || PublishedTagEvildomRoster() is not { } roster ||
            ChannelEntry.Identity.ServerIndex == 0) return null;
        byte server = ChannelEntry.Identity.ServerIndex;
        return new ImperialArenaProjection(store.ReadImperialArenaState(owner, ImperialArenaPeriod.At(now)), server,
            roster, owner.Generation, SelectionEpoch, Selected!.Id);
    });

    public bool CanPublishImperialArena(ImperialArenaProjection projection) => owner.Execute(() =>
        Ready && Phase == SessionPhase.Town && ChannelEntry?.Type == 39 && projection.Generation == owner.Generation &&
        projection.Epoch == SelectionEpoch && projection.CharacterId == Selected?.Id && ReferenceEquals(projection.Roster, tagEvildomRoster));

    public ImperialArenaCharacters? CaptureImperialArenaCharacters(ImperialArenaProjection projection) => owner.Execute(() =>
    {
        if (!CanPublishImperialArena(projection) || Selected is not { } primary ||
            projection.State.Configuration is not { IsEmpty: false } configuration || configuration.First != primary.Id ||
            primary.Level < 90 || primary.IsPvpPrivate || !primary.Learning.Initialized) return null;
        var row = projection.Roster.Entries.SingleOrDefault(row => row.Id == configuration.Second);
        if (row is null) return null;
        var standby = repository.LoadCharacter(owner, row.Slot);
        if (standby.Id != row.Id || standby.Name != row.Name || standby.Level < 90 || standby.IsPvpPrivate || !standby.Learning.Initialized) return null;
        return new ImperialArenaCharacters(projection, primary, standby);
    });

    public ImperialArenaProjection? ConfigureImperialArena(IImperialArenaStore store, byte firstServer, byte firstIndex,
        byte secondServer, byte secondIndex, DateTimeOffset now) => owner.Execute(() =>
    {
        if (ReadImperialArena(store, now) is not { } snapshot || firstServer != snapshot.Server || secondServer != snapshot.Server ||
            firstIndex >= snapshot.Roster.Entries.Length || secondIndex >= snapshot.Roster.Entries.Length || firstIndex == secondIndex)
            return null;
        var ids = new[] { snapshot.Roster.Entries[firstIndex], snapshot.Roster.Entries[secondIndex] };
        var current = repository.ListRoster(owner);
        if (ids.Any(row => row.Level < 90 || row.IsPvpPrivate || !current.Any(actual => actual.Id == row.Id && actual.Slot == row.Slot && actual.Name == row.Name))) return null;
        var desired = new ImperialArenaConfiguration(snapshot.State.Configuration.Revision, ids[0].Id, ids[1].Id);
        var saved = store.SaveImperialArenaConfiguration(owner, snapshot.State.Configuration, desired);
        imperialArenaPair = null;
        imperialArenaSelection = null;
        return snapshot with { State = snapshot.State with { Configuration = saved } };
    });

    public ImperialArenaProjection? PurchaseImperialArena(IImperialArenaStore store, ImperialArenaCatalog catalog,
        ImperialArenaMode mode, DateTimeOffset now) => owner.Execute(() =>
    {
        if (ReadImperialArena(store, now) is not { } snapshot || Selected?.Position is not { Town: 48, Area: 0 } ||
            !CanPublishImperialArena(snapshot) || CurrentImperialArenaPair(store, now) is null ||
            HasPendingTownQuestCommit || pendingRental is not null || HasMultiplePartyMembers?.Invoke() == true ||
            mode == ImperialArenaMode.Hard && ImperialArenaPeriod.RankingClosed(now)) return null;
        Selected = store.PurchaseImperialArenaEntry(owner, Selected, mode, snapshot.State.Period, catalog.EntryGold);
        imperialArenaPair = null;
        return ReadImperialArena(store, now);
    });

    public bool CanPublishImperialArenaCharacters(IImperialArenaStore store, ImperialArenaCharacters characters) => owner.Execute(() =>
        CanPublishImperialArena(characters.Projection) && ReferenceEquals(Selected, characters.Primary) &&
        store.MatchesImperialArenaCharacters(owner, characters));

    public bool MarkImperialArenaPairPublished(IImperialArenaStore store, ImperialArenaCharacters characters,
        TagEvildomAppearance primary, TagEvildomAppearance standby, DateTimeOffset now) => owner.Execute(() =>
    {
        if (!CanPublishImperialArenaCharacters(store, characters) || !primary.Matches(characters.Primary, now) ||
            !standby.Matches(characters.Standby, now)) return false;
        imperialArenaPair = new(characters, primary, standby);
        return true;
    });

    ImperialArenaCharacters? CurrentImperialArenaPair(IImperialArenaStore store, DateTimeOffset now)
    {
        if (!Ready || ChannelEntry?.Type != 39 || Selected is not { } current || imperialArenaPair is not { } pair ||
            pair.Characters.Projection.Generation != owner.Generation || pair.Characters.Projection.Epoch != SelectionEpoch ||
            !ReferenceEquals(pair.Characters.Projection.Roster, tagEvildomRoster)) return null;
        var original = pair.Characters.Primary;
        if ((current with { Revision = original.Revision, Position = original.Position, Return = original.Return }) != original ||
            !pair.PrimaryAppearance.Matches(current, now) || !pair.StandbyAppearance.Matches(pair.Characters.Standby, now)) return null;
        var rebased = pair.Characters with { Primary = current };
        return store.MatchesImperialArenaCharacters(owner, rebased) ? rebased : null;
    }

    public DungeonSelectionReceipt? BeginImperialArenaSelection(uint dungeonId, DungeonCatalog dungeons,
        ImperialArenaCatalog catalog, IImperialArenaStore store)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var mode = dungeonId switch { 320 => ImperialArenaMode.Normal, 336 => ImperialArenaMode.Hard, _ => ImperialArenaMode.None };
        if (mode == ImperialArenaMode.None || mode == ImperialArenaMode.Hard && ImperialArenaPeriod.RankingClosed(now)) return null;
        if (!ImperialArenaEntryPolicy.Supports(dungeons.Get(checked((int)dungeonId)))) return null;
        var route = store.ReadImperialArenaRoute(owner, catalog, ImperialArenaPeriod.At(now));
        return owner.Execute(() =>
        {
            if (Phase is not (SessionPhase.Town or SessionPhase.MapSelect) || formalDungeon is not null || HasPendingTownQuestCommit ||
                pendingRental is not null || HasMultiplePartyMembers?.Invoke() == true || Selected?.Position is not { Town: 48, Area: 0 } ||
                !towns.IsPosition(Selected.Position, QuestEligibility.Completed(Selected)) || CurrentImperialArenaPair(store, now) is not { } characters)
                return null;
            var state = store.ReadImperialArenaState(owner, route.Period);
            if (state.PaidMode != mode) return null;
            characters = characters with { Projection = characters.Projection with { State = state } };
            var admission = new ImperialArenaAdmission(characters, mode, route, now.ToUnixTimeSeconds(), catalog.MinimumHardClearMilliseconds);
            admission.Validate();
            var receipt = BeginDungeonSelection();
            if (receipt is null) return null;
            imperialArenaSelection = new(admission, CurrentDungeonSelection!, store, catalog);
            return receipt;
        });
    }

    ImperialArenaEntrySelection? CurrentImperialArenaSelection(Character expected, long epoch, uint dungeonId) => owner.Execute(() =>
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return imperialArenaSelection is { } choice && choice.Admission.DungeonId == dungeonId && SelectionEpoch == epoch &&
            ReferenceEquals(choice.Selection, CurrentDungeonSelection) && Phase == SessionPhase.MapSelect &&
            ReferenceEquals(choice.Admission.Primary, expected) && choice.Admission.Route.Period == ImperialArenaPeriod.At(now) &&
            !(choice.Admission.Mode == ImperialArenaMode.Hard && ImperialArenaPeriod.RankingClosed(now)) &&
            CurrentImperialArenaPair(choice.Store, now) is not null ? choice : null;
    });
}
