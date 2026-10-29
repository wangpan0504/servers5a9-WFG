using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public bool UsesArenaSkills => ChannelEntry?.Type is 4 or 5 or 8 or 24;
    SkillLearningCatalog? arenaSkillCatalog;
    public CharacterLearningState ReadSkillLearning(Character character) => UsesArenaSkills
        ? arenaSkillCatalog is { } skills ? PvpTrainingSkills.ArenaSource(character,skills) : PvpTrainingSkills.ArenaSource(character)
        : character.Learning;
    public Character ReadSkillCharacter(Character character) => UsesArenaSkills ? character with {Learning=ReadSkillLearning(character)} : character;

    void InitializeArenaLearning(SkillLearningCatalog catalog)
    {
        var capture=owner.Execute(()=>(Character:Selected!,SelectionEpoch,Ready,Phase));
        var source=PvpTrainingSkills.ArenaSource(capture.Character);
        var after=SkillPointLedger.Synchronize(capture.Character.Job,capture.Character.GrowType,capture.Character.Level,
            source,catalog,catalog.ActiveExpertContractLevels(source.ExpertContractExpiresAt,DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if(after==capture.Character.PvpTrainingSkills?.Arena) return;
        var plan=PvpTrainingSkillPlan.Arena(capture.Character,after);
        owner.Execute(()=>
        {
            if(!ReferenceEquals(Selected,capture.Character) || SelectionEpoch!=capture.SelectionEpoch || Ready!=capture.Ready || Phase!=capture.Phase)
                throw new InvalidOperationException("正式决斗技能初始化的原选角失效");
            Selected=repository.CommitPvpTrainingSkills(owner,plan);
            return true;
        });
    }

    Character CommitActiveSkills(Character expected,CharacterLearningState after,int? totalSp=null,int? totalTp=null,bool layout=false) =>
        UsesArenaSkills ? repository.CommitPvpTrainingSkills(owner,PvpTrainingSkillPlan.Arena(expected,after)) :
            layout ? repository.CommitSkillLayout(owner,expected,after) : repository.CommitLearning(owner,expected,after,totalSp,totalTp);

    (PvpTrainingSkillPlan Plan,long Epoch)? pendingTrainingCopy;
    public SkillLearningReceipt? ChooseTrainingSkills(byte choice,SkillLearningCatalog skills)
    {
        var capture=owner.Execute(()=>(Character:CopyReady()?Selected:null,SelectionEpoch,Pending:pendingTrainingCopy));
        if(capture.Character is not { } character) return null;
        var plan=capture.Pending is { } pending && pending.Epoch==capture.SelectionEpoch &&
            ReferenceEquals(pending.Plan.Expected,character) && pending.Plan.After.CopyChoice==choice ? pending.Plan :
            capture.Pending is null ? PvpTrainingSkillPlan.Copy(character,choice,skills) : null;
        if(plan is null) return null;
        return owner.Execute(()=>
        {
            if(!CopyReady() || SelectionEpoch!=capture.SelectionEpoch || !ReferenceEquals(Selected,character) || pendingTrainingCopy!=capture.Pending)
                throw new InvalidOperationException("训练复制的原选角或计划失效");
            pendingTrainingCopy=(plan,capture.SelectionEpoch);
            Selected=repository.CommitPvpTrainingSkills(owner,plan);
            pendingTrainingCopy=null;
            return new SkillLearningReceipt(Selected,owner.Generation,SelectionEpoch) {Phase=Phase};
        });
        bool CopyReady()=>Ready && Phase==SessionPhase.Town && Selected is not null && pvpTraining is null &&
            CurrentParty?.Invoke() is null && pvpMatching is null && pendingRental is null &&
            (!HasPendingTownQuestCommit || pendingTrainingCopy is not null);
    }
}
