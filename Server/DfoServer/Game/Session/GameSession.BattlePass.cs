using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record BattlePassSessionState(BattlePassSeasonState State, InventoryCommitReceipt Receipt);
internal sealed record BattlePassSessionReward(InventoryResult Inventory, BattlePassSeasonState? State = null);

internal sealed partial class GameSession
{
    public BattlePassSessionState? ReadBattlePass(string seasonKey) => owner.Execute<BattlePassSessionState?>(() =>
    {
        if (!Ready || Selected is null || repository is not IBattlePassStore store) return null;
        var state = store.ReadBattlePass(owner, seasonKey);
        return new(state, InventoryReceipt(Selected));
    });

    public BattlePassSessionReward ClaimBattlePassReward(BattlePassRewardService service, string seasonKey,
        byte action, byte track, uint key)
    {
        if (repository is not IBattlePassStore store) return new(InventoryResult.Reject(19));
        var capture = CaptureInventoryPlan(requireTown: false);
        if (capture is null) return new(InventoryResult.Reject(19));
        var state = owner.Execute(() => InventoryPlanIsCurrent(capture, requireTown: false)
            ? store.ReadBattlePass(owner, seasonKey) : null);
        if (state is null) return new(InventoryResult.Reject(19));
        var planned = service.Plan(capture.Character, state, action, track, key);
        if (!planned.Accepted || planned.Plan is not { } plan)
            return new(InventoryResult.Reject(planned.Error) with { RejectReason = planned.RejectReason });
        return owner.Execute(() =>
        {
            if (!InventoryPlanIsCurrent(capture, requireTown: false)) return new BattlePassSessionReward(InventoryResult.Reject(19));
            BattlePassRewardCommit committed;
            try { committed = store.CommitBattlePassReward(owner, capture.Character, plan); }
            catch (BattlePassRewardChangedException)
            {
                return new(InventoryResult.Reject(19) with { RejectReason = "battle_pass_state_changed" });
            }
            catch (InventoryQualificationExpiredException)
            {
                return new(InventoryResult.Reject(19) with { RejectReason = "qualification_expired" });
            }
            // 安装在线状态只发生在同库库存及领取事务真正提交之后。
            Selected = committed.Character;
            var inventory = new InventoryResult(true, 0, plan.Inventory) { Receipt = InventoryReceipt(capture.Character) };
            return new(inventory, committed.State);
        });
    }
}
