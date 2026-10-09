using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class QuestQuickClearHandler(QuestCatalog quests, QuestConnectionCatalog connections, QuestProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.QUEST_QUICK_CLEAR,QuickClearAsync,nameof(QuestQuickClearHandler));

    async Task<CommandResult> QuickClearAsync(GameConnectionContext context, Packet packet)
    {
        if (!QuestSkipRequests.TryQuickClear(packet.Body,out var request) || context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        var receipt = context.Session.QuickClearQuest(request.ParentKey,quests,connections);
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishQuestQuickClear(receipt);
        log.Write("quest_quick_clear",new { Connection = context.Id, request.ParentKey, receipt.Reason, receipt.Commit?.Duplicate });
        // 1714没有专用成功消费者，使用完整公共前缀；失败不借用34的业务文案含义。
        await context.Reply(CmdPacketType.QUEST_QUICK_CLEAR,receipt.Commit is null ? QuestPackets.Rejected(0) : [1],"quest_quick_clear_result",Guard);
        if (receipt.Commit is null) return CommandResult.Handled;
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),"quest_quick_clear_active",Guard);
        await projection.RefreshAsync(context,receipt.Character,Guard);
        // 完成表放在完整活动/可接投影之后，恢复答案和本角色级联标记。
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(receipt.Character),"quest_quick_clear_completed",Guard);
        return CommandResult.Handled;
    }
}
