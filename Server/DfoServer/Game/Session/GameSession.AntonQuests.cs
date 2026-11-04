using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    AntonQuestBindings? antonQuestBindings;

    void PublishAntonQuestBindings()
    {
        var current = Volatile.Read(ref antonQuestBindings);
        if (selected is { } same && current is not null && current.Character == same.Id &&
            current.Epoch == SelectionEpoch && current.Active.SequenceEqual(same.ActiveMainQuests)) return;
        Volatile.Write(ref antonQuestBindings,
            selected is { } character ? new(character.Id, SelectionEpoch, character.ActiveMainQuests.ToImmutableArray()) : null);
    }

    // Battle只读取已经发布的不可变身份，不取得owner或读取Selected。
    internal AntonQuestBindings? CaptureAntonQuestBindings(RaidActor actor)
    {
        var snapshot = Volatile.Read(ref antonQuestBindings);
        return ReferenceEquals(actor.Session, this) && ReferenceEquals(actor.Owner, owner) &&
            snapshot is not null && snapshot.Character == actor.Character && snapshot.Epoch == actor.Epoch
                ? snapshot : null;
    }

    internal AntonPhaseQuestSnapshot? CaptureAntonPhaseQuest(RaidActor actor, AntonRaidBattle battle,
        ImmutableArray<ActiveMainQuest> bindings) => owner.Execute(() =>
        Ready && actor.IsCurrent && raidCombat is { } raid && raid.Actor == actor &&
        ReferenceEquals(raid.Battle, battle) && Selected is { } character
            ? new AntonPhaseQuestSnapshot(character.Id, owner.Account, character.Revision,
                character.ActiveMainQuests.ToImmutableArray(), bindings) : null);

    internal void InstallAntonPhaseQuest(RaidActor actor, AntonRaidBattle battle, AntonPhaseQuestProgress progress) => owner.Execute(() =>
    {
        if (!actor.IsCurrent || raidCombat is not { } raid || raid.Actor != actor ||
            !ReferenceEquals(raid.Battle, battle) || Selected is not { } character) return false;
        Selected = ApplyAntonPhaseQuest(character, progress);
        return true;
    });

    internal static Character ApplyAntonPhaseQuest(Character character, AntonPhaseQuestProgress progress)
    {
        if (character.Revision == progress.Revision && character.ActiveMainQuests.SequenceEqual(progress.After) ||
            character.Revision > progress.Revision) return character;
        if (character.Revision != progress.BeforeRevision || !character.ActiveMainQuests.SequenceEqual(progress.Before))
            throw new InvalidOperationException("安徒恩阶段任务回执与角色快照不一致");
        return character with { Revision = progress.Revision, ActiveMainQuests = progress.After.ToArray() };
    }

    static bool SameAntonQuestIdentities(IReadOnlyList<ActiveMainQuest> active, ImmutableArray<ActiveMainQuest> frozen) =>
        !frozen.IsDefault && active.Count == frozen.Length && active.All(row => row.AcceptanceId != Guid.Empty && frozen.Any(before =>
            before.AcceptanceId == row.AcceptanceId && before.Slot == row.Slot && before.QuestId == row.QuestId));

    // 601会广播同一原生事件给全部任务，混有未接入声明时必须抑制，不能只证明其中一个任务安全。
    internal static bool CanTriggerNativeAntonQuests(IReadOnlyList<ActiveMainQuest> active, QuestCatalog? catalog) =>
        active.All(row => catalog is not null && catalog.TryGet(row.QuestId, out var quest) &&
            (quest.TargetKind != QuestTargetKind.RaidPhaseClear ||
                QuestRaidPhaseProgress.Supports(quest) && QuestCoverage.HasOrdinaryDeclarations(quest)));

    internal QuestLifecycleReceipt? CaptureAntonPhaseQuestConditions(AntonRaidBattleView view) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } raid ||
            raid.Battle.Instance != view.Instance || raid.Battle.View.Phase != view.Phase ||
            raid.Battle.AntonEligibility(character.Id, view.Phase) is not
                { Eligible: true, AntonQuestProgress: not null }) return null;
        return new QuestLifecycleReceipt(character, null, owner.Generation, SelectionEpoch, Phase,
            formalDungeon?.Run.Snapshot.Identity, "安徒恩阶段任务权威回读") { Outcome = QuestLifecycleOutcome.Unchanged };
    });

    // 原生601随后发33；这里只确认已经落库的指定通道，不以网络回调再次推进阶段任务。
    internal QuestLifecycleReceipt? ObserveAntonPhaseQuestTrigger(ushort questId, QuestClientCondition condition,
        QuestCatalog catalog) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || condition is not { Channel: 0x10 or 0x20 or 0x40, Value: 0, Reserved: 0 } ||
            raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Anton } raid ||
            raid.Battle.View is not { State: AntonRaidBattleState.Reward } view ||
            raid.Battle.AntonEligibility(character.Id, view.Phase) is not { Eligible: true, AntonQuestProgress: { } progress } ||
            !catalog.TryGet(questId, out var quest) || !QuestRaidPhaseProgress.Supports(quest) ||
            character.ActiveMainQuests.SingleOrDefault(row => row.QuestId == questId) is not { } active || active.AcceptanceId == Guid.Empty ||
            progress.Before.SingleOrDefault(row => row.AcceptanceId == active.AcceptanceId && row.QuestId == questId && row.Slot == active.Slot) is not { } before ||
            progress.After.SingleOrDefault(row => row.AcceptanceId == active.AcceptanceId && row.QuestId == questId && row.Slot == active.Slot) is not { } after) return null;
        int channel = condition.Channel == 0x10 ? 0 : condition.Channel == 0x20 ? 1 : 2;
        if (channel >= quest.IntData.Count / 4 || quest.IntData[channel * 4] != view.Phase ||
            (before.ConditionState >> (channel * 9) & 511) == 0 ||
            (before.ConditionState >> (channel * 9) & 511) != (after.ConditionState >> (channel * 9) & 511) + 1) return null;
        return new QuestLifecycleReceipt(character, null, owner.Generation, SelectionEpoch, Phase,
            formalDungeon?.Run.Snapshot.Identity, "安徒恩已提交阶段通道确认") { Outcome = QuestLifecycleOutcome.Unchanged };
    });
}
