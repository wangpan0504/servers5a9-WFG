using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class QuestHiddenHandler(QuestCatalog quests, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.REQ_OPEN_HIDDEN_QUEST,
        OpenAsync,nameof(QuestHiddenHandler));

    async Task<CommandResult> OpenAsync(GameConnectionContext context, Packet packet)
    {
        if (!QuestRequests.TryOpenHidden(packet.Body,out var request)) return CommandResult.Unhandled;
        var receipt = context.Session.ReadHiddenQuest(request.QuestId,quests);
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishQuestHidden(receipt);
        // 请求携带的本地状态只用于审计；不能代替资源、等级、前置与实际选角资格。
        log.Write("quest_hidden_open",new { Connection = context.Id, request, receipt.Open, receipt.Reason });
        // Full03 ACK884 callback是单ret，不读成功字节或正文；真正开启由822完成。
        await context.Reply(CmdPacketType.REQ_OPEN_HIDDEN_QUEST,[],"quest_hidden_request",Guard);
        if (receipt.Open)
            await context.Notify(NotiPacketType.OPEN_HIDDEN_QUEST,QuestPackets.OpenHidden([(int)receipt.QuestId]),
                "quest_hidden_opened",Guard);
        return CommandResult.Handled;
    }
}
