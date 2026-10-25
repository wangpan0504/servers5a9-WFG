using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    PvpTrainingRun? pvpTraining;
    long pvpTrainingVersion;

    public PvpTrainingLifecycleReceipt? BeginPvpTraining(Func<Character,PvpTrainingEntry?> prepare, bool blocked=false)
    {
        var capture=owner.Execute(()=>(Character:CanBegin()?Selected:null,SelectionEpoch));
        if(capture.Character is not { } character) return null;
        var entry=prepare(character);
        return owner.Execute(()=>
        {
            if(entry is null || !CanBegin() || capture.SelectionEpoch!=SelectionEpoch ||
                !ReferenceEquals(character,Selected) || !ReferenceEquals(entry.Expected,character) ||
                !character.ActiveMainQuests.Any(row=>ReferenceEquals(row,entry.Quest)) || entry.Quest.AcceptanceId==Guid.Empty)
                return null;
            pvpTraining=new(Guid.NewGuid(),entry,owner.Generation,SelectionEpoch);
            pvpTrainingVersion++;
            SetState(SessionPhase.PvpRoom);
            return new PvpTrainingLifecycleReceipt(pvpTraining,false,pvpTrainingVersion);
        });
        bool CanBegin()=>!blocked && Ready && Phase==SessionPhase.Town &&
            Selected is { Position.Town:10,Position.Area:4 } && pvpTraining is null && CurrentParty?.Invoke() is null &&
            pendingRental is null && !HasPendingTownQuestCommit && !HasActiveDungeonContinuation &&
            !HasPendingQuestReplay && !HasPendingFormalCard;
    }

    public PvpTrainingRun? ReadPvpTraining()=>owner.Execute(()=>pvpTraining);

    public PvpTrainingRoundReceipt? ReportPvpTraining(PvpTrainingRun expected,PvpArcadeRoundReport report)=>owner.Execute(()=>
    {
        if(expected.Entry.IsCombo || !Ready || Phase!=SessionPhase.PvpRoom || !ReferenceEquals(pvpTraining,expected) ||
            expected.Generation!=owner.Generation || expected.SelectionEpoch!=SelectionEpoch ||
            !ReferenceEquals(Selected,expected.Entry.Expected) ||
            !Selected.ActiveMainQuests.Any(row=>ReferenceEquals(row,expected.Entry.Quest)) ||
            expected.Entry.Quest.AcceptanceId==Guid.Empty) return null;
        var next=expected.Round.Apply(report,expected.Entry.Rounds,expected.Entry.RandomRounds);
        if(next is null) return null;
        pvpTraining=expected with {Round=next};
        pvpTrainingVersion++;
        // 此边沿仅供后续原子任务提交使用；接取UUID与实例仍须在提交时再次核验。
        return new PvpTrainingRoundReceipt(pvpTraining,!expected.Round.Complete && next.Complete);
    });

    // V31 015D8C6D→015C2140→02011CB0→0200F0A1发送对应通道的零值33。
    // 胜利报告不能直接发奖；这里只在原实例仍有效时原子消耗该事实。
    public QuestLifecycleReceipt? ObservePvpTrainingTrigger(ushort questId,QuestClientCondition condition,QuestCatalog quests,
        bool manual=false,ItemCatalog? items=null,GoldLimitCatalog? goldLimits=null,EpicPieceCatalog? pieces=null)=>owner.Execute(()=>
    {
        if(!manual && ReadPvpSkillTrigger(questId,condition) is { } skill) return skill;
        if (!Ready || Phase!=SessionPhase.PvpRoom || pvpTraining is not { } run ||
            (!run.Entry.IsCombo && !run.Round.Complete) ||
            run.Generation!=owner.Generation || run.SelectionEpoch!=SelectionEpoch ||
            !ReferenceEquals(Selected,run.Entry.Expected) || run.Entry.Quest.QuestId!=questId ||
            !quests.TryGet(questId,out var quest) || !PvpTrainingProgress.Valid(quest,run.Entry.Quest.ConditionState) ||
            condition.Value!=0 || condition.Reserved!=0 ||
            condition.Channel!=(run.Entry.IsCombo ? (byte)0x20 : quest.IntData[3]==2 ? (byte)0x10 : (byte)0x40) ||
            quest.Id==2679 && run.Entry.Mode?.Difficulty!=2 ||
            pendingRental is not null || pendingCommunication is not null || pendingQuestCompletion is not null ||
            pendingQuestQuickClear is not null) return null;
        bool hasManual=run.Entry.Expected.Inventory.Items.Any(item=>item.Position.List==DfoServer.Game.Inventory.Models.InventoryListType.Main &&
            item.Core.ItemId==PvpTrainingProgress.ManualItemId && item.Core.Count>0 && item.Core.EquipmentLockId==0 &&
            !item.Core.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if(manual && (!PvpTrainingProgress.Manual(quest) || hasManual || items is null || goldLimits is null || pieces is null) ||
            !manual && PvpTrainingProgress.Manual(quest) && condition.Channel==0x40 &&
                (run.Entry.Quest.ConditionState & (511u<<18))!=0 && !hasManual) return null;
        var expected=run.Entry.Expected;
        var receipt=new QuestLifecycleReceipt(expected,null,owner.Generation,SelectionEpoch,Phase,null,"训练实例结果已消费");
        QuestLifecyclePlan? plan;
        if (pendingQuestLifecycle is { } pending)
        {
            if (pending.Plan.QuestId!=questId || pending.Plan.Action!=QuestLifecycleAction.Condition ||
                !ReferenceEquals(pending.Plan.Expected,expected) || pending.Condition!=condition ||
                pending.SelectionEpoch!=SelectionEpoch || pending.Phase!=Phase || pending.Identity is not null ||
                manual!=pending.Plan.Inventory.Changed) return null;
            plan=pending.Plan;
        }
        else plan=manual ? QuestLifecyclePlan.ObtainPvpTrainingManual(expected,quest,run,items!,goldLimits!,pieces!) : run.Entry.IsCombo ? QuestLifecyclePlan.ObservePvpTrainingCombo(expected,quest,run) :
            QuestLifecyclePlan.ObservePvpTrainingWin(expected,quest,run);
        if (plan is null) return receipt with {Outcome=QuestLifecycleOutcome.Unchanged};
        pendingQuestLifecycle=new(plan,SelectionEpoch,Phase,null,condition);
        var commit=repository.CommitQuestLifecycle(owner,plan);
        receipt=receipt with {Character=commit.Character,Commit=commit,Outcome=QuestLifecycleOutcome.Committed};
        if (!commit.ProjectionCurrent) return receipt;
        Selected=commit.Character;
        completed=QuestEligibility.Completed(Selected);
        pvpTraining=run with {Entry=run.Entry with {Expected=Selected,
            Quest=Selected.ActiveMainQuests.Single(row=>row.AcceptanceId==plan.AcceptanceId)}};
        pvpTrainingVersion++;
        pendingQuestLifecycle=null;
        return receipt;
    });

    public PvpTrainingLifecycleReceipt? EndPvpTraining(Guid instance) => owner.Execute(()=>
    {
        if(!Ready || Phase!=SessionPhase.PvpRoom || pendingQuestLifecycle is not null || pvpTraining is not { } run || run.InstanceId!=instance ||
            run.Generation!=owner.Generation || run.SelectionEpoch!=SelectionEpoch) return null;
        SetState(SessionPhase.Town);
        pvpTrainingVersion++;
        return new PvpTrainingLifecycleReceipt(run,true,pvpTrainingVersion);
    });

    public bool CanPublishPvpTraining(PvpTrainingLifecycleReceipt receipt)=>owner.Execute(()=>Ready &&
        receipt.Run.Generation==owner.Generation && receipt.Run.SelectionEpoch==SelectionEpoch &&
        Selected?.Id==receipt.Run.Entry.Expected.Id && receipt.Version==pvpTrainingVersion &&
        (receipt.Removed ? Phase==SessionPhase.Town && pvpTraining is null :
            Phase==SessionPhase.PvpRoom && ReferenceEquals(pvpTraining,receipt.Run) &&
            ReferenceEquals(Selected,receipt.Run.Entry.Expected)));
}
