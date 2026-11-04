using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 只保存本次呈现身份，不持有另一份Run、角色资产或旧Session引用。
    sealed class AntonRejoinPresentation(long generation, long selectionEpoch, DungeonRunIdentity identity)
    {
        internal long Generation { get; } = generation;
        internal long SelectionEpoch { get; } = selectionEpoch;
        internal DungeonRunIdentity Identity { get; } = identity;
        internal bool Written { get; set; }
    }
    AntonRejoinPresentation? antonRejoinPresentation;

    bool AntonRejoinPresentationPending => Ready && Selected is not null &&
        antonRejoinPresentation is { } pending && pending.Generation == owner.Generation &&
        pending.SelectionEpoch == SelectionEpoch && formalDungeon is { } dungeon &&
        dungeon.Entry.RaidEntry?.Kind == Game.Raids.RaidKind.Anton &&
        dungeon.Run.Snapshot.Identity == pending.Identity &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement;

    internal bool HasAntonRejoinPresentation => owner.Execute(() => AntonRejoinPresentationPending);
    internal bool AntonRejoinSceneWritten => owner.Execute(() => AntonRejoinPresentationPending && antonRejoinPresentation!.Written);

    internal ushort? CaptureAntonRejoinDeadMember(FormalDungeonReceipt scene) => owner.Execute<ushort?>(() =>
        Ready && Selected is { } character && formalDungeon is { CharacterDead: true } dungeon &&
        dungeon.Entry.RaidEntry?.Kind == Game.Raids.RaidKind.Anton && IsCurrentRaidDungeon(dungeon.Entry) &&
        PartyDungeon?.Id == scene.Snapshot.Identity.InstanceId &&
        dungeon.Run.Snapshot.Identity.InstanceId == scene.Snapshot.Identity.InstanceId &&
        dungeon.Run.Snapshot.CurrentMap == scene.Snapshot.CurrentMap ? character.Id : null);

    internal void MarkAntonRejoinSceneWritten(FormalDungeonReceipt scene) => owner.Execute(() =>
    {
        if (!AntonRejoinPresentationPending || !CanPublishNativeScene(scene)) return false;
        antonRejoinPresentation!.Written = true;
        return true;
    });

    internal FormalDungeonReceipt? CompleteAntonRejoinPresentation() => owner.Execute(() =>
    {
        if (!AntonRejoinPresentationPending || !antonRejoinPresentation!.Written || PartyDungeon is not { IsExecuting: true } ||
            formalDungeon is not { } dungeon || !IsCurrentRaidDungeon(dungeon.Entry)) return null;
        // 37仅确认这个新客户端已装好原场景；原Run、任务初始化、疲劳和费用均不重放。
        var pending = antonRejoinPresentation;
        antonRejoinPresentation = null;
        if (Phase != SessionPhase.Loading) return FormalReceipt();
        try
        {
            // 原业务确在Loading时，仍由原正式路径提交一次真实加载。
            var receipt = CompleteFormalDungeonLoading();
            if (receipt is null) antonRejoinPresentation = pending;
            return receipt;
        }
        catch
        {
            antonRejoinPresentation = pending;
            throw;
        }
    });
}
