using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Projection.Inventory;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class ConsumableUseHandler(InventoryConsumableUseService use, ServerLog log,
    TutorialCatalog? tutorials = null, ItemCatalog? items = null, InventoryProjection? inventory = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_STACKABLE, Handle, nameof(ConsumableUseHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var tutorialRequest = tutorials is null ? null : StackableUseRequests.Parse(packet.Body);
        if (tutorialRequest is not null && items is not null && inventory is not null)
        {
            var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch));
            var consumed = context.Session.UseTutorialConsumable(tutorialRequest.Position, tutorialRequest.ItemId, tutorials!, items);
            if (consumed is not null)
            {
                bool Guard() => consumed.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
                    context.Owner.Execute(() => ReferenceEquals(before.Selected, context.Session.Selected) && before.SelectionEpoch == context.Session.SelectionEpoch);
                await context.Reply(CmdPacketType.USE_STACKABLE, StackableUsePackets.Result(checked((ushort)tutorialRequest.Position.Index),
                    0, tutorialRequest.Token, tutorialRequest.ItemId, consumed.Error), "tutorial_potion_used", Guard);
                await inventory.RefreshAsync(context, consumed);
                return CommandResult.Handled;
            }
        }
        var request = ConsumableUseRequests.Read(packet.Body);
        var result = context.Session.UseConsumable(request, use, context.Activity?.BlocksTownActions == true);
        bool accepted = result.Outcome == ConsumableUseOutcome.Committed;
        log.Write("consumable_use", new { Connection = context.Id, Item = request.ItemId, Slot = request.Position.Index,
            List = request.Position.List, Accepted = accepted, result.Reason, Character = result.Guard?.Character?.Id,
            Revision = result.Guard?.Character?.Revision, result.SourceUid });
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        var guard = result.Guard ?? throw new InvalidOperationException("道具使用缺少原状态发布身份");
        bool CanSend() => context.Session.CanPublishConsumable(guard);
        await context.Reply(CmdPacketType.USE_STACKABLE, ConsumableUsePackets.Ack(request, accepted),
            accepted ? "consumable_use_committed" : "consumable_use_rejected", CanSend);
        // ACK44原生消费一次源数量；不推源14，但保留同事务更新的实际寻物任务条件。
        if (result.Inventory?.Receipt is { } receipt && receipt.PreviousQuests is { } old &&
            !old.SequenceEqual(receipt.Character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),
                "consumable_quest_conditions", CanSend);
        return CommandResult.Handled;
    }
}
