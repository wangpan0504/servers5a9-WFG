using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    (PvpTrainingSkillPlan Plan,object Request,long Epoch)? pendingPvpSkills;
    public bool InPvpTrainingSkillArea => owner.Execute(()=>Selected?.Position is {Town:10,Area:4});

    public SkillLearningResult? ReadPvpTrainingSkills(bool initializing=false)=>owner.Execute(()=>
    {
        if ((!Ready && !initializing) || Selected is not {Position.Town:10,Position.Area:4,Learning.Initialized:true} character ||
            Phase!=SessionPhase.Town || questCatalog is null || !character.ActiveMainQuests.Any(row=>
                questCatalog.TryGet(row.QuestId,out var quest) && PvpTrainingExposure.Allows(character,quest) && PvpTrainingProgress.Valid(quest,row.ConditionState) && row.ConditionState!=0)) return null;
        return new SkillLearningResult(SkillLearningFailure.None,PvpTrainingSkills.Source(character),[])
        {Receipt=new(character,owner.Generation,SelectionEpoch){Phase=Phase}};
    });

    public bool CanPublishPvpTrainingSkills(SkillLearningReceipt receipt, bool initializing) => owner.Execute(() =>
        (Ready || initializing) && receipt.Generation == owner.Generation && Phase == receipt.Phase &&
        SelectionEpoch == receipt.SelectionEpoch && ReferenceEquals(Selected, receipt.Character));

    public SkillLearningResult LearnPvpTrainingSkills(SkillLearningCommand request,SkillLearningCatalog skills) =>
        ApplyPvpTrainingSkills(request,character=>PvpTrainingSkillPlan.Prepare(character,request,skills,questCatalog!));
    public SkillLearningResult ResetPvpTrainingSkills(byte context,byte mode,SkillLearningCatalog skills) =>
        ApplyPvpTrainingSkills((context,mode),character=>PvpTrainingSkillPlan.Reset(character,context,mode,skills,questCatalog!));
    public SkillLearningResult MovePvpTrainingSkills(SkillSlotChangeCommand request,SkillLearningCatalog skills) =>
        ApplyPvpTrainingSkills(request,character=>PvpTrainingSkillPlan.Move(character,request,skills,questCatalog!));

    SkillLearningResult ApplyPvpTrainingSkills(object request,Func<Character,PvpTrainingSkillPlan?> prepare)
    {
        var capture=owner.Execute(()=>(Character:SkillReady()?Selected:null,SelectionEpoch,Pending:pendingPvpSkills));
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
                pendingPvpSkills!=capture.Pending) throw new InvalidOperationException("训练技能原选角或待提交计划失效");
            pendingPvpSkills=(plan,request,capture.SelectionEpoch);
            var committed=repository.CommitPvpTrainingSkills(owner,plan);
            Selected=committed;completed=QuestEligibility.Completed(committed);pendingPvpSkills=null;
            return plan.Result with {Receipt=new(committed,owner.Generation,SelectionEpoch){Phase=Phase}};
        });
        bool SkillReady()=>Ready && Phase==SessionPhase.Town && Selected?.Position is {Town:10,Area:4} &&
            pendingRental is null && pendingCommunication is null && pendingQuestCompletion is null &&
            pendingQuestLifecycle is null && pendingQuestQuickClear is null && !HasPendingQuestScene &&
            singleParty is null && pvpTraining is null;
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
