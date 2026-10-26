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

    public DungeonSelectionReceipt? BeginTagEvildomSelection(uint dungeonId, DungeonCatalog dungeons,
        TagEvildomRewardCatalog rewards, ITagEvildomConfigurationStore store, Func<int, int>? draw = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var capture = owner.Execute(() =>
        {
            if (Phase is not (SessionPhase.Town or SessionPhase.MapSelect) || formalDungeon is not null ||
                HasPendingTownQuestCommit || pendingRental is not null || HasMultiplePartyMembers?.Invoke() == true ||
                Selected?.Position is not { Town: 62, Area: 0 } ||
                !towns.IsPosition(Selected.Position, QuestEligibility.Completed(Selected)) ||
                !towns.Areas[(62, 0)].Map(QuestEligibility.Completed(Selected)).TagDungeonPopup || CurrentTagEvildomPair(store, clock.GetUtcNow()) is not { } pair ||
                towns.DestinationAdmission(Selected.Position, Selected.Level, QuestEligibility.Completed(Selected)) != "ready") return default;
            var state = store.ReadTagEvildomState(owner, TagEvildomPeriod.At(clock.GetUtcNow()));
            if (state.UsedThisWeek >= rewards.WeeklyLimit || state.ProtectedMembers.Contains(pair.Primary.Id) ||
                state.ProtectedMembers.Contains(pair.Standby.Id)) return default;
            return (Pair: pair, State: state, Previous: tagEvildomSelection);
        });
        // TownUI的select_mode本地选择3/4，没有独立请求；15的两个已证DGN在同一准入中冻结，不能锁死1678初值。
        if (capture.Pair is not { } characters || dungeonId is not (9608 or 400000095)) return null;
        var definition = dungeons.Get(checked((int)dungeonId));
        if (!TagEvildomEntryPolicy.Supports(definition)) return null;
        int ordinal;
        if (capture.Previous is { } previous && previous.Admission.DungeonId == dungeonId &&
            ReferenceEquals(previous.Selection, CurrentDungeonSelection)) ordinal = previous.MazeOrdinal;
        else
        {
            int index = (draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32)(definition.Mazes.Count);
            if (index < 0 || index >= definition.Mazes.Count) throw new InvalidDataException("TAG迷宫随机索引越界");
            ordinal = definition.Mazes[index].SourceOrdinal;
        }
        return owner.Execute(() =>
        {
            if (CurrentTagEvildomPair(store, clock.GetUtcNow()) is not { } current || !ReferenceEquals(current.Primary, characters.Primary) ||
                current.Projection.State.Configuration != characters.Projection.State.Configuration ||
                !ReferenceEquals(tagEvildomSelection, capture.Previous)) return null;
            var receipt = BeginDungeonSelection();
            if (receipt is null) return null;
            var admission = new TagEvildomAdmission(capture.State.Configuration, characters.Primary, characters.Standby,
                checked((int)dungeonId), rewards.ResourceKey, clock.GetUtcNow().ToUnixTimeSeconds());
            admission.Validate();
            tagEvildomSelection = new(admission, ordinal, CurrentDungeonSelection!, store);
            return receipt;
        });
    }

    TagEvildomEntrySelection? CurrentTagEvildomSelection(Character expected, long epoch, uint dungeonId) => owner.Execute(() =>
        tagEvildomSelection is { } choice && choice.Admission.DungeonId == dungeonId && SelectionEpoch == epoch &&
        ReferenceEquals(choice.Selection, CurrentDungeonSelection) && Phase == SessionPhase.MapSelect &&
        ReferenceEquals(choice.Admission.Primary, expected) &&
        CurrentTagEvildomPair(choice.Store, (timeProvider ?? TimeProvider.System).GetUtcNow()) is not null ? choice : null);
}
