using DfoServer.Game.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class TagEvildomHandler
{
    async Task<CommandResult> SwitchAsync(GameConnectionContext context, Packet packet)
    {
        byte ordinal = TagEvildomRunRequests.Ordinal(packet.Body);
        var receipt = context.Session.SwitchFormalTagEvildom(ordinal);
        if (receipt is null || receipt.Result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
        {
            // 本版失败只打印数字并解除pending；19沿项目通用拒绝约定，不冒称原服冷却/死亡专有码。
            await context.Reply(CmdPacketType.TAG_TOURNAMENT_CHARACTER_TAG_IN, TagEvildomRunPackets.Failure(19), "tag_evildom_switch_rejected");
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishTagEvildomLife(receipt);
        await context.Reply(CmdPacketType.TAG_TOURNAMENT_CHARACTER_TAG_IN,
            TagEvildomRunPackets.SwitchAcknowledgement(receipt.Result), "tag_evildom_switch_ack", Guard);
        if (receipt.Result.SwitchTo is not null)
            await context.Notify(NotiPacketType.TAG_TOURNAMENT_CHARACTER_TAG_IN,
                TagEvildomRunPackets.SwitchNotification(receipt.Result), "tag_evildom_switched", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeathAsync(GameConnectionContext context, Packet packet)
    {
        byte ordinal = TagEvildomRunRequests.Ordinal(packet.Body);
        var receipt = context.Session.ReportFormalTagEvildomDeath(ordinal);
        log.Write("tag_evildom_character_death", new { Connection = context.Id, Ordinal = ordinal,
            receipt?.Run, Outcome = receipt?.Result.Outcome, Defeated = receipt?.Result.Snapshot.Defeated });
        if (receipt?.Result.Outcome != DungeonRunOutcome.Accepted) return CommandResult.Handled;
        bool Guard() => context.Session.CanPublishTagEvildomLife(receipt);
        // 本服兼容顺序：非终局不发会自动败北的success，切到存活角色；原连接有界任务在12秒后发1676。
        if (receipt.Result.DefeatAcknowledgement)
            await context.Reply(CmdPacketType.DIE_TAG_TOURNAMENT_CHARACTER,
                TagEvildomRunPackets.DefeatAcknowledgement(receipt.Result), "tag_evildom_defeated", Guard);
        else if (receipt.Result.SwitchTo is not null)
            await context.Notify(NotiPacketType.TAG_TOURNAMENT_CHARACTER_TAG_IN,
                TagEvildomRunPackets.SwitchNotification(receipt.Result), "tag_evildom_death_switch", Guard);
        if (!receipt.ImperialArenaMails.IsEmpty)
            await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "imperial_arena_defeat_mail", Guard);
        return CommandResult.Handled;
    }
}
