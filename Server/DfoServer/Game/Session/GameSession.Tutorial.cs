using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Tutorials;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DungeonSelectionReceipt? BeginTutorialSelection(uint dungeonId, TutorialCatalog tutorials) => owner.Execute(() =>
    {
        if (!Ready || Selected?.Tutorial is not { Completed: false } pending || pending.DungeonId != dungeonId ||
            tutorials.Get(Selected.Job) is not { } flow || flow.DungeonId != dungeonId || formalDungeon is not null ||
            Phase is not (SessionPhase.Town or SessionPhase.MapSelect) || !towns.IsPosition(flow.Destination, completed)) return null;
        if (CurrentDungeonSelection is { IsTutorial: false }) return null;
        bool opening = CurrentDungeonSelection is null;
        CurrentDungeonSelection ??= new(++dungeonSelectionId, SelectionEpoch, Selected.Id, Selected.Position, flow.Destination) { IsTutorial = true };
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return new DungeonSelectionReceipt(Selected, owner.Generation, SelectionEpoch, dungeonSelectionId) { Tutorial = true, OpensClientSelector = opening };
    });

    public TutorialFlagResult? ChangeTutorialFlag(uint progress, TutorialCatalog tutorials, ItemCatalog items,
        GoldLimitCatalog limits, EpicPieceCatalog pieces)
    {
        var capture = owner.Execute(() => (Expected: Ready ? Selected : null, SelectionEpoch, Phase,
            Dungeon: formalDungeon, Identity: formalDungeon?.Run.Snapshot.Identity,
            MapId: formalDungeon?.Run.Snapshot.CurrentMap.Id ?? 0, EligibleReward: CanReward()));
        if (capture.Expected is not { } expected) return null;
        if (progress == 36 && capture.Phase == SessionPhase.Town || progress == 31 && expected.Tutorial is not { Completed: false } ||
            progress == 30 && expected.Tutorial is { Completed: false } &&
                (capture.Phase == SessionPhase.MapSelect || capture.Dungeon?.Entry.IsTutorial == true))
            return owner.Execute(() => NoChange(0));
        if (progress != 38 || expected.Tutorial is not { Completed: false } state ||
            !capture.EligibleReward) return owner.Execute(() => NoChange(19));
        if (state.RewardReceived) return owner.Execute(() => NoChange(0));
        var grant = TutorialRewards.Plan(expected, tutorials.Rewards, items, limits, pieces);
        if (!grant.Accepted) return owner.Execute(() => NoChange(grant.Error));
        var inventoryPlan = grant.Plan!;
        if (TutorialRewards.Rows(inventoryPlan, inventoryPlan.AfterIndex).Length > byte.MaxValue)
            return owner.Execute(() => NoChange(19));
        var checkpoint = new TutorialCheckpointPlan(expected, Guid.NewGuid(), capture.Identity, capture.MapId,
            "reward", progress, 0, inventoryPlan, state with { RewardReceived = true }, expected.Position);
        return owner.Execute(() =>
        {
            if (!Current() || !CanReward()) return (TutorialFlagResult?)null;
            var commit = repository.CommitTutorialCheckpoint(owner, checkpoint);
            Selected = commit.Character;
            var receipt = new InventoryCommitReceipt(Selected, owner.Generation, SelectionEpoch)
                { Phase = Phase, DungeonIdentity = capture.Identity, PreviousQuests = expected.ActiveMainQuests };
            var rows = TutorialRewards.Rows(inventoryPlan, receipt.Index);
            return new TutorialFlagResult(0, Selected, owner.Generation, SelectionEpoch, rows,
                grant with { Receipt = receipt })
                { Phase = Phase, Identity = capture.Identity };
        });

        bool Current() => Ready && ReferenceEquals(Selected, expected) && SelectionEpoch == capture.SelectionEpoch &&
            Phase == capture.Phase && ReferenceEquals(formalDungeon, capture.Dungeon) && formalDungeon?.Run.Snapshot.Identity == capture.Identity;
        TutorialFlagResult? NoChange(ushort error) => Current()
            ? new(error, expected, owner.Generation, SelectionEpoch, []) { Phase = Phase, Identity = capture.Identity } : null;
        bool CanReward() => Phase == SessionPhase.Dungeon &&
            formalDungeon is { Entry.IsTutorial: true, Run.Phase: SinglePlayerDungeonPhase.Dungeon } run &&
            !run.Run.HasPendingDeath && !run.Run.HasPendingSettlement && !run.Ground.HasPendingOperation &&
            !run.HasPendingFatigue && run.PendingTutorialClear is null && !HasPendingQuestScene;
    }

    public bool CanPublishTutorial(TutorialFlagResult receipt) => owner.Execute(() => Ready &&
        receipt.Generation == owner.Generation && receipt.SelectionEpoch == SelectionEpoch && receipt.Phase == Phase &&
        ReferenceEquals(Selected, receipt.Character) && receipt.Identity == formalDungeon?.Run.Snapshot.Identity);

    public InventoryResult? UseTutorialConsumable(InventorySlot slot, int itemId, TutorialCatalog tutorials, ItemCatalog items)
    {
        if (!tutorials.Rewards.Any(row => row.ItemId == itemId)) return null;
        var expected = owner.Execute(() => Ready && Phase == SessionPhase.Dungeon && formalDungeon?.Entry.IsTutorial == true &&
            Selected?.Tutorial?.RewardReceived == true ? Selected : null);
        if (expected is null) return null;
        return PlanAndCommitInventory(character => ReferenceEquals(character, expected)
            ? TutorialRewards.Use(character, slot, itemId, items.Get(itemId)) : InventoryResult.Reject(17),
            17, requireTown: false, requiredPhase: SessionPhase.Dungeon);
    }

    public FormalDungeonReceipt? ConfirmTutorialCinematic(ushort actorId, ushort targetKey) => owner.Execute(() =>
    {
        if (!Ready || Selected?.Id != actorId || Selected.Tutorial is null || Phase != SessionPhase.Dungeon ||
            formalDungeon is not { Entry.IsTutorial: true } dungeon || dungeon.Run.TutorialCinematicTargets(targetKey) is null ||
            dungeon.Ground.HasPendingOperation || dungeon.HasPendingFatigue || HasPendingQuestScene) return null;
        var identity = dungeon.Run.Snapshot.Identity;
        var checkpoint = dungeon.PendingTutorialClear ??= TutorialCheckpoint(Selected, "clear", Guid.NewGuid(), targetKey,
            0, identity, dungeon.Run.Snapshot.CurrentMap.Id, true);
        if (!ReferenceEquals(Selected, checkpoint.Expected) || checkpoint.Identity != identity || checkpoint.Subject != targetKey)
            throw new InvalidOperationException("训练剧情完成的原角色或实例已变化");
        var commit = repository.CommitTutorialCheckpoint(owner, checkpoint);
        var result = dungeon.Run.CompleteTutorialCinematic(identity, targetKey);
        if (result.Outcome != DungeonRunOutcome.Accepted) throw new InvalidOperationException("已提交训练完成不能确认原剧情场景");
        Selected = commit.Character;
        dungeon.PendingTutorialClear = null;
        dungeon.TutorialClearTarget = targetKey;
        dungeon.ClearTimeMilliseconds = checked((uint)System.Diagnostics.Stopwatch.GetElapsedTime(dungeon.StartedTimestamp).TotalMilliseconds);
        SetState(SessionPhase.Settlement);
        return FormalReceipt();
    });

    TutorialCheckpointPlan TutorialCheckpoint(Character character, string kind, Guid eventId, uint subject, uint amount,
        DungeonRunIdentity? identity, int mapId, bool complete, CharacterGrowthCatalog? growth = null)
    {
        var before = character.Tutorial ?? throw new InvalidOperationException("训练角色状态缺失");
        CharacterGrowthPlan? progress = null;
        if (amount != 0)
        {
            var result = CharacterGrowthPlan.AddExperience(character, amount, growth ?? throw new InvalidOperationException("训练成长目录缺失"));
            progress = result.Plan ?? throw new InvalidDataException("训练成长候选无效: " + result.Failure);
        }
        var destination = complete ? formalDungeon?.OrdinaryReturnPosition ?? CurrentDungeonSelection?.ReturnPosition ?? character.Position : character.Position;
        return new(character, eventId, identity, mapId, kind, subject, amount,
            new(character.Inventory, character.Inventory, "tutorial_" + kind, new(character)),
            before with { Completed = before.Completed || complete }, destination, progress);
    }
}
