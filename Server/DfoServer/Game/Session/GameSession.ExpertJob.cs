using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record ExpertJobGiveUpResult(ushort Error, string Reason, Character? Character, long Generation, long Epoch,
    SessionPhase Phase, uint FinalGold = 0, byte GiveUpState = 0, InventoryPlan? Inventory = null);

internal sealed partial class GameSession
{
    internal AccountOwners.Lease ExpertJobOwner => owner;
    ICharacterRepository ExpertJobRepository => repository;
    public Character? CaptureExpertJobTrade() => owner.Execute(() => CanUseExpertJob() ? Selected : null);
    public Character? CapturePersonalExpertJob() => owner.Execute(() => CanUsePersonalExpertJob() ? Selected : null);

    public bool InitializeExpertJobExperience(ExpertJobScriptCatalog scripts) => owner.Execute(() =>
    {
        if (Ready || Phase != SessionPhase.Unselected || pendingRental is not null || HasPendingTownQuestCommit ||
            Selected is not { ExpertJobType: >= 1 and <= 4 } current) return false;
        int experience = scripts.ClampExperience(current.ExpertJobType, current.ExpertJobExperience);
        if (experience == current.ExpertJobExperience) return false;
        Selected = repository.CommitExpertJobExperience(owner, current, experience);
        return true;
    });

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

    bool CanUsePersonalExpertJob() => Ready && Selected is not null && !AntonRejoinPresentationPending &&
        pendingRental is null && pendingCommunication is null && pendingQuestCompletion is null &&
        pendingQuestLifecycle is null && pendingQuestQuickClear is not { CommitStarted: true } &&
        !HasPendingTownQuestCommit && !HasPendingFormalCard && !HasPendingQuestScene &&
        !HasActiveDungeonContinuation && !HasPendingQuestReplay &&
        formalDungeon?.PendingTutorialClear is null && formalDungeon?.PendingInventoryDiscard is null &&
        formalDungeon?.AltarSettlement is not { Stage: < 2 } && formalDungeon?.Ground.HasPendingOperation != true &&
        (Phase == SessionPhase.Town || Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
            !HasUncommittedQuestQuickClear && formalDungeon is { HasPendingFatigue: false } dungeon &&
            !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement);

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
            var committed = repository.CommitExpertJobGiveUp(owner, before, wallet, giveUpState, learning, scripts.ClearQuests(before.ExpertJobType));
            Selected = committed;
            completed = QuestEligibility.Completed(committed);
            InvalidateExpertJobStore();
            return new ExpertJobGiveUpResult(0, "committed", committed, owner.Generation, SelectionEpoch, Phase,
                committed.Inventory.Gold, (byte)committed.ExpertGiveUpState, wallet);
        });
    }

    public InventoryResult CommitExpertJobMachine(InventoryPlan inventory, int grade, int endurance) => owner.Execute(() =>
    {
        if (!CanUsePersonalExpertJob() || Selected is not { } current || current.ExpertJobType is < 1 or > 4)
            return InventoryResult.Reject(19);
        var committed = repository.CommitExpertJobMachine(owner, current, inventory, grade, endurance);
        Selected = committed;
        return new InventoryResult(true, 0, inventory) { Receipt = InventoryReceipt(current) };
    });

    public InventoryResult CommitExpertJobCraft(InventoryPlan inventory, int experience, QuestCatalog? tutorialQuests = null,
        bool enchanting = false) => owner.Execute(() =>
    {
        if (!CanUsePersonalExpertJob() || Selected is not { } current || current.ExpertJobType is < 1 or > 4)
            return InventoryResult.Reject(19);
        Character committed;
        QuestLifecyclePlan? tutorial = null;
        // 炼金/控偶教程使用个人制作台，不要求摆摊；附魔商店路径仍保留自己的开店资格。
        if (tutorialQuests is not null && (current.ExpertJobType is 2 or 4 || enchanting && current.ExpertJobType == 1 && expertJobStoreOpen) &&
            QuestExpertJobProgress.TutorialQuest(current.ExpertJobType) is { } questId)
            tutorial = QuestLifecyclePlan.ObserveExpertJobTutorial(current, tutorialQuests, questId);
        try { committed = repository.CommitExpertJobCraft(owner, current, inventory, experience, tutorial); }
        catch (InventoryQualificationExpiredException) { return InventoryResult.Reject(19); }
        Selected = committed;
        return new InventoryResult(true, 0, inventory) { Receipt = InventoryReceipt(current) };
    });

    public InventoryResult UseExpertJobBead(Character expected, InventoryEnchantmentService enchantments,
        ExpertJobScriptCatalog scripts, EnchantmentRequest request, QuestCatalog tutorialQuests)
    {
        var capture = owner.Execute(() => (Allowed: CanUsePersonalExpertJob() && ReferenceEquals(Selected, expected),
            SelectionEpoch, Phase, Identity: formalDungeon?.Run.Snapshot.Identity));
        if (!capture.Allowed) return InventoryResult.Reject(19);
        var source = expected.Inventory.Find(request.Source);
        // 只接原宝珠服务，卡片配方及职业扣料保持各自业务路径。
        if (source is null || enchantments.Definition(source.Core.ItemId) is null)
            return InventoryResult.Reject(InventoryEnchantmentService.Rejection);
        var qualification = EffectRules(expected, source.Core.ItemId, request.Target);
        if (CheckStackableUse(expected, capture.SelectionEpoch, capture.Phase, qualification.Rules, qualification.TargetWorlds) != StackableUseRejection.None)
            return InventoryResult.Reject(InventoryEnchantmentService.Rejection);
        if (!ExpertJobCraft.TryPlanEnchant(expected, enchantments, scripts, (byte)request.Target.List, (ushort)request.Target.Index,
            (byte)request.Source.List, (ushort)request.Source.Index, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            out var inventory, out int experience, out ushort error))
            return InventoryResult.Reject(error == 0 ? (ushort)19 : error);
        return owner.Execute(() =>
        {
            if (!CanUsePersonalExpertJob() || SelectionEpoch != capture.SelectionEpoch || Phase != capture.Phase ||
                formalDungeon?.Run.Snapshot.Identity != capture.Identity ||
                CheckStackableUse(expected, capture.SelectionEpoch, capture.Phase,
                    qualification.Rules, qualification.TargetWorlds) != StackableUseRejection.None)
                return InventoryResult.Reject(InventoryEnchantmentService.Rejection);
            return CommitExpertJobCraft(inventory, experience, tutorialQuests, enchanting: true);
        });
    }

    public Character? CommitExpertJobExperience(int experience) => owner.Execute(() =>
    {
        if (!CanUsePersonalExpertJob() || Selected is not { ExpertJobType: >= 1 and <= 4 } current)
            return null;
        var committed = repository.CommitExpertJobExperience(owner, current, experience);
        Selected = committed;
        return committed;
    });

    public InventoryResult CommitExpertJobAction(DfoServer.Game.ExpertJob.ExpertJobActionPlan plan, QuestCatalog? tutorialQuests = null) => owner.Execute(() =>
    {
        if (!CanUsePersonalExpertJob() || !ReferenceEquals(Selected, plan.Expected)) return InventoryResult.Reject(19);
        if (plan.Succeeded && tutorialQuests is not null && expertJobStoreOpen &&
            QuestExpertJobProgress.TutorialQuest(plan.Expected.ExpertJobType) is { } questId)
            plan = plan with { Tutorial = QuestLifecyclePlan.ObserveExpertJobTutorial(plan.Expected, tutorialQuests, questId) };
        Character committed;
        try { committed = repository.CommitExpertJobAction(owner, plan); }
        catch (InventoryQualificationExpiredException) { return InventoryResult.Reject(19); }
        Selected = committed;
        return new InventoryResult(true, 0, plan.Inventory) { Receipt = InventoryReceipt(plan.Expected) };
    });

    public InventoryResult LearnExpertJobRecipe(InventorySlot slot, int itemId, DfoServer.Game.ExpertJob.ExpertJobLearning learning)
    {
        var captured = owner.Execute(() => (Character: CanUsePersonalExpertJob() ? Selected : null,
            SelectionEpoch, Phase, Identity: formalDungeon?.Run.Snapshot.Identity));
        if (captured.Character is not { } expected || learning.Plan(expected, slot, itemId) is not { } plan)
            return InventoryResult.Reject(123);
        return owner.Execute(() =>
        {
            if (!CanUsePersonalExpertJob() || SelectionEpoch != captured.SelectionEpoch || Phase != captured.Phase ||
                formalDungeon?.Run.Snapshot.Identity != captured.Identity ||
                !ReferenceEquals(Selected, expected)) return InventoryResult.Reject(123);
            Character committed;
            try { committed = repository.CommitExpertJobLearning(owner, plan); }
            catch (InventoryQualificationExpiredException) { return InventoryResult.Reject(123); }
            Selected = committed;
            return new InventoryResult(true, 0, plan.Inventory) { Receipt = InventoryReceipt(expected) };
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
