using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.MonsterMockBattle;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed record MockBattleView(Character Character, long Epoch, MockBattleAccount Account,
    InventoryResult? Inventory = null, ImmutableArray<byte> AcquiredSlots = default);

internal sealed partial class GameSession
{
    (long Epoch, ImmutableArray<MockBattleOpponent> Matches, MockBattleOpponent? Search)? mockBattleMatching;

    public MockBattleView? ReadMockBattle(MonsterMockBattleService service)
    {
        var capture = owner.Execute(() => Ready && Selected is not null && Phase == SessionPhase.Town
            ? (Selected, SelectionEpoch) : default);
        if (capture.Selected is null || !service.Enabled) return null;
        var account = service.Read(owner, capture.Selected);
        var initialization = service.PrepareRefresh(owner, capture.Selected);
        return owner.Execute<MockBattleView?>(() =>
        {
            if (!Ready || Phase != SessionPhase.Town || !ReferenceEquals(Selected, capture.Selected) ||
                SelectionEpoch != capture.SelectionEpoch) return null;
            InventoryResult? inventory = null;
            if (initialization is not null)
            {
                var committed = service.Commit(owner, capture.Selected, initialization);
                Selected = committed.Character;
                account = committed.Account;
                inventory = new(true, 0, initialization.Inventory) { Receipt = InventoryReceipt(capture.Selected) };
                if (initialization.Mail is not null) NotifyMailbox(Selected!.Id);
            }
            return new(Selected!, SelectionEpoch, account, inventory);
        });
    }

    public MockBattleView? MutateMockBattle(MonsterMockBattleService service, uint operation, uint argument0,
        uint argument1, ReadOnlySpan<byte> payload)
    {
        var capture = owner.Execute(() => Ready && Selected is not null && Phase == SessionPhase.Town &&
            !HasPendingTownQuestCommit && pendingRental is null ? (Selected, SelectionEpoch) : default);
        if (capture.Selected is null) return null;
        var plan = service.Prepare(owner, capture.Selected, operation, argument0, argument1, payload);
        return owner.Execute<MockBattleView?>(() =>
        {
            if (!Ready || Phase != SessionPhase.Town || SelectionEpoch != capture.SelectionEpoch ||
                !ReferenceEquals(Selected, capture.Selected) || HasPendingTownQuestCommit || pendingRental is not null) return null;
            var committed = service.Commit(owner, capture.Selected, plan);
            Selected = committed.Character;
            if (!plan.Inventory.OverflowItems.IsEmpty) MarkMailboxRestore();
            if (plan.Mail is not null) NotifyMailbox(Selected.Id);
            var inventory = new InventoryResult(true, 0, plan.Inventory) { Receipt = InventoryReceipt(capture.Selected) };
            return new(Selected, SelectionEpoch, committed.Account, inventory, plan.Mutation.AcquiredSlots);
        });
    }

    public (MockBattleView View, ImmutableArray<MockBattleOpponent> Opponents)? MatchMockBattle(
        MonsterMockBattleService service, string? name, uint stars)
    {
        if (name is null && stars is < 1 or > 25) throw new MockBattleRejectedException("匹配星数必须为1至25");
        var view = ReadMockBattle(service);
        if (view is null) return null;
        var after = owner.Execute(() => mockBattleMatching is { } previous && previous.Epoch == SelectionEpoch
            ? previous.Matches.LastOrDefault()?.Account : null);
        var matches = service.Opponents(owner, name, checked((int)stars), after);
        return owner.Execute<(MockBattleView, ImmutableArray<MockBattleOpponent>)?>(() =>
        {
            if (!CanPublishMockBattle(view)) return null;
            var previous = mockBattleMatching is { } current && current.Epoch == SelectionEpoch ? current :
                (SelectionEpoch, ImmutableArray<MockBattleOpponent>.Empty, (MockBattleOpponent?)null);
            mockBattleMatching = name is null ? (SelectionEpoch, matches, previous.Item3) :
                (SelectionEpoch, previous.Item2, matches.FirstOrDefault());
            return (view, matches);
        });
    }

    public bool CanPublishMockBattle(MockBattleView view) => owner.IsCurrent && owner.Execute(() =>
        Ready && Phase == SessionPhase.Town && SelectionEpoch == view.Epoch && ReferenceEquals(Selected, view.Character));

    public DungeonSelectionReceipt? BeginMockBattleSelection(MonsterMockBattleService service, uint dungeon) => owner.Execute(() =>
    {
        if (!service.Enabled || !service.ContainsDungeon(dungeon) || !Ready || Selected is not { } character ||
            !service.Catalog.Worlds.Values.Any(world => world.DungeonId == dungeon && world.Mode < 2) ||
            Phase != SessionPhase.Town || pendingRental is not null || HasPendingTownQuestCommit ||
            HasMultiplePartyMembers?.Invoke() == true ||
            !towns.IsRoom(character.Position, DfoServer.Game.Quests.QuestEligibility.Completed(character))) return null;
        CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, character.Id, character.Position, character.Position)
            { MockBattleDungeon = dungeon };
        SetState(SessionPhase.MapSelect);
        return new DungeonSelectionReceipt(character, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

    public FormalDungeonReceipt? EnterMockBattle(MonsterMockBattleService service, uint dungeon, byte difficulty,
        uint opponentIndex, DungeonCatalog catalog, DungeonRoomCatalog rooms)
    {
        var capture = owner.Execute(() => CanEnterFormalDungeon(null) && CurrentDungeonSelection?.MockBattleDungeon == dungeon
            ? (Selected, SelectionEpoch, CurrentDungeonSelection) : default);
        if (capture.Selected is not { } character || capture.CurrentDungeonSelection is not { } selection) return null;
        var world = service.Catalog.Worlds.Values.Single(row => row.DungeonId == dungeon);
        MockBattleOpponent? opponent = null;
        if (world.Mode is 2 or 3)
        {
            opponent = owner.Execute(() => mockBattleMatching is { } matching && matching.Epoch == SelectionEpoch
                ? opponentIndex == 0 ? matching.Search : opponentIndex <= matching.Matches.Length
                    ? matching.Matches[(int)opponentIndex - 1] : null : null);
            if (opponent is null || world.Mode == 3 && opponentIndex != 0 || world.Mode == 2 && opponentIndex == 0) return null;
        }
        var admission = service.PrepareAdmission(owner, character, dungeon, opponent);
        var definition = service.Catalog.Battlefield(catalog.Get(checked((int)dungeon)));
        if (!DungeonEntryPlan.TryCreate(definition, character, dungeon, 0, difficulty,
            out var entry, out var reason)) return RejectMockBattleEntry(reason);
        entry = entry! with { MockBattle = admission,
            TerminalLayerMap = null, PassiveClearTerminalMap = null,
            DisableClearReward = true, DisableMonsterAndClearExperience = true, DisableItemDrops = true,
            DisableFatigueConsumption = true, DisableGiveupPenalty = true, CoinLimit = 0, AllowsCompanionAi = false,
            RecommendedQuestClear = false, RecommendedQuestItems = false, GuildMileageEligible = false,
            QuestAcceptances = [], StartFatigue = 0 };
        var prepared = PrepareFormalDungeonResources(character, capture.SelectionEpoch, entry, rooms, null, null, null, null);
        if (prepared is null) return null;
        return owner.Execute(() => ReferenceEquals(Selected, character) && SelectionEpoch == capture.SelectionEpoch &&
            ReferenceEquals(CurrentDungeonSelection, selection) && CanEnterFormalDungeon(null) ? InstallFormalDungeon(prepared) : null);
    }

    FormalDungeonReceipt? RejectMockBattleEntry(string reason)
    {
        LastFormalDungeonRejection = "mock-battle:" + reason;
        return null;
    }
}
