using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class BattlePassProjection(string seasonKey, BattlePassCatalog catalog, TimeProvider? clock = null)
{
    public async Task ReadyAsync(GameConnectionContext context)
    {
        var selection = context.Owner.Execute(() => (CharacterId: context.Session.Selected?.Id,
            context.Session.SelectionEpoch, context.Session.Phase, context.Session.Ready));
        if (selection.CharacterId is null) return;
        bool SameSelectionLocked() => context.Session.Selected?.Id == selection.CharacterId &&
            context.Session.SelectionEpoch == selection.SelectionEpoch && context.Session.Phase == selection.Phase &&
            context.Session.Ready == selection.Ready;
        bool SameSelection() => context.Owner.Execute(SameSelectionLocked);

        // 快照被同角色的其他提交替换时重读整组，避免发送不同季状态的混合页。
        while (true)
        {
            context.Stop.ThrowIfCancellationRequested();
            var snapshot = context.Owner.Execute(() =>
            {
                if (!SameSelectionLocked())
                    return (Current: false, Mission: (BattlePassMissionSessionState?)null, Reward: (BattlePassSessionState?)null);
                var missions = context.Session.ReadBattlePassMissionState((clock ?? TimeProvider.System).GetUtcNow());
                return (Current: true, Mission: missions, Reward: missions is null ? context.Session.ReadBattlePass(seasonKey) : null);
            });
            if (!snapshot.Current) return;
            if (snapshot.Mission is { } mission)
            {
                await MissionStateAsync(context, mission.State, mission.Receipt);
                if (context.Session.CanPublishInventory(mission.Receipt)) return;
            }
            else
            {
                if (snapshot.Reward is not { } state) return;
                await context.Notify(NotiPacketType.BATTLE_PASS_INFO_PASS, PassInfo(catalog, state.State),
                    "battle_pass_progress_state", () => context.Session.CanPublishInventory(state.Receipt));
                await ClaimsAsync(context, state.State, state.Receipt);
                if (context.Session.CanPublishInventory(state.Receipt)) return;
            }
            if (!SameSelection()) return;
            await Task.Yield();
        }
    }

    public async Task MissionStateAsync(GameConnectionContext context, BattlePassMissionState state, InventoryCommitReceipt receipt)
    {
        while (true)
        {
            context.Stop.ThrowIfCancellationRequested();
            await context.Notify(NotiPacketType.BATTLE_PASS_INFO_PASS, PassInfo(catalog, state.Season),
                "battle_pass_progress_state", () => context.Session.CanPublishInventory(receipt));
            await BattlePassMissionProjection.MissionsAsync(context, state, receipt);
            await ClaimsAsync(context, state.Season, receipt);
            var current = context.Owner.Execute(() => context.Session.CanPublishInventory(receipt)
                ? context.Session.ReadBattlePassMissionState((clock ?? TimeProvider.System).GetUtcNow()) : null);
            if (current is null) return;
            if (current.State.Season.Revision == state.Season.Revision && current.State.Active == state.Active &&
                (!state.Active || current.State.ToPayload().SequenceEqual(state.ToPayload()))) return;
            state = current.State;
            receipt = current.Receipt;
            await Task.Yield();
        }
    }

    internal static byte[] PassInfo(BattlePassCatalog catalog, BattlePassSeasonState state)
    {
        state.Validate();
        var progress = BattlePassProgress.FromTotalExperience(catalog, state.TotalExperience);
        // 000000是本服显式保留策略，不冒充原服务器值。
        return BattlePassPackets.PassInfo(state.PassType, [0, 0, 0], progress.Level, progress.Experience);
    }

    public static Task ClaimsAsync(GameConnectionContext context, BattlePassSeasonState state, InventoryCommitReceipt receipt) =>
        context.Notify(NotiPacketType.BATTLE_PASS_INFO_REWARD, state.ToClaimBytes(), "battle_pass_reward_state",
            () => context.Session.CanPublishInventory(receipt));
}
