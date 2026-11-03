using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

internal sealed class ImageCommunicationHandler(ImageCommunicationCatalog catalog, QuestCatalog quests, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.IMAGE_COMMUNICATION_EQUIPMENT_USE,
        UseAsync, nameof(ImageCommunicationHandler));

    private async Task<CommandResult> UseAsync(GameConnectionContext context, Packet packet)
    {
        // 027AF444..455初始化475后直接发送，正文没有echo或NPC选择字段。
        if (packet.Body.Length != 0 || context.Activity?.BlocksTownActions == true) return CommandResult.Unhandled;
        var receipt = context.Session.UseImageCommunication(catalog, quests);
        if (receipt is null) return CommandResult.Handled;
        log.Write("quest_lifecycle", new { Connection = context.Id, Action = "image_communication", receipt.Accepted, receipt.Reason,
            receipt.Commit?.Duplicate });
        bool Guard() => context.Session.CanPublishImageCommunication(receipt);
        if (!receipt.Accepted)
        {
            await context.Reply(CmdPacketType.IMAGE_COMMUNICATION_EQUIPMENT_USE,
                ImageCommunicationPackets.NeutralRejected(), "quest_communication_rejected", Guard);
            return CommandResult.Handled;
        }
        if (receipt.Commit is { ProjectionCurrent: false }) return CommandResult.CloseConnection;
        await context.Reply(CmdPacketType.IMAGE_COMMUNICATION_EQUIPMENT_USE,
            ImageCommunicationPackets.Used(receipt.Character.Inventory.Gold), "quest_communication_paid", Guard);
        if (receipt.Commit is { } commit && !commit.Plan.Expected.ActiveMainQuests.SequenceEqual(commit.Plan.ActiveAfter))
            await QuestProjection.ProgressAsync(context,commit.Plan.Expected.ActiveMainQuests,receipt.Character,Guard,
                "quest_communication_currency_condition");
        return CommandResult.Handled;
    }
}
