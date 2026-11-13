using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed record PvpPrivateCreationCheck(byte Job,byte Grow,long Generation,long SelectionEpoch,bool Allowed);

internal sealed partial class GameSession
{
    PvpPrivateCreationCheck? pendingPvpPrivateCreation;

    public PvpPrivateCreationCheck CheckPvpPrivateCreation(NewCharacter? prepared)=>owner.Execute(()=>
    {
        bool allowed=Selected is null && Phase==SessionPhase.Unselected && prepared?.PvpPrivateCreation is {} creation &&
            repository.CanCreatePvpPrivate(owner,prepared.Job,(byte)(prepared.GrowType&15),creation);
        var result=new PvpPrivateCreationCheck(prepared?.Job??0,prepared?.GrowType??0,owner.Generation,SelectionEpoch,allowed);
        pendingPvpPrivateCreation=allowed ? result : null;
        return result;
    });

    public bool CanPublishPvpPrivateCreationCheck(PvpPrivateCreationCheck receipt)=>owner.Execute(()=>
        Selected is null && Phase==SessionPhase.Unselected && receipt.Generation==owner.Generation && receipt.SelectionEpoch==SelectionEpoch &&
        (!receipt.Allowed || ReferenceEquals(pendingPvpPrivateCreation,receipt)));

    public CharacterCreationReceipt? CreatePvpPrivateCharacterForClient(NewCharacter prepared)=>owner.Execute(()=>
    {
        if(prepared.PvpPrivateCreation is null || Selected is not null || Phase!=SessionPhase.Unselected ||
            pendingPvpPrivateCreation is not {Allowed:true} choice || choice.Generation!=owner.Generation ||
            choice.SelectionEpoch!=SelectionEpoch || choice.Job!=prepared.Job || choice.Grow!=prepared.GrowType) return null;
        // 初次资格只负责打开命名界面；最终事务再次核验等级、唯一分支和冷却，失败不半建角色。
        if(!repository.CanCreatePvpPrivate(owner,prepared.Job,prepared.GrowType,prepared.PvpPrivateCreation)) return null;
        var created=repository.Create(owner,prepared);
        pendingPvpPrivateCreation=null;SelectionEpoch++;
        return new CharacterCreationReceipt(created,CaptureRoster());
    });

    public Character? ConvertSelectedPvpPrivateToNormal() => owner.Execute(() =>
    {
        if (Selected is not { IsPvpPrivate: true } current)
            return null;
        var converted = repository.ConvertPvpPrivateToNormal(owner, current);
        Selected = converted;
        return converted;
    });
}
