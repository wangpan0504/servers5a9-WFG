using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class QuestConditionHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.SET_QUEST_TRIGGER, TriggerAsync, nameof(QuestConditionHandler));

    private async Task<CommandResult> TriggerAsync(GameConnectionContext context, Packet packet)
    {
        if (!QuestRequests.TrySingleClearTrigger(packet.Body, out var request) ||
            request is not { QuestId: 3145, Value: 0, Reserved: 0 } || context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        // 客户端报告仅请求投影，不能生成清图资格、修改条件或触发任务奖励。
        var receipt = context.Session.ReadClearedQuest(request.QuestId);
        log.Write("quest_clear_projection", new { Connection = context.Id, request.QuestId,
            Accepted = receipt is not null, receipt?.Clear.AcceptanceId, receipt?.Clear.InstanceId });
        if (receipt is null)
            return CommandResult.Unhandled;
        await context.Reply(CmdPacketType.SET_QUEST_TRIGGER, QuestPackets.ClearTrigger(request.QuestId, 0),
            "quest_clear_committed", () => context.Session.CanPublishQuestCondition(receipt));
        return CommandResult.Handled;
    }
}
