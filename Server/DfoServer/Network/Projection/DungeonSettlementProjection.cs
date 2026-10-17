using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DungeonSettlementProjection
{
    internal static async Task EnableAsync(GameConnectionContext context, ushort? reportedBossKey = null)
    {
        if (context.Session.EnableFormalDungeonSettlement(reportedBossKey) is not { } completion) return;
        bool Guard() => context.Session.CanPublishDungeonSettlement(completion.Receipt);
        if (completion.Receipt.Entry.IsPractice)
        {
            bool sent = false;
            await context.Notify(NotiPacketType.EXERCISE_MODE_CLEAR,
                DungeonSettlementPackets.PracticeCleared(completion.Receipt.ClearTimeMilliseconds), "formal_practice_clear",
                () => sent = Guard());
            if (sent) context.Session.ConfirmFormalDungeonSettlementEnabled(completion.Receipt);
            return;
        }
        // 空终点的物件或演出条件只许可已有清图的结算，不编造115需要的怪物key。
        if (completion.BossKey is { } bossKey)
            await context.Notify(NotiPacketType.BOSS_DIE_CHECK, DungeonSettlementPackets.BossAccepted(bossKey), "formal_boss_confirmed", Guard);
        bool enabled = false;
        await context.Notify(NotiPacketType.ENABLE_CLEAR_DUNGEON, [], "formal_clear_enabled", () => enabled = Guard());
        if (enabled) context.Session.ConfirmFormalDungeonSettlementEnabled(completion.Receipt);
    }
}
