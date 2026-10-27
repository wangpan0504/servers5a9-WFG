using DfoServer.Game.Dungeons;
using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public LostLandTimerReceipt? StartedLostLandTimer(FormalDungeonReceipt loaded) => owner.Execute(() =>
    {
        if (!loaded.LostLandStarted || formalDungeon?.Run.LostLandClock is not { Started: true } clock) return null;
        // 加载包发送期间的正常资产提交不能让本实例丢失到期任务；这里绑定实例而非旧角色revision。
        var candidate = new LostLandTimerReceipt(loaded.Generation, loaded.SelectionEpoch, loaded.Snapshot.Identity.InstanceId,
            clock.Admission.OpaqueValue, clock.Remaining, clock.Expired);
        return MatchesLostLandTimer(candidate) ? candidate : null;
    });

    public bool CanPublishLostLandTimer(LostLandTimerReceipt receipt) => owner.Execute(() =>
        MatchesLostLandTimer(receipt) && formalDungeon!.Run.LostLandClock!.Expired == receipt.Expired);

    public LostLandTimerReceipt? TickLostLandTimer(LostLandTimerReceipt receipt) => owner.Execute(() =>
    {
        if (!MatchesLostLandTimer(receipt)) return null;
        var clock = formalDungeon!.Run.LostLandClock!;
        if (clock.TryExpire()) SetState(SessionPhase.Settlement);
        return LostLandTimer(clock);
    });

    public TownTravelReceipt? ReturnFromExpiredLostLand(LostLandTimerReceipt receipt) => owner.Execute(() =>
        receipt.Expired && MatchesLostLandTimer(receipt) && formalDungeon!.Run.LostLandClock!.Expired
            ? ReturnFromFormalDungeonCore(false) : null);

    bool MatchesLostLandTimer(LostLandTimerReceipt receipt) => Ready && Selected is not null &&
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement &&
        formalDungeon is { Run.LostLandClock.Started: true } dungeon &&
        dungeon.Run.Snapshot.Identity.InstanceId == receipt.InstanceId && dungeon.Run.Snapshot.Identity.CharacterId == Selected.Id;

    LostLandTimerReceipt LostLandTimer(LostLandRunClock clock) => new(owner.Generation, SelectionEpoch,
        formalDungeon!.Run.Snapshot.Identity.InstanceId, clock.Admission.OpaqueValue, clock.Remaining, clock.Expired);
}
