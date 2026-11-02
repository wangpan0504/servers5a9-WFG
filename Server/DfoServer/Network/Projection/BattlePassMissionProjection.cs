using System.Buffers.Binary;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class BattlePassMissionProjection
{
    public static async Task MissionsAsync(GameConnectionContext context, BattlePassMissionState state, InventoryCommitReceipt receipt)
    {
        if (!state.Active) return;
        // 公共Builder保持原显式8槽+108B接口；后18槽由权威领域状态完整编码。
        byte[] all = state.ToPayload();
        var firstEight = Enumerable.Range(0, 8).Select(index =>
        {
            int offset = 6 * index;
            return new BattlePassMissionCell(BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(offset)),
                BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(offset + 2)), all[offset + 4], all[offset + 5]);
        }).ToArray();
        await context.Notify(NotiPacketType.BATTLE_PASS_INFO_MISSION, BattlePassPackets.Missions(firstEight, all.AsSpan(48)),
            "battle_pass_mission_state", () => context.Session.CanPublishInventory(receipt));
        // 共享发布仅入队时不能记作已写出；Ready使用实际快照的期段，避免初始化时跨日漏刷。
        if (!context.Sender.IsPublicationQueued)
            context.Owner.Execute(() =>
            {
                if (!context.Session.CanPublishInventory(receipt)) return false;
                long period = state.Slots[0].Instance.Key.Period;
                if (context.BattlePassMissionDisplay is not { } previous || previous.Epoch != receipt.SelectionEpoch ||
                    previous.Period <= period)
                    context.BattlePassMissionDisplay = (receipt.SelectionEpoch, period);
                return true;
            });
    }

    public static void Post(DfoServer.Game.Session.GameSession session, ushort characterId,
        DfoServer.Game.Session.OnlineSessionDirectory online, DateTimeOffset now)
    {
        // 复用原在线连接有界发送入口；计时器已释放owner/SQL锁，不创建第二份队列或角色状态。
        if (session.ReadBattlePassMissionState(now) is not { } state || !state.State.Active ||
            state.Receipt.Character.Id != characterId ||
            online.FindConnectionByCharacterId(characterId) is not { GuardedProbe: { } post } target ||
            !ReferenceEquals(target.Session, session)) return;
        var origin = state.Receipt;
        try
        {
            while (true)
            {
                var snapshot = state;
                byte[] payload = snapshot.State.ToPayload();
                if (!post((ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION, payload,
                    "battle_pass_guild_contribution", () => session.CanPublishInventory(snapshot.Receipt))) return;
                if (!session.CanPublishInventory(origin)) return;
                var current = session.ReadBattlePassMissionState();
                if (current is null || !current.State.Active || !session.CanPublishInventory(origin)) return;
                if (current.State.Season.Revision == snapshot.State.Season.Revision &&
                    current.State.ToPayload().SequenceEqual(payload)) return;
                state = current;
            }
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation") { }
    }

    public static Task RefreshAsync(GameConnectionContext context, DateTimeOffset? now = null)
    {
        DfoServer.Game.Session.BattlePassMissionSessionState? state;
        try { state = now is { } at ? context.Session.ReadBattlePassMissionState(at) : context.Session.ReadBattlePassMissionState(); }
        catch (System.Data.Common.DbException error)
        {
            // 调用方已提交业务并可能已发送成功帧，不能让分派层重试原请求。
            throw new IOException("Committed battle pass mission publication failed", error);
        }
        return state is null ? Task.CompletedTask : MissionsAsync(context, state.State, state.Receipt);
    }
}
