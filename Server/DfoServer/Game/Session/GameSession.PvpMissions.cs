using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using DfoServer.Game.Social;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal InventoryResult? RefreshPvpMissions(PvpMissionBook book) => CommitPvpMission(book, null, () => true);

    internal InventoryResult? CompletePvpMissionCombo(int index, PvpMissionBook book)
    {
        var capture = owner.Execute(() =>
        {
            if (index is < 0 or >= 12 || Phase != SessionPhase.PvpRoom || Selected is null) return null;
            if (pvpPractice is { Mode: null } practice && CanPublishPvpPractice(practice) && index < practice.Combos.Length)
                return new PvpMissionFact(0, Selected.Job, index, practice.Instance);
            if (pvpTraining is { Entry.IsCombo: true } training && index < training.Entry.Combos.Length &&
                training.SelectionEpoch == SelectionEpoch && training.Generation == owner.Generation &&
                ReferenceEquals(training.Entry.Expected, Selected))
                return new PvpMissionFact(0, Selected.Job, index, training.InstanceId);
            return null;
        });
        return capture is null ? null : CommitPvpMission(book, capture, () =>
            Phase == SessionPhase.PvpRoom && (pvpPractice?.Instance == capture.Instance || pvpTraining?.InstanceId == capture.Instance));
    }

    internal InventoryResult? CompletePvpMissionArcade(PvpMissionBook book)
    {
        var fact = owner.Execute(() =>
        {
            if (Phase != SessionPhase.PvpRoom || Selected is null) return null;
            if (pvpPractice is { Mode: { } mode, Round.Complete: true } practice && CanPublishPvpPractice(practice))
                return new PvpMissionFact(1, Selected.Job, mode.Difficulty, practice.Instance);
            if (pvpTraining is { Entry.Mode: { } trainingMode, Round.Complete: true } training &&
                training.SelectionEpoch == SelectionEpoch && training.Generation == owner.Generation &&
                ReferenceEquals(training.Entry.Expected, Selected))
                return new PvpMissionFact(1, Selected.Job, trainingMode.Difficulty, training.InstanceId);
            return null;
        });
        return fact is null ? null : CommitPvpMission(book, fact, () =>
            Phase == SessionPhase.PvpRoom && (pvpPractice?.Instance == fact.Instance || pvpTraining?.InstanceId == fact.Instance));
    }

    InventoryResult? CommitPvpMission(PvpMissionBook book, PvpMissionFact? fact, Func<bool> valid)
    {
        var capture = owner.Execute(() => Ready && Selected is not null && ChannelEntry is { } channel &&
            ChannelAreaLanding.IsPvpType(channel.Type) && valid() && pendingQuestLifecycle is null &&
            pendingRental is null && !HasPendingTownQuestCommit && !HasPendingFormalCard
            ? (Actor: new SocialActor(owner, this, Selected, SelectionEpoch), Phase) : default);
        if (capture.Actor is not { } actor) return null;
        // 模板解析和奖励分配在锁外；只有原选角、原训练实例能提交。
        var plan = book.Plan(actor.Owner, actor.Character, fact);
        return owner.Execute(() =>
        {
            if (!actor.IsCurrent() || !ReferenceEquals(Selected, actor.Character) || Phase != capture.Phase || !valid() ||
                pendingQuestLifecycle is not null || pendingRental is not null || HasPendingTownQuestCommit || HasPendingFormalCard) return null;
            if (plan.Missions.IsEmpty && plan.Before.Progress.Same(plan.Before.Progress.Apply(fact))) return null;
            Selected = book.Commit(owner, plan);
            completed = QuestEligibility.Completed(Selected);
            // 资产提交可能刷新任务对象；风云接取UUID不变，继续绑定同一实例的最新快照。
            if (pvpTraining is { } training)
                pvpTraining = training with { Entry = training.Entry with { Expected = Selected,
                    Quest = Selected.ActiveMainQuests.Single(row => row.AcceptanceId == training.Entry.Quest.AcceptanceId) } };
            if (pvpPractice is { } practice)
                pvpPractice = practice with { Actor = new(owner, this, Selected, SelectionEpoch) };
            return plan.Inventory with { Receipt = new(Selected, owner.Generation, SelectionEpoch)
                { Phase = Phase, PreviousQuests = actor.Character.ActiveMainQuests,
                    PreviousSkillSwitchInventory = actor.Character.SkillSwitchInventory } };
        });
    }
}
