using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record BattlePassMissionSessionState(BattlePassMissionState State, InventoryCommitReceipt Receipt);
internal sealed record BattlePassMissionSessionResult(bool Accepted, ushort Error,
    BattlePassMissionState? State = null, InventoryCommitReceipt? Receipt = null);

internal sealed partial class GameSession
{
    public BattlePassMissionSessionState? ReadBattlePassMissionState() =>
        ReadBattlePassMissionState((timeProvider ?? TimeProvider.System).GetUtcNow());

    public BattlePassMissionSessionState? ReadBattlePassMissionState(DateTimeOffset now) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || repository is not IBattlePassMissionStore store ||
            store.ReadBattlePassMissions(owner, now) is not { } state) return null;
        return new BattlePassMissionSessionState(state, InventoryReceipt(Selected));
    });

    public BattlePassMissionSessionResult ClaimBattlePassMission(DateTimeOffset now,
        byte action, ushort key, uint category, uint viewRow)
    {
        if (repository is not IBattlePassMissionStore store) return new(false, 19);
        var capture = CaptureInventoryPlan(requireTown: false);
        if (capture is null) return new(false, 19);
        return owner.Execute(() =>
        {
            if (!InventoryPlanIsCurrent(capture, requireTown: false) ||
                store.ReadBattlePassMissions(owner, now) is not { } before) return new BattlePassMissionSessionResult(false, 19);
            var result = store.CommitBattlePassMission(owner, capture.Character, before.Season.Revision,
                now, action, key, category, viewRow, timeProvider ?? TimeProvider.System);
            // 账号季经验改变不更换角色资产快照；只有真正提交后才生成发布回执。
            return result.Accepted && result.State is { } state
                ? new BattlePassMissionSessionResult(true, 0, state, InventoryReceipt(capture.Character))
                : new BattlePassMissionSessionResult(false, result.Error);
        });
    }

    public BattlePassMissionSessionResult ObserveQuickReboundSkill(BattlePassQuickReboundObservation use)
    {
        if (repository is not IBattlePassMissionStore store) return new(false, 19);
        var capture = CaptureInventoryPlan(requireTown: false);
        if (capture is null) return new(false, 19);
        return owner.Execute(() =>
        {
            if (!InventoryPlanIsCurrent(capture, requireTown: false) ||
                formalDungeon is not { } active ||
                !BattlePassQuickReboundSource.Eligible(capture.Character, active.Run.Entry, active.Run.Snapshot.Identity, use))
                return new BattlePassMissionSessionResult(false, 19);
            var result = store.CommitBattlePassQuickRebound(owner, capture.Character, active.Run.Entry,
                active.Run.Snapshot.Identity, use, timeProvider ?? TimeProvider.System);
            return result.Accepted && result.State is { } state
                ? new BattlePassMissionSessionResult(true, 0, state, InventoryReceipt(capture.Character))
                : new BattlePassMissionSessionResult(false, result.Error);
        });
    }
}
