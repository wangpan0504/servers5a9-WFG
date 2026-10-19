using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record ExpertJobGiveUpResult(ushort Error, string Reason, Character? Character, long Generation, long Epoch,
    SessionPhase Phase, uint FinalGold = 0, byte GiveUpState = 0);

internal sealed partial class GameSession
{
    bool expertJobStoreOpen;

    public bool ExpertJobStoreOpen => owner.Execute(() => expertJobStoreOpen);

    bool CanUseExpertJob() => Ready && Phase == SessionPhase.Town && Selected is not null && pendingRental is null &&
        pendingCommunication is null && pendingQuestCompletion is null && pendingQuestLifecycle is null &&
        pendingQuestQuickClear is not { CommitStarted: true } && !HasPendingFormalCard && !HasPendingQuestScene &&
        !HasActiveDungeonContinuation && !HasPendingQuestReplay;

    public ExpertJobGiveUpResult GiveUpExpertJob(ExpertJobScriptCatalog scripts)
    {
        var capture = owner.Execute(() => (Allowed: CanUseExpertJob(), Selected, SelectionEpoch, Phase, owner.Generation));
        ExpertJobGiveUpResult Rejected(ushort error, string reason) => new(error, reason, capture.Selected,
            capture.Generation, capture.SelectionEpoch, capture.Phase);
        if (!capture.Allowed || capture.Selected is not { } before) return Rejected(19, "not-ready-town");
        if (before.ExpertJobType is < 1 or > 4) return Rejected(10, "no-expert-job");
        if (!DfoServer.Game.ExpertJob.ExpertJobCraft.TryPlanGiveUp(before, scripts, out var wallet, out int giveUpState, out ushort error))
            return Rejected(error, error == 10 ? "no-expert-job" : "short-gold");
        return owner.Execute(() =>
        {
            if (!CanUseExpertJob() || SelectionEpoch != capture.SelectionEpoch || !ReferenceEquals(Selected, before))
                return Rejected(19, "stale-expert-job");
            var committed = repository.CommitExpertJobGiveUp(owner, before, wallet, giveUpState);
            Selected = committed;
            expertJobStoreOpen = false;
            return new ExpertJobGiveUpResult(0, "committed", committed, owner.Generation, SelectionEpoch, Phase,
                committed.Inventory.Gold, (byte)committed.ExpertGiveUpState);
        });
    }

    public InventoryResult CommitExpertJobMachine(InventoryPlan inventory, int grade, int endurance) => owner.Execute(() =>
    {
        if (!CanUseExpertJob() || Selected is not { } current || current.ExpertJobType is < 1 or > 4)
            return InventoryResult.Reject(19);
        var committed = repository.CommitExpertJobMachine(owner, current, inventory, grade, endurance);
        Selected = committed;
        return new InventoryResult(true, 0, inventory)
        {
            Receipt = new(committed, owner.Generation, SelectionEpoch) { Phase = Phase }
        };
    });

    public InventoryResult CommitExpertJobCraft(InventoryPlan inventory, int experience) => owner.Execute(() =>
    {
        if (!CanUseExpertJob() || Selected is not { } current || current.ExpertJobType is < 1 or > 4)
            return InventoryResult.Reject(19);
        var result = CommitInventory(current, new(true, 0, inventory));
        if (!result.Accepted || Selected is not { } committed) return result;
        var final = committed.ExpertJobExperience == experience ? committed
            : repository.CommitExpertJobExperience(owner, committed, experience);
        Selected = final;
        return result with { Receipt = new(final, owner.Generation, SelectionEpoch) { Phase = Phase } };
    });

    public Character? CommitExpertJobExperience(int experience) => owner.Execute(() =>
    {
        if (!CanUseExpertJob() || Selected is not { ExpertJobType: >= 1 and <= 4 } current)
            return null;
        var committed = repository.CommitExpertJobExperience(owner, current, experience);
        Selected = committed;
        return committed;
    });

    public bool CanPublishExpertJobGiveUp(ExpertJobGiveUpResult result) => owner.Execute(() =>
        result.Generation == owner.Generation && result.Epoch == SelectionEpoch && result.Phase == Phase &&
        ReferenceEquals(result.Character, Selected));

    public bool OpenExpertJobStore()
    {
        return owner.Execute(() =>
        {
            if (!CanUseExpertJob() || Selected is not { ExpertJobType: >= 1 and <= 4 })
                return false;
            expertJobStoreOpen = true;
            return true;
        });
    }

    public void CloseExpertJobStore() => owner.Execute(() =>
    {
        expertJobStoreOpen = false;
        return true;
    });

    public QuestLifecycleReceipt? ObserveExpertJobTutorial(ushort questId, QuestCatalog quests)
    {
        var capture = owner.Execute(() =>
        {
            Character? character = CanUseExpertJob() ? Selected : null;
            return (Character: character, SelectionEpoch, Phase, Identity: (DfoServer.Game.Dungeons.DungeonRunIdentity?)null,
                Pending: pendingQuestLifecycle, Store: expertJobStoreOpen);
        });
        if (capture.Character is not { } expected) return null;
        var refusal = new QuestLifecycleReceipt(expected, null, owner.Generation, capture.SelectionEpoch, capture.Phase,
            capture.Identity, "副职业教程资格不符");
        if (!capture.Store) return refusal with { Reason = "副职业商店未开设" };
        if (!quests.TryGet(questId, out var quest) || !QuestExpertJobProgress.Tutorial(quest) ||
            expected.ExpertJobType != QuestExpertJobProgress.RequiredType(quest))
            return refusal;
        QuestLifecyclePlan? plan;
        try { plan = QuestLifecyclePlan.ObserveExpertJobTutorial(expected, quests, questId); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            return refusal with { Reason = error.Message };
        }
        if (plan is null)
            return expected.ActiveMainQuests.Any(row => row.QuestId == questId && row.ConditionState == 0)
                ? refusal with { Outcome = QuestLifecycleOutcome.Unchanged, Reason = "副职业教程条件已归零" }
                : refusal;
        return owner.Execute(() =>
        {
            if (!CanUseExpertJob() || !ReferenceEquals(pendingQuestLifecycle, capture.Pending) ||
                !CanPublishQuestLifecycle(refusal) || !expertJobStoreOpen)
                throw new InvalidOperationException("副职业教程规划的原选角已失效");
            pendingQuestLifecycle = new(plan, capture.SelectionEpoch, capture.Phase, capture.Identity, null);
            var commit = repository.CommitQuestLifecycle(owner, plan);
            var receipt = refusal with { Character = commit.Character, Commit = commit,
                Outcome = QuestLifecycleOutcome.Committed, Reason = "副职业教程事实已提交" };
            if (!commit.ProjectionCurrent) return receipt with { Reason = "副职业教程已提交，当前投影已失效" };
            Selected = commit.Character;
            completed = QuestEligibility.Completed(Selected);
            pendingQuestLifecycle = null;
            return receipt;
        });
    }
}
