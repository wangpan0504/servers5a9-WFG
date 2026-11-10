using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record HopeSelection(ushort CharacterId, long SelectionEpoch, OperationHopeAdmission Admission);
    HopeSelection? hopeSelection;

    // 命令层先持有队伍和各连接门禁；全部选择成功才保留，失败不留下半队抽签。
    public bool BeginPartyOperationHope(OperationHopeCatalog catalog, DungeonCatalog dungeons, Func<int,int>? draw = null)
    {
        var party = CurrentParty?.Invoke();
        if (party is null) return BeginOperationHope(catalog,dungeons,draw);
        if (!party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session,this))) return false;
        return AccountOwners.Lease.ExecuteGroup(party.Members.Select(m => (m.Actor.Owner,m.Actor.Character.Id)).ToArray(), () =>
        {
            if (!ReferenceEquals(CurrentParty?.Invoke(),party) || party.Members.Any(m => !m.IsCurrent() ||
                m.Actor.Session.Selected?.Position.Town != Selected?.Position.Town ||
                m.Actor.Session.Selected?.Position.Area != Selected?.Position.Area)) return false;
            var previous = party.Members.Select(m => (Session:m.Actor.Session,Selection:m.Actor.Session.hopeSelection)).ToArray();
            bool accepted = false;
            try
            {
                if (!BeginOperationHope(catalog,dungeons,draw)) return false;
                int maze = hopeSelection!.Admission.MazeOrdinal;
                foreach (var member in party.Members.Where(m => !ReferenceEquals(m.Actor.Session,this)))
                    if (!member.Actor.Session.BeginOperationHope(catalog,dungeons,draw,maze)) return false;
                accepted = true;
                return true;
            }
            finally
            {
                if (!accepted)
                    foreach (var member in previous) member.Session.hopeSelection = member.Selection;
            }
        });
    }

    // 1598 只固定本次合法入场选择；任务和首区疲劳在16的原入场事务中一起提交。
    public bool BeginOperationHope(OperationHopeCatalog catalog, DungeonCatalog dungeons, Func<int,int>? draw = null,
        int? requiredMaze = null)
    {
        var capture = owner.Execute(() => (Character: Ready && Phase is SessionPhase.Town or SessionPhase.MapSelect &&
            formalDungeon is null && !HasPendingTownQuestCommit ? Selected : null, Epoch: SelectionEpoch, Previous: hopeSelection));
        if (capture.Character is not { IsPvpPrivate: false } character || character.Level is < 95 or > 99 ||
            character.DungeonState is null || character.ActiveMainQuests.Length >= 30 ||
            character.ActiveMainQuests.Any(row => catalog.Missions.ContainsKey(row.QuestId))) return false;
        HopeSelection selection;
        if (capture.Previous is { } previous && previous.CharacterId == character.Id && previous.SelectionEpoch == capture.Epoch)
        {
            // 位置或背包修改不能把已抽取任务永久锁死，也不能借刷新角色版本重新抽签。
            var admission = previous.Admission with { SourceRevision = character.Revision };
            if (!ReferenceEquals(admission.Catalog,catalog) || requiredMaze is { } required && admission.MazeOrdinal != required || character.ActiveMainQuests.Any(row =>
                row.Slot == admission.Acceptance.Slot || row.AcceptanceId == admission.Acceptance.AcceptanceId) ||
                !DungeonEntryPlan.TryCreate(dungeons,character,8523,0,catalog.Difficulty,out _,out _,
                    draw:_ => 0,quests:questCatalog,hope:admission)) return false;
            selection = previous with { Admission = admission };
        }
        else
        {
            int mazeOrdinal;
            if (requiredMaze is { } sharedMaze)
            {
                if (!catalog.Mazes.ContainsKey(sharedMaze)) return false;
                mazeOrdinal = sharedMaze;
            }
            else
            {
                mazeOrdinal = OperationHopePlan.ChooseMaze(catalog,draw);
            }
            if (OperationHopePlan.ChooseMission(catalog,mazeOrdinal,draw) is not { } questId) return false;
            byte slot = Enumerable.Range(0,30).Select(value => (byte)value).First(value => character.ActiveMainQuests.All(row => row.Slot != value));
            var active = new ActiveMainQuest(slot,checked((ushort)questId),1) { AcceptanceId = Guid.NewGuid() };
            var admission = new OperationHopeAdmission(catalog,mazeOrdinal,active,character.Revision);
            if (!DungeonEntryPlan.TryCreate(dungeons,character,8523,0,catalog.Difficulty,out _,out _,
                draw:_ => 0,quests:questCatalog,hope:admission)) return false;
            selection = new(character.Id,capture.Epoch,admission);
        }
        return owner.Execute(() =>
        {
            if (!Ready || !ReferenceEquals(Selected,character) || SelectionEpoch != capture.Epoch || formalDungeon is not null ||
                Phase is not (SessionPhase.Town or SessionPhase.MapSelect) || HasPendingTownQuestCommit ||
                !ReferenceEquals(hopeSelection,capture.Previous)) return false;
            hopeSelection = selection;
            return true;
        });
    }

    OperationHopeAdmission? CurrentHopeAdmission(Character expected, long epoch) => owner.Execute(() =>
        hopeSelection is { } value && value.CharacterId == expected.Id && value.SelectionEpoch == epoch &&
        value.Admission.SourceRevision == expected.Revision ? value.Admission : null);
    public bool IsOperationHopeQuest(ushort questId) => owner.Execute(() => formalDungeon?.Run.HopeBinding?.Acceptance.QuestId == questId);

    public QuestLifecycleReceipt? ObserveOperationHopeTrigger(ushort questId, QuestClientCondition condition) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) ||
            condition is not { Channel: 0x10, Value: 0, Reserved: 0 } ||
            formalDungeon is not { } dungeon || dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement ||
            dungeon.Run.HopeBinding is not { } binding || binding.Acceptance.QuestId != questId ||
            character.ActiveMainQuests.SingleOrDefault(row => row.QuestId == questId) is not { } active ||
            active.AcceptanceId != binding.Acceptance.AcceptanceId || active.Slot != binding.Acceptance.Slot ||
            active.ConditionState > 1 || !QuestHuntProgress.Bound(active, dungeon.Entry)) return null;
        // 希望是专用分类，不能走普通猎杀资源门禁；33只回读已提交的死亡事实，不减少次数或写库。
        return new QuestLifecycleReceipt(character, null, owner.Generation, SelectionEpoch, Phase, dungeon.Run.Snapshot.Identity,
            "希望任务回读权威进度") { Outcome = QuestLifecycleOutcome.Unchanged };
    });

    public OperationHopeFinishReceipt? FinishOperationHope(ushort questId, int selector, int multiplier,
        GameWorld.Quests.QuestCatalog quests, GameWorld.Quests.QuestRewardParameterCatalog parameters,
        GameWorld.Items.ItemCatalog itemCatalog, GameWorld.Items.GoldLimitCatalog goldLimits,
        GameWorld.Items.EpicPieceCatalog pieces, GameWorld.Characters.CharacterGrowthCatalog growth)
    {
        var capture = owner.Execute(() => (Character: pendingRental is null && pendingCommunication is null &&
            pendingQuestLifecycle is null && pendingQuestCompletion is null && pendingQuestQuickClear is not { CommitStarted: true } &&
            !HasPendingFormalCard && !HasPendingQuestScene && formalDungeon?.Ground.HasPendingOperation != true
            ? Selected : null, Dungeon: formalDungeon, Epoch: SelectionEpoch, Pending: formalDungeon?.PendingHopeReward));
        if (!Ready || capture.Character is not { } expected || capture.Dungeon is not { } dungeon ||
            selector != ushort.MaxValue || multiplier != 1 || dungeon.Run.HopeCatalog is not { } catalog ||
            dungeon.Run.HopeBinding is not { } binding || binding.Acceptance.QuestId != questId) return null;
        var plan = capture.Pending ?? OperationHopeRewardPlan.Prepare(expected,catalog,binding,dungeon.Run,
            quests,parameters,itemCatalog,goldLimits,pieces,growth,(timeProvider ?? TimeProvider.System).GetUtcNow()).Plan;
        if (plan is null) return null;
        plan.BattlePassCompletion ??= DfoServer.Game.BattlePass.BattlePassOperationHopeCompletion.Prepare(plan, catalog, dungeon.Run, timeProvider ?? TimeProvider.System);
        return owner.Execute(() =>
        {
            if (!Ready || SelectionEpoch != capture.Epoch || !ReferenceEquals(Selected,expected) ||
                !ReferenceEquals(formalDungeon,dungeon) || Phase != SessionPhase.Settlement ||
                dungeon.Run.Snapshot.Identity != plan.Identity || dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement ||
                pendingRental is not null || pendingCommunication is not null || pendingQuestLifecycle is not null ||
                pendingQuestCompletion is not null || pendingQuestQuickClear is { CommitStarted: true } ||
                HasPendingFormalCard || HasPendingQuestScene || dungeon.Ground.HasPendingOperation ||
                dungeon.PendingHopeReward is not null && !ReferenceEquals(dungeon.PendingHopeReward,plan))
                return null;
            if (dungeon.PendingHopeReward is null)
            {
                var active = expected.ActiveMainQuests.SingleOrDefault(row => row.AcceptanceId == binding.Acceptance.AcceptanceId);
                if (active is null || !OperationHopePlan.CanClaim(catalog,binding,dungeon.Run,active)) return null;
                dungeon.PendingHopeReward = plan;
            }
            var committed = repository.CommitOperationHopeReward(owner,plan);
            if (!committed.ProjectionCurrent) return new OperationHopeFinishReceipt(capture.Epoch,plan.Identity,questId,committed)
                { BattlePassMissionAt = plan.BattlePassCompletion?.Clock.GetUtcNow() };
            dungeon.HopeRewardConfirmed = true;
            Selected = committed.Character;
            return new OperationHopeFinishReceipt(capture.Epoch,plan.Identity,questId,committed)
                { BattlePassMissionAt = plan.BattlePassCompletion?.Clock.GetUtcNow() };
        });
    }

    // 只在本次免费牌已经实际领取后尝试自动交付；未达成条件时仍由既有 CanClaim 拒绝。
    public OperationHopeFinishReceipt? FinishOperationHopeAfterCard(
        GameWorld.Quests.QuestCatalog quests, GameWorld.Quests.QuestRewardParameterCatalog parameters,
        GameWorld.Items.ItemCatalog itemCatalog, GameWorld.Items.GoldLimitCatalog goldLimits,
        GameWorld.Items.EpicPieceCatalog pieces, GameWorld.Characters.CharacterGrowthCatalog growth)
    {
        var questId = owner.Execute(() => Ready && Phase == SessionPhase.Settlement &&
            formalDungeon is { HopeRewardConfirmed: false, Settlement: { SelectedCard: not null }, Run.HopeBinding: { } binding }
                ? binding.Acceptance.QuestId : (ushort?)null);
        return questId is { } id
            ? FinishOperationHope(id, ushort.MaxValue, 1, quests, parameters, itemCatalog, goldLimits, pieces, growth)
            : null;
    }

    public bool CanPublishOperationHope(OperationHopeFinishReceipt receipt) => owner.Execute(() => Ready &&
        SelectionEpoch == receipt.SelectionEpoch && ReferenceEquals(Selected,receipt.Commit.Character) &&
        formalDungeon?.Run.Snapshot.Identity == receipt.Identity && Phase == SessionPhase.Settlement);

}

internal sealed record OperationHopeFinishReceipt(long SelectionEpoch, DungeonRunIdentity Identity,
    ushort QuestId, OperationHopeRewardCommit Commit)
{
    public DateTimeOffset? BattlePassMissionAt { get; init; }
}
