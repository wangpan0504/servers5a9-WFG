using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record ExpertJobGiveUpResult(ushort Error, string Reason, Character? Character, long Generation, long Epoch,
    SessionPhase Phase, uint FinalGold = 0, byte GiveUpState = 0);

internal sealed partial class GameSession
{
    internal AccountOwners.Lease ExpertJobOwner => owner;
    ICharacterRepository ExpertJobRepository => repository;
    public Character? CaptureExpertJobTrade() => owner.Execute(() => CanUseExpertJob() ? Selected : null);

    public static (InventoryResult Customer, InventoryResult Owner)? CommitExpertJobTrade(
        GameSession customer, GameSession provider, ExpertJobTradePlan plan, Func<bool> eligible)
    {
        return AccountOwners.Lease.ExecuteTogether<(InventoryResult, InventoryResult)?>(customer.ExpertJobOwner, provider.ExpertJobOwner, () =>
        {
            if (!ReferenceEquals(customer.ExpertJobRepository, provider.ExpertJobRepository) || !customer.CanUseExpertJob() || !provider.CanUseExpertJob() ||
                !provider.expertJobStoreOpen || !ReferenceEquals(customer.Selected, plan.CustomerExpected) ||
                !ReferenceEquals(provider.Selected, plan.OwnerExpected) || !eligible()) return null;
            ExpertJobTradeCommit committed;
            try { committed = customer.ExpertJobRepository.CommitExpertJobTrade(customer.ExpertJobOwner, provider.ExpertJobOwner, plan); }
            catch (InventoryQualificationExpiredException) { return null; }
            customer.Selected = committed.Customer;
            provider.Selected = committed.Owner;
            return (Result(customer, committed.Customer, plan.CustomerExpected, plan.CustomerInventory),
                Result(provider, committed.Owner, plan.OwnerExpected, plan.OwnerInventory));
        });
        static InventoryResult Result(GameSession session, Character current, Character before, InventoryPlan inventory) =>
            new(true, 0, inventory)
            {
                Receipt = new(current, session.ExpertJobOwner.Generation, session.SelectionEpoch)
                { Phase = session.Phase, PreviousQuests = before.ActiveMainQuests, PreviousSkillSwitchInventory = before.SkillSwitchInventory }
            };
    }

    public Action<ushort>? ExpertJobStoreClosed { get; set; }
    void InvalidateExpertJobStore(bool publish = true)
    {
        bool wasOpen = expertJobStoreOpen;
        expertJobStoreOpen = false; expertJobStoreQuote = null;
        if (publish && wasOpen && owner.IsCurrent && Selected is { } character) ExpertJobStoreClosed?.Invoke(character.Id);
    }
    bool expertJobStoreOpen;
    (ushort Owner, Guid Instance, long Epoch)? expertJobStoreQuote;
    public void QuoteExpertJobStore(ExpertJobStoreRecord store) => owner.Execute(() =>
    {
        expertJobStoreQuote = (store.OwnerId, store.Instance, SelectionEpoch);
        return true;
    });
    public bool MatchesExpertJobStoreQuote(ExpertJobStoreRecord store) => owner.Execute(() =>
        expertJobStoreQuote is { } quote && quote.Owner == store.OwnerId && quote.Instance == store.Instance && quote.Epoch == SelectionEpoch);


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
        var learning = ExpertJobSkills.Remove(before.Learning, scripts.Skills(before.ExpertJobType));
        return owner.Execute(() =>
        {
            if (!CanUseExpertJob() || SelectionEpoch != capture.SelectionEpoch || !ReferenceEquals(Selected, before))
                return Rejected(19, "stale-expert-job");
            var committed = repository.CommitExpertJobGiveUp(owner, before, wallet, giveUpState, learning);
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

    public InventoryResult CommitExpertJobCraft(InventoryPlan inventory, int experience, QuestCatalog? tutorialQuests = null) => owner.Execute(() =>
    {
        if (!CanUseExpertJob() || Selected is not { } current || current.ExpertJobType is < 1 or > 4)
            return InventoryResult.Reject(19);
        Character committed;
        QuestLifecyclePlan? tutorial = null;
        if (tutorialQuests is not null && expertJobStoreOpen && current.ExpertJobType is 2 or 4 &&
            QuestExpertJobProgress.TutorialQuest(current.ExpertJobType) is { } questId)
            tutorial = QuestLifecyclePlan.ObserveExpertJobTutorial(current, tutorialQuests, questId);
        try { committed = repository.CommitExpertJobCraft(owner, current, inventory, experience, tutorial); }
        catch (InventoryQualificationExpiredException) { return InventoryResult.Reject(19); }
        Selected = committed;
        return new InventoryResult(true, 0, inventory)
        {
            Receipt = new(committed, owner.Generation, SelectionEpoch)
            { Phase = Phase, PreviousQuests = current.ActiveMainQuests, PreviousSkillSwitchInventory = current.SkillSwitchInventory }
        };
    });

    public Character? CommitExpertJobExperience(int experience) => owner.Execute(() =>
    {
        if (!CanUseExpertJob() || Selected is not { ExpertJobType: >= 1 and <= 4 } current)
            return null;
        var committed = repository.CommitExpertJobExperience(owner, current, experience);
        Selected = committed;
        return committed;
    });

    public InventoryResult CommitExpertJobAction(DfoServer.Game.ExpertJob.ExpertJobActionPlan plan) => owner.Execute(() =>
    {
        if (!CanUseExpertJob() || !ReferenceEquals(Selected, plan.Expected)) return InventoryResult.Reject(19);
        Character committed;
        try { committed = repository.CommitExpertJobAction(owner, plan); }
        catch (InventoryQualificationExpiredException) { return InventoryResult.Reject(19); }
        Selected = committed;
        return new InventoryResult(true, 0, plan.Inventory)
        {
            Receipt = new(committed, owner.Generation, SelectionEpoch)
            { Phase = Phase, PreviousQuests = plan.Expected.ActiveMainQuests, PreviousSkillSwitchInventory = plan.Expected.SkillSwitchInventory }
        };
    });

    public InventoryResult LearnExpertJobRecipe(InventorySlot slot, int itemId, DfoServer.Game.ExpertJob.ExpertJobLearning learning)
    {
        var captured = owner.Execute(() => (Character: CanUseExpertJob() ? Selected : null, SelectionEpoch, Phase));
        if (captured.Character is not { } expected || learning.Plan(expected, slot, itemId) is not { } plan)
            return InventoryResult.Reject(123);
        return owner.Execute(() =>
        {
            if (!CanUseExpertJob() || SelectionEpoch != captured.SelectionEpoch || Phase != captured.Phase ||
                !ReferenceEquals(Selected, expected)) return InventoryResult.Reject(123);
            Character committed;
            try { committed = repository.CommitExpertJobLearning(owner, plan); }
            catch (InventoryQualificationExpiredException) { return InventoryResult.Reject(123); }
            Selected = committed;
            return new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(committed, owner.Generation, SelectionEpoch)
                {
                    Phase = Phase, PreviousQuests = expected.ActiveMainQuests,
                    PreviousSkillSwitchInventory = expected.SkillSwitchInventory
                }
            };
        });
    }

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
