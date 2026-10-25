using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Pvp;

// 训练技能是独立快照；不是角色普通双树的第三页，也不把训练加点写回普通加点。
internal sealed record PvpTrainingSkills(CharacterLearningState Learning,ushort QuestId,Guid AcceptanceId)
{
    public void Validate()
    {
        Learning.Validate();
        if(!Learning.Initialized || (QuestId==0) != (AcceptanceId==Guid.Empty))
            throw new InvalidDataException("训练技能快照或接取身份无效");
    }
    public static CharacterLearningState Source(Character character) => character.PvpTrainingSkills?.Learning is { } stored
        ? stored with {Experience=character.Learning.Experience,BonusSp=character.Learning.BonusSp,
            BonusTp=character.Learning.BonusTp,ExpertContractExpiresAt=character.Learning.ExpertContractExpiresAt}
        : character.Learning;
}

internal sealed record PvpTrainingSkillPlan(Character Expected,PvpTrainingSkills After,
    ActiveMainQuest[] ActiveAfter,SkillLearningResult Result)
{
    public Guid OperationId { get; }=Guid.NewGuid();
    public static PvpTrainingSkillPlan? Prepare(Character expected,SkillLearningCommand request,
        SkillLearningCatalog skills,QuestCatalog quests) => Prepare(expected,request,quests,
            (source,command)=>SkillLearning.Plan(expected.Job,expected.GrowType,expected.Level,source,command,skills));

    public static PvpTrainingSkillPlan? Reset(Character expected,byte context,byte mode,SkillLearningCatalog skills,QuestCatalog quests) =>
        mode>1 ? null : Prepare(expected,new(context,0,[]),quests,(source,_)=>
            SkillPointLedger.Reset(expected.Job,expected.GrowType,expected.Level,source,context,skills,DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public static PvpTrainingSkillPlan? Move(Character expected,SkillSlotChangeCommand command,SkillLearningCatalog skills,QuestCatalog quests) =>
        Prepare(expected,new(command.Context,0,[]),quests,(source,_)=>
        {
            var result=SkillLayout.Move(expected.Job,source,command,skills.Get);
            return new(result.Failure,result.State,[]);
        });

    internal static PvpTrainingSkillPlan? Prepare(Character expected,SkillLearningCommand request,QuestCatalog quests,
        Func<CharacterLearningState,SkillLearningCommand,SkillLearningResult> learning)
    {
        if(expected.Position is not {Town:10,Area:4} || request.Context!=0 || request.Mode is not (0 or 1 or 3) ||
            !expected.Learning.Initialized) return null;
        var active=expected.ActiveMainQuests.Where(row=>quests.TryGet(row.QuestId,out var q) &&
            PvpTrainingExposure.Allows(expected,q) && PvpTrainingProgress.Valid(q,row.ConditionState) && row.ConditionState!=0).ToArray();
        if(active is not [var accepted] || accepted.AcceptanceId==Guid.Empty || !quests.TryGet(accepted.QuestId,out var quest)) return null;
        var source=PvpTrainingSkills.Source(expected);
        var result=learning(source,request with {Mode=request.Mode==3 ? (byte)1 : request.Mode});
        if(!result.Success || result.State is null) return null;
        var after=expected.ActiveMainQuests;
        ushort fact=expected.PvpTrainingSkills?.QuestId ?? 0;
        Guid acceptance=expected.PvpTrainingSkills?.AcceptanceId ?? Guid.Empty;
        if(request.Mode==3 && PvpTrainingProgress.TryApply(quest,accepted.ConditionState,PvpTrainingFact.SkillsCommitted,0,out uint scalar))
        {
            after=after.Select(row=>ReferenceEquals(row,accepted) ? row with {ConditionState=scalar} : row).ToArray();
            fact=accepted.QuestId;acceptance=accepted.AcceptanceId;
        }
        var state=new PvpTrainingSkills(result.State,fact,acceptance);state.Validate();
        return new(expected,state,after,result);
    }
}
