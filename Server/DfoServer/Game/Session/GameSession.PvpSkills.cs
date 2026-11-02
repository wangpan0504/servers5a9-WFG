using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    (PvpTrainingSkillPlan Plan,object Request,long Epoch)? pendingPvpSkills;
    long? pvpTrainingSkillContextEpoch;
    public bool InPvpTrainingSkillArea => owner.Execute(()=>Selected?.Position is {Town:10,Area:4});
    public bool HasPvpTrainingSkillContext => owner.Execute(()=>Selected is not null && pvpTrainingSkillContextEpoch==SelectionEpoch);

    // 该标记只说明本次选角已排入训练对象初始化，不是另一份技能或在线状态。
    public bool CanPublishPvpTrainingSkillContext(SkillLearningReceipt receipt) => owner.Execute(()=>
        receipt.Generation==owner.Generation && receipt.SelectionEpoch==SelectionEpoch && Selected?.Id==receipt.Character.Id);

    public bool MarkPvpTrainingSkillsQueued(SkillLearningReceipt receipt) => owner.Execute(()=>
    {
        if(!CanPublishPvpTrainingSkillContext(receipt)) return false;
        pvpTrainingSkillContextEpoch=SelectionEpoch;
        return true;
    });

    public SkillLearningResult? ReadPvpTrainingSkills(bool initializing=false)=>owner.Execute(()=>
    {
        if ((!Ready && !initializing) || Selected is not {Position.Town:10,Position.Area:4,Learning.Initialized:true} character ||
            Phase!=SessionPhase.Town && !(initializing && Phase==SessionPhase.Unselected) ||
            questCatalog is null || !character.ActiveMainQuests.Any(row=>
                questCatalog.TryGet(row.QuestId,out var quest) && PvpTrainingExposure.Allows(character,quest) && PvpTrainingProgress.Valid(quest,row.ConditionState) && row.ConditionState!=0)) return null;
        var source=arenaSkillCatalog is { } catalog ? PvpTrainingSkills.Source(character,catalog,SkillLevel(character,catalog)) :
            PvpTrainingSkills.Source(character);
        return new SkillLearningResult(SkillLearningFailure.None,source,[])
        {Receipt=new(character,owner.Generation,SelectionEpoch){Phase=Phase}};
    });

    public bool CanPublishPvpTrainingSkills(SkillLearningReceipt receipt, bool initializing) => owner.Execute(() =>
        (Ready || initializing) && receipt.Generation == owner.Generation && Phase == receipt.Phase &&
        SelectionEpoch == receipt.SelectionEpoch && ReferenceEquals(Selected, receipt.Character));

    public SkillLearningResult LearnPvpTrainingSkills(SkillLearningCommand request,SkillLearningCatalog skills) =>
        ApplyPvpTrainingSkills(request,character=>PvpTrainingSkillPlan.Prepare(character,request,skills,questCatalog!,SkillLevel(character,skills)));
    public SkillLearningResult ResetPvpTrainingSkills(byte context,byte mode,SkillLearningCatalog skills) =>
        ApplyPvpTrainingSkills((context,mode),character=>PvpTrainingSkillPlan.Reset(character,context,mode,skills,questCatalog!,SkillLevel(character,skills)));
    public SkillLearningResult MovePvpTrainingSkills(SkillSlotChangeCommand request,SkillLearningCatalog skills) =>
        ApplyPvpTrainingSkills(request,character=>PvpTrainingSkillPlan.Move(character,request,skills,questCatalog!,SkillLevel(character,skills)),layout:true);

    SkillLearningResult ApplyPvpTrainingSkills(object request,Func<Character,PvpTrainingSkillPlan?> prepare,bool layout=false)
    {
        var capture=owner.Execute(()=>(Character:SkillReady()?Selected:null,SelectionEpoch,Pending:pendingPvpSkills,Training:pvpTraining));
        if(capture.Character is not { } expected || questCatalog is null) return Refused();
        PvpTrainingSkillPlan? plan;
        if(capture.Pending is { } pending)
        {
            if(pending.Epoch!=capture.SelectionEpoch || !ReferenceEquals(pending.Plan.Expected,expected) ||
                !SameRequest(pending.Request,request)) return Refused();
            plan=pending.Plan;
        }
        else plan=prepare(expected);
        if(plan is null) return Refused();
        return owner.Execute(()=>
        {
            if(!SkillReady() || SelectionEpoch!=capture.SelectionEpoch || !ReferenceEquals(Selected,expected) ||
                pendingPvpSkills!=capture.Pending || !ReferenceEquals(pvpTraining,capture.Training))
                throw new InvalidOperationException("训练技能原选角、实例或待提交计划失效");
            if (capture.Pending is null && plan.After == expected.PvpTrainingSkills && plan.ActiveAfter.SequenceEqual(expected.ActiveMainQuests))
                return plan.Result with {Receipt=new(expected,owner.Generation,SelectionEpoch){Phase=Phase}};
            pendingPvpSkills=(plan,request,capture.SelectionEpoch);
            var committed=repository.CommitPvpTrainingSkills(owner,plan);
            Selected=committed;completed=QuestEligibility.Completed(committed);pendingPvpSkills=null;
            if(capture.Training is { } run)
            {
                // 合法换槽只更新原实例的角色快照，保留接取UUID、连招与回合事实。
                pvpTraining=run with {Entry=run.Entry with {Expected=committed,
                    Quest=committed.ActiveMainQuests.Single(row=>row.AcceptanceId==run.Entry.Quest.AcceptanceId)}};
                pvpTrainingVersion++;
            }
            return plan.Result with {Receipt=new(committed,owner.Generation,SelectionEpoch){Phase=Phase}};
        });
        bool SkillReady()=>Ready && Selected?.Position is {Town:10,Area:4} &&
            pendingRental is null && pendingCommunication is null && pendingQuestCompletion is null &&
            pendingQuestLifecycle is null && pendingQuestQuickClear is null && !HasPendingQuestScene &&
            CurrentParty?.Invoke() is null &&
            (Phase==SessionPhase.Town && pvpTraining is null || layout && Phase==SessionPhase.PvpRoom &&
                pvpTraining is {Entry.IsCombo:true} run && run.Generation==owner.Generation &&
                run.SelectionEpoch==SelectionEpoch && ReferenceEquals(Selected,run.Entry.Expected) &&
                Selected.ActiveMainQuests.Any(row=>ReferenceEquals(row,run.Entry.Quest)));
        static bool SameRequest(object left,object right) => left is SkillLearningCommand a && right is SkillLearningCommand b
            ? a.Context==b.Context && a.Mode==b.Mode && a.Entries.SequenceEqual(b.Entries) : Equals(left,right);
        static SkillLearningResult Refused()=>new(SkillLearningFailure.Unsupported,null,[]);
    }

    QuestLifecycleReceipt? ReadPvpSkillTrigger(ushort questId,QuestClientCondition condition)
    {
        if(!Ready || Phase is not (SessionPhase.Town or SessionPhase.PvpRoom) ||
            Selected is not {Position.Town:10,Position.Area:4,PvpTrainingSkills: { } skills} character ||
            condition is not {Channel:0x10,Value:0,Reserved:0} || questCatalog is null ||
            !questCatalog.TryGet(questId,out var quest) || !PvpTrainingExposure.Allows(character,quest) || !PvpTrainingProgress.Matches(quest) || quest.IntData[3]==2 ||
            !character.ActiveMainQuests.Any(row=>row.QuestId==questId && row.AcceptanceId==skills.AcceptanceId &&
                skills.QuestId==questId && row.AcceptanceId!=Guid.Empty && PvpTrainingProgress.Valid(quest,row.ConditionState) &&
                (row.ConditionState & 511)==0)) return null;
        return new(character,null,owner.Generation,SelectionEpoch,Phase,null,"自动训练加点已同事务提交")
            {Outcome=QuestLifecycleOutcome.Unchanged};
    }
}
