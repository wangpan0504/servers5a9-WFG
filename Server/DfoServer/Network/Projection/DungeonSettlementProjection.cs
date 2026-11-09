using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DungeonSettlementProjection
{
    internal static async Task<AntonRaidBattleChange?> EnableAsync(GameConnectionContext context, ushort? reportedBossKey = null)
    {
        AntonRaidBattleChange? change = null;
        if (context.Session.RaidCombatView() is not null)
        {
            change = context.Session.CompleteCurrentRaidCombatDungeon();
            if (change is not null)
                context.Session.PublishRaidCombat();
            // 合成诊断和专用战场不借普通许可；正式团本沿原Run清图事实启用每场结算。
            if (!context.Session.IsFormalRaidDungeon) return change;
        }
        if (context.Session.EnableFormalDungeonSettlement(reportedBossKey) is not { } completion) return change;
        bool Guard() => context.Session.CanPublishDungeonSettlement(completion.Receipt);
        if (completion.Receipt.Entry.IsPractice)
        {
            bool sent = false;
            await context.Notify(NotiPacketType.EXERCISE_MODE_CLEAR,
                DungeonSettlementPackets.PracticeCleared(completion.Receipt.ClearTimeMilliseconds), "formal_practice_clear",
                () => sent = Guard());
            if (sent) context.Session.ConfirmFormalDungeonSettlementEnabled(completion.Receipt);
            return change;
        }
        // 空终点的物件或演出条件只许可已有清图的结算，不编造115需要的怪物key。
        if (completion.BossKey is { } bossKey)
            await context.Notify(NotiPacketType.BOSS_DIE_CHECK, DungeonSettlementPackets.BossAccepted(bossKey), "formal_boss_confirmed", Guard);
        bool enabled = false;
        await context.Notify(NotiPacketType.ENABLE_CLEAR_DUNGEON, [], "formal_clear_enabled", () => enabled = Guard());
        if (enabled)
        {
            context.Session.ConfirmFormalDungeonSettlementEnabled(completion.Receipt);
            if ((completion.Receipt.Entry.IsCosmo || completion.Receipt.Entry.SuppressOrdinaryResultBoard) && context.Session.PartyDungeon is null)
                await RefreshCosmoRetryAsync(context);
        }
        return change;
    }

    internal static async Task RefreshCosmoRetryAsync(GameConnectionContext context)
    {
        if (context.Session.FormalDungeonResultFocus() is not { } receipt ||
            !receipt.Entry.IsCosmo && !receipt.Entry.SuppressOrdinaryResultBoard) return;
        await context.Notify(NotiPacketType.EPLP_RECHALLENGE,
            DungeonSettlementPackets.RetryEligibility(receipt.Entry.IsCosmo
                ? context.Session.PartyDungeon is { } party
                    ? party.CanRetryCosmoDungeon() : context.Session.CanRetryCosmoDungeon(receipt)
                : context.Session.PartyDungeon is null && context.Session.CanRetryEpicRoadDungeon(receipt)),
            receipt.Entry.IsCosmo ? "formal_cosmo_retry_eligibility" : "formal_epic_road_retry_eligibility",
            () => context.Session.CanPublishDungeonSettlement(receipt));
    }
}
