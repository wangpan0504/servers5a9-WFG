using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record TagEvildomEntrySelection(TagEvildomAdmission Admission, int MazeOrdinal,
        DungeonSelectionContext Selection, ITagEvildomConfigurationStore Store);
    TagEvildomEntrySelection? tagEvildomSelection;

    // 最近一次入场被拒的原因。只用于拒绝日志，不参与准入判定。
    public string? TagEvildomEntryReject { get; private set; }

    public DungeonSelectionReceipt? BeginTagEvildomSelection(uint dungeonId, DungeonCatalog dungeons,
        TagEvildomRewardCatalog rewards, ITagEvildomConfigurationStore store, Func<int, int>? draw = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        if (!TagEvildomSchedule.CanEnter(clock.GetUtcNow())) return TagEvildomReject("schedule-closed");
        var capture = owner.Execute(() =>
        {
            (TagEvildomCharacters Pair, TagEvildomState State, TagEvildomEntrySelection? Previous) Reject(string reason)
            {
                TagEvildomEntryReject = reason;
                return default;
            }
            var now = clock.GetUtcNow();
            if (Phase is not (SessionPhase.Town or SessionPhase.MapSelect)) return Reject("phase");
            if (formalDungeon is not null) return Reject("dungeon-active");
            if (HasPendingTownQuestCommit) return Reject("quest-commit-pending");
            if (pendingRental is not null) return Reject("rental-pending");
            if (HasMultiplePartyMembers?.Invoke() == true) return Reject("party");
            if (Selected?.Position is not { Town: 62, Area: 0 }) return Reject("position");
            if (!towns.IsPosition(Selected.Position, QuestEligibility.Completed(Selected))) return Reject("position-not-walkable");
            if (!towns.Areas[(62, 0)].Map(QuestEligibility.Completed(Selected)).TagDungeonPopup) return Reject("popup-disabled");
            if (CurrentTagEvildomPair(store, now) is not { } pair) return Reject("pair-unavailable");
            var state = store.ReadTagEvildomState(owner, TagEvildomPeriod.At(now));
            if (state.UsedThisWeek >= rewards.WeeklyLimit) return Reject("weekly-limit");
            if (state.ProtectedMembers.Contains(pair.Primary.Id) || state.ProtectedMembers.Contains(pair.Standby.Id))
                return Reject("protected-member");
            if (towns.DestinationAdmission(Selected.Position, Selected.Level, QuestEligibility.Completed(Selected)) != "ready")
                return Reject("destination-admission");
            return (Pair: pair, State: state, Previous: tagEvildomSelection);
        });
        // TownUI的select_mode本地选择3/4，没有独立请求；15的两个已证DGN在同一准入中冻结，不能锁死1678初值。
        if (capture.Pair is not { } characters) return null;
        if (dungeonId is not (9608 or 400000095)) return TagEvildomReject("unsupported-dungeon");
        var definition = dungeons.Get(checked((int)dungeonId));
        if (!TagEvildomEntryPolicy.Supports(definition)) return TagEvildomReject("unsupported-dungeon-policy");
        int ordinal;
        var day = TagEvildomPeriod.At(clock.GetUtcNow()).Day;
        if (store.ReadTagEvildomRoute(owner, day, capture.State.Configuration.SelectedTeam) is int saved) ordinal = saved;
        else if (capture.Previous is { } previous &&
            ReferenceEquals(previous.Selection, CurrentDungeonSelection)) ordinal = previous.MazeOrdinal;
        else
        {
            int index = (draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32)(definition.Mazes.Count);
            if (index < 0 || index >= definition.Mazes.Count) throw new InvalidDataException("TAG迷宫随机索引越界");
            ordinal = definition.Mazes[index].SourceOrdinal;
        }
        return owner.Execute(() =>
        {
            DungeonSelectionReceipt? Reject(string reason) => TagEvildomReject(reason);
            if (!TagEvildomSchedule.CanEnter(clock.GetUtcNow())) return Reject("schedule-closed");
            if (TagEvildomPeriod.At(clock.GetUtcNow()).Day != day) return Reject("period-changed");
            if (CurrentTagEvildomPair(store, clock.GetUtcNow()) is not { } current) return Reject("pair-unavailable");
            if (!ReferenceEquals(current.Primary, characters.Primary)) return Reject("primary-changed");
            if (current.Projection.State.Configuration != characters.Projection.State.Configuration) return Reject("configuration-changed");
            if (!ReferenceEquals(tagEvildomSelection, capture.Previous)) return Reject("selection-changed");
            var receipt = BeginDungeonSelection();
            if (receipt is null) return Reject("selection-unavailable");
            var admission = new TagEvildomAdmission(capture.State.Configuration, characters.Primary, characters.Standby,
                checked((int)dungeonId), rewards.ResourceKey, clock.GetUtcNow().ToUnixTimeSeconds()) { MazeOrdinal = ordinal };
            admission.Validate();
            tagEvildomSelection = new(admission, ordinal, CurrentDungeonSelection!, store);
            return receipt;
        });
    }

    DungeonSelectionReceipt? TagEvildomReject(string reason)
    {
        TagEvildomEntryReject = reason;
        return null;
    }

    TagEvildomEntrySelection? CurrentTagEvildomSelection(Character expected, long epoch, uint dungeonId) => owner.Execute(() =>
        tagEvildomSelection is { } choice && choice.Admission.DungeonId == dungeonId && SelectionEpoch == epoch &&
        ReferenceEquals(choice.Selection, CurrentDungeonSelection) && Phase == SessionPhase.MapSelect &&
        ReferenceEquals(choice.Admission.Primary, expected) &&
        TagEvildomSchedule.CanEnter((timeProvider ?? TimeProvider.System).GetUtcNow()) &&
        TagEvildomPeriod.At((timeProvider ?? TimeProvider.System).GetUtcNow()) ==
            TagEvildomPeriod.At(DateTimeOffset.FromUnixTimeSeconds(choice.Admission.AdmittedAt)) &&
        CurrentTagEvildomPair(choice.Store, (timeProvider ?? TimeProvider.System).GetUtcNow()) is not null ? choice : null);
}
