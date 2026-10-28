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
        projection.Epoch == SelectionEpoch && projection.CharacterId == Selected?.Id && ReferenceEquals(projection.Roster, selectedRoster));

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

    // 帝国竞技场的 1393/1394 只在已选编队非空时发送；空编队时客户端拿不到 runtimeActor(+4D8)，
    // 左上角只剩 "Tag" 占位圆圈、队伍设置与入场按钮都无反应。与再战魔界 126a8007 同源。
    // 入城时若编队为空且当前角色合格，就按「当前角色为队首 + 名册里第一名其他 90 级非决斗角色」自动配队并走原保存事务。
    // 已有编队、主角不合格、备用角色技能未初始化都不猜。
    public ImperialArenaProjection? EnsureImperialArenaTeam(IImperialArenaStore store, DateTimeOffset now)
    {
        var capture = owner.Execute<(ImperialArenaProjection? Primary, CharacterRosterEntry? Standby)>(() =>
        {
            if (ChannelEntry?.Type != 39 || ReadImperialArena(store, now) is not { } snapshot) return default;
            if (!snapshot.State.Configuration.IsEmpty || Selected is not { } selected) return default;
            if (selected.Id != snapshot.CharacterId || selected.Level < 90 || selected.IsPvpPrivate || !selected.Learning.Initialized)
                return default;
            var roster = snapshot.Roster.Entries;
            for (int index = 0; index < roster.Length; index++)
            {
                var row = roster[index];
                if (row.Id == selected.Id || row.Level < 90 || row.IsPvpPrivate) continue;
                return (snapshot, row);
            }
            return default;
        });
        if (capture.Primary is not { } primary || capture.Standby is not { } row) return null;
        // 1394要装的是备用角色的完整资料；技能未初始化或资格已变的角色装上去也没用，不落库。
        var standby = repository.LoadCharacter(owner, row.Slot);
        if (standby.Id != row.Id || standby.Level < 90 || standby.IsPvpPrivate || !standby.Learning.Initialized) return null;
        var desired = new ImperialArenaConfiguration(primary.State.Configuration.Revision, primary.CharacterId, row.Id);
        var saved = store.SaveImperialArenaConfiguration(owner, primary.State.Configuration, desired);
        imperialArenaPair = null;
        return primary with { State = primary.State with { Configuration = saved } };
    }

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

    // 已发布的双Actor是否仍可直接用于购票与入场；入场前据此决定要不要就地重建一次投影。
    public bool HasCurrentImperialArenaPair(IImperialArenaStore store, DateTimeOffset now) =>
        CurrentImperialArenaPair(store, now) is not null;

    ImperialArenaCharacters? CurrentImperialArenaPair(IImperialArenaStore store, DateTimeOffset now)
    {
        if (!Ready || ChannelEntry?.Type != 39 || Selected is not { } current || imperialArenaPair is not { } pair ||
            pair.Characters.Projection.Generation != owner.Generation || pair.Characters.Projection.Epoch != SelectionEpoch ||
            !ReferenceEquals(pair.Characters.Projection.Roster, selectedRoster)) return null;
        // 与再战魔界同源：1394只安装备用角色，主角是客户端已持有的真实Actor。走位、修理、金币与体力
        // 恢复只改主角自己的记录；按整条记录判等会让一次装备修理把投影判死，此后购票与入场永久失败。
        if (current.Id != pair.Characters.Primary.Id ||
            current.Inventory.AccountWallet.Revision != pair.PrimaryAppearance.WalletRevision ||
            !pair.StandbyAppearance.Matches(pair.Characters.Standby, now)) return null;
        var rebased = pair.Characters with { Primary = current };
        return store.MatchesImperialArenaCharacters(owner, rebased) ? rebased : null;
    }

    // 最近一次入场被拒的原因。只用于拒绝日志，不参与准入判定。
    public string? ImperialArenaEntryReject { get; private set; }

    public DungeonSelectionReceipt? BeginImperialArenaSelection(uint dungeonId, DungeonCatalog dungeons,
        ImperialArenaCatalog catalog, IImperialArenaStore store)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var mode = dungeonId switch { 320 => ImperialArenaMode.Normal, 336 => ImperialArenaMode.Hard, _ => ImperialArenaMode.None };
        if (mode == ImperialArenaMode.None) return ImperialArenaReject("unsupported-dungeon");
        if (mode == ImperialArenaMode.Hard && ImperialArenaPeriod.RankingClosed(now)) return ImperialArenaReject("ranking-closed");
        if (!ImperialArenaEntryPolicy.Supports(dungeons.Get(checked((int)dungeonId)))) return ImperialArenaReject("unsupported-dungeon-policy");
        var route = store.ReadImperialArenaRoute(owner, catalog, ImperialArenaPeriod.At(now));
        return owner.Execute(() =>
        {
            DungeonSelectionReceipt? Reject(string reason) => ImperialArenaReject(reason);
            if (Phase is not (SessionPhase.Town or SessionPhase.MapSelect)) return Reject("phase");
            if (formalDungeon is not null) return Reject("dungeon-active");
            if (HasPendingTownQuestCommit) return Reject("quest-commit-pending");
            if (pendingRental is not null) return Reject("rental-pending");
            if (HasMultiplePartyMembers?.Invoke() == true) return Reject("party");
            if (Selected?.Position is not { Town: 48, Area: 0 }) return Reject("position");
            if (!towns.IsPosition(Selected.Position, QuestEligibility.Completed(Selected))) return Reject("position-not-walkable");
            if (CurrentImperialArenaPair(store, now) is not { } characters) return Reject("pair-unavailable");
            var state = store.ReadImperialArenaState(owner, route.Period);
            if (state.PaidMode != mode) return Reject("ticket-not-purchased");
            characters = characters with { Projection = characters.Projection with { State = state } };
            var admission = new ImperialArenaAdmission(characters, mode, route, now.ToUnixTimeSeconds(), catalog.MinimumHardClearMilliseconds);
            admission.Validate();
            var receipt = BeginDungeonSelection();
            if (receipt is null) return Reject("selection-unavailable");
            imperialArenaSelection = new(admission, CurrentDungeonSelection!, store, catalog);
            return receipt;
        });
    }

    DungeonSelectionReceipt? ImperialArenaReject(string reason)
    {
        ImperialArenaEntryReject = reason;
        return null;
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
