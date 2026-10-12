using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Network.Handlers;

internal sealed class QuestTicketHandler(QuestCatalog quests,ItemCatalog items,QuestProjection projection,ServerLog log,
    CharacterLevelProjection? levelProjection=null,SkillLearningCatalog? skills=null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_STACKABLE_ACTION,UseAsync,nameof(QuestTicketHandler));

    async Task<CommandResult> UseAsync(GameConnectionContext context,Packet packet)
    {
        if (!QuestTicketRequests.TryUse(packet.Body,out var request) || context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        if (request.Reserved != 0 || request.FirstArgument != 0 || request.SecondArgument != 0 ||
            request.Action is not (QuestTicketDefinition.Action or QuestTicketDefinition.SpecifiedAction or
                QuestTicketDefinition.FirstAwakeningAction or QuestTicketDefinition.SecondAwakeningAction))
        {
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,QuestTicketPackets.Rejected(request),"quest_ticket_unsupported");
            return CommandResult.Handled;
        }
        var receipt = context.Session.UseQuestTicket(request.Slot,request.List,quests,items,request.Action,skills);
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishQuestQuickClear(receipt);
        log.Write("quest_ticket_use",new { Connection = context.Id,request.Slot,request.List,request.Action,receipt.Reason,
            receipt.Commit?.Duplicate,receipt.Commit?.Plan.OperationId,receipt.TicketAcknowledged });
        if (receipt.Commit is null)
        {
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,QuestTicketPackets.Rejected(request),"quest_ticket_rejected",Guard);
            return CommandResult.Handled;
        }
        // 绝对快照可安全重放；与正常职业领奖一样，先装载真实技能，再由客户端券动作重建职业。
        if (receipt.Commit.Plan.ProfessionGrowType is not null)
            await context.Notify(NotiPacketType.SKILLINFO,SkillPackets.Initialized(receipt.Character.Learning),"quest_ticket_profession_skills",Guard);
        if (!receipt.TicketAcknowledged)
        {
            // ACK515的已确认券消费者自行扣一张；不发NOTI14“获得剩余量”，同一操作不重复发送消费ACK。
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,QuestTicketPackets.Used(request),"quest_ticket_used",Guard);
            context.Session.MarkQuestTicketAcknowledged(receipt);
        }
        var added=receipt.Commit.Plan.Inventory.ChangedSlots.Where(slot=>receipt.Commit.Plan.Inventory.Before.Find(slot)?.Core.Kind==ItemKind.Quest ||
            receipt.Character.Inventory.Find(slot)?.Core.Kind==ItemKind.Quest).ToArray();
        if(added.Length>0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Character.Inventory,InventoryListType.Main,added),"quest_ticket_special_items",Guard);
        if (receipt.Character.EquipmentSlotFlags!=receipt.Commit.Plan.Expected.EquipmentSlotFlags)
            await (levelProjection ?? throw new InvalidOperationException("指定券槽位发布缺少角色投影"))
                .PlayerAsync(context,receipt.Character,"quest_ticket_slots",Guard);
        if (receipt.Commit.Plan.ProfessionGrowType is not null)
        {
            await context.Notify(NotiPacketType.USERINFO,TownPackets.Actor(receipt.Character, context.Channel),"quest_ticket_profession_actor",Guard);
            await (levelProjection ?? throw new InvalidOperationException("觉醒券发布缺少角色投影"))
                .PlayerAsync(context,receipt.Character,"quest_ticket_profession_player",Guard);
        }
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),"quest_ticket_active",Guard);
        await projection.RefreshAsync(context,receipt.Character,Guard, restoreCompleted: false);
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(receipt.Character),"quest_ticket_completed",Guard);
        return CommandResult.Handled;
    }
}
