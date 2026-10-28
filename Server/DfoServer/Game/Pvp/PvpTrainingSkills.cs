using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Pvp;

// 训练技能是独立快照；不是角色普通双树的第三页，也不把训练加点写回普通加点。
internal sealed record PvpTrainingSkills(CharacterLearningState Learning,ushort QuestId,Guid AcceptanceId)
{
    public CharacterLearningState? Arena { get; init; }
    public byte? ArenaJob { get; init; }
    public byte? ArenaGrowType { get; init; }
    public byte CopyChoice { get; init; }
    public ushort CopyQuestId { get; init; }
    public Guid CopyAcceptanceId { get; init; }
    public void Validate()
    {
        Learning.Validate();
        Arena?.Validate();
        if(!Learning.Initialized || (QuestId==0) != (AcceptanceId==Guid.Empty))
            throw new InvalidDataException("训练技能快照或接取身份无效");
        if(Arena is {Initialized:false} || (ArenaJob is null)!=(ArenaGrowType is null) ||
            ArenaGrowType is >15 || ArenaJob is not null && Arena is null || CopyChoice>2 ||
            (CopyChoice==0 ? CopyQuestId!=0 || CopyAcceptanceId!=Guid.Empty :
                CopyQuestId is not (2674 or 2678) || CopyAcceptanceId==Guid.Empty))
            throw new InvalidDataException("正式决斗技能或训练复制身份无效");
    }
    public static CharacterLearningState Source(Character character) => character.PvpTrainingSkills?.Learning is { } stored
        ? WithSources(stored,character.Learning)
        : character.Learning;
    public static CharacterLearningState ArenaSource(Character character) => character.PvpTrainingSkills is {Arena: { } stored} profile &&
        (profile.ArenaJob is null || profile.ArenaJob==character.Job && profile.ArenaGrowType==(character.GrowType & 15))
        ? WithSources(stored.SelectedTree<0 && character.Learning.SelectedTree>=0 ? stored with {SelectedTree=0} : stored,character.Learning)
        : character.Learning;
    public static CharacterLearningState ArenaSource(Character character,SkillLearningCatalog skills)
    {
        var source=ArenaSource(character);
        return SkillPointLedger.Synchronize(character.Job,character.GrowType,character.Level,source,skills,
            skills.ActiveExpertContractLevels(source.ExpertContractExpiresAt,DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }
    static CharacterLearningState WithSources(CharacterLearningState stored,CharacterLearningState ordinary)
    {
        if(stored.Experience==ordinary.Experience && stored.BonusSp==ordinary.BonusSp && stored.BonusTp==ordinary.BonusTp &&
            stored.ExpertContractExpiresAt==ordinary.ExpertContractExpiresAt) return stored;
        int sp=stored.BonusSp is int oldSp && ordinary.BonusSp is int newSp ? newSp-oldSp : 0;
        int tp=stored.BonusTp is int oldTp && ordinary.BonusTp is int newTp ? newTp-oldTp : 0;
        return stored with {Experience=ordinary.Experience,BonusSp=ordinary.BonusSp,BonusTp=ordinary.BonusTp,
            ExpertContractExpiresAt=ordinary.ExpertContractExpiresAt,Trees=sp==0 && tp==0 ? stored.Trees :
                Array.AsReadOnly(stored.Trees.Select(tree=>tree with
                    {Sp=checked((ushort)(tree.Sp+sp)),Tp=checked((ushort)(tree.Tp+tp))}).ToArray())};
    }
}

internal enum PvpSkillPlanKind { Training, Arena, Copy }
internal sealed record PvpTrainingSkillPlan(Character Expected,PvpTrainingSkills After,
    ActiveMainQuest[] ActiveAfter,SkillLearningResult Result)
{
    public Guid OperationId { get; }=Guid.NewGuid();
    public PvpSkillPlanKind Kind { get; init; }
    public static PvpTrainingSkillPlan Arena(Character expected,CharacterLearningState learning)
    {
        var profile=expected.PvpTrainingSkills ?? new(PvpTrainingSkills.Source(expected),0,Guid.Empty);
        var after=profile with {Learning=PvpTrainingSkills.Source(expected),Arena=learning,
            ArenaJob=expected.Job,ArenaGrowType=(byte)(expected.GrowType & 15)};
        after.Validate();
        return new(expected,after,expected.ActiveMainQuests,new(SkillLearningFailure.None,learning,[])) {Kind=PvpSkillPlanKind.Arena};
    }

    public static PvpTrainingSkillPlan? Copy(Character expected,byte choice,SkillLearningCatalog? skills=null)
    {
        if(choice is not (1 or 2) || expected.PvpTrainingSkills is not {QuestId:2674 or 2678} profile ||
            profile.AcceptanceId==Guid.Empty || !DfoServer.Game.Quests.QuestEligibility.Completed(expected).Contains(profile.QuestId)) return null;
        if(profile.CopyChoice!=0) return null;
        var arena=PvpTrainingSkills.ArenaSource(expected);
        var training=PvpTrainingSkills.Source(expected);
        if(choice==1 && skills is not null)
        {
            int contract=skills.ActiveExpertContractLevels(expected.Learning.ExpertContractExpiresAt,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            training=SkillPointLedger.Synchronize(expected.Job,expected.GrowType,expected.Level,training,skills,contract);
        }
        if(choice==1)
        {
            // 本服兼容页规则：只把训练活动页复制到正式决斗活动页，保留两边页选择及其它页。
            var source=training.Trees.Single(tree=>tree.Context==training.ActiveContext);
            arena=arena with {Trees=Array.AsReadOnly(arena.Trees.Select(tree=>tree.Context==arena.ActiveContext
                ? source.Freeze() with {Context=tree.Context} : tree).ToArray())};
        }
        var after=profile with {Learning=training,Arena=arena,ArenaJob=expected.Job,ArenaGrowType=(byte)(expected.GrowType & 15),
            CopyChoice=choice,CopyQuestId=profile.QuestId,CopyAcceptanceId=profile.AcceptanceId};
        after.Validate();
        return new(expected,after,expected.ActiveMainQuests,new(SkillLearningFailure.None,arena,[])) {Kind=PvpSkillPlanKind.Copy};
    }
    public static PvpTrainingSkillPlan? Prepare(Character expected,SkillLearningCommand request,
        SkillLearningCatalog skills,QuestCatalog quests) => Prepare(expected,request,quests,
            (source,command)=>SkillLearning.Plan(expected.Job,expected.GrowType,expected.Level,source,command,skills,
                DfoServer.Game.Quests.QuestEligibility.Completed(expected).Contains(355)));

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
        var state=(expected.PvpTrainingSkills ?? new(result.State,fact,acceptance)) with
            {Learning=result.State,QuestId=fact,AcceptanceId=acceptance};state.Validate();
        return new(expected,state,after,result);
    }
}
