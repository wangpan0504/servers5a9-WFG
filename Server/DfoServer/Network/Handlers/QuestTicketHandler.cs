using System.Data.Common;
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
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Game.Creatures;
using DfoServer.GameWorld.Creatures;

namespace DfoServer.Network.Handlers;

internal sealed class QuestTicketHandler(QuestCatalog quests,ItemCatalog items,QuestProjection projection,ServerLog log,
    CharacterLevelProjection? levelProjection=null,SkillLearningCatalog? skills=null,
    InventoryCreatureSkinService? skinService=null,InventoryProjection? inventoryProjection=null,
    CreatureChangeService? creatureChanges=null,LevelUpTicketHandler? levelUpTickets=null,
    Inventory.SkinStorageHandler? skinStorage=null) : IGameCommandModule
{
    const uint CreatureSkinAction = 224;
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_STACKABLE_ACTION,UseAsync,nameof(QuestTicketHandler));

    async Task<CommandResult> UseAsync(GameConnectionContext context,Packet packet)
    {
        if (!QuestTicketRequests.TryUse(packet.Body,out var request))
            return CommandResult.Unhandled;
        if (request.Action == InventorySkinStorageService.AddAction)
            return skinStorage is not null ? await skinStorage.UseAsync(context,request) : CommandResult.Unhandled;
        if (request.Action == LevelUpByTableDefinition.JumpingAction)
            return await (levelUpTickets ?? throw new InvalidOperationException("按表成长动作缺少升级道具处理器"))
                .UseJumpingAsync(context,request);
        if (request.Action == CreatureSkinAction)
            return await UseCreatureSkinAsync(context, request);
        if (request.Action == InventoryCreatureSkinService.AuraAction)
        {
            var sameSelection = CaptureAuraRejectionGuard(context);
            bool validWire = request.Reserved == 0 && request.FirstArgument == 0 && request.SecondArgument == 0 &&
                request.List == (byte)InventoryListType.Main && request.Slot <= short.MaxValue;
            InventoryResult result;
            try
            {
                result = !validWire || context.Activity?.BlocksTownActions == true || skinService is null || inventoryProjection is null
                    ? InventoryResult.Reject(InventoryCreatureSkinService.Rejection)
                    : context.Session.PlanAndCommitInventory(character => skinService.PlanAuraAction(character,
                        request.Slot,request.List,request.Action,skinService.Clock.GetUtcNow().ToUnixTimeSeconds()),
                        InventoryCreatureSkinService.Rejection);
            }
            catch (DbException error)
            {
                log.Write("aura_skin_action_commit_failed",new { Connection=context.Id,request.Slot,request.List,
                    request.Action,Error=error.Message });
                result = InventoryResult.Reject(InventoryCreatureSkinService.Rejection);
            }
            bool CanPublish() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : sameSelection();
            log.Write("aura_skin_action",new { Connection=context.Id,request.Slot,request.List,request.Action,
                result.Accepted,result.Error,Revision=result.Receipt?.Character.Revision });
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
                result.Accepted ? QuestTicketPackets.Used(request) : QuestTicketPackets.Rejected(request),
                result.Accepted ? "aura_skin_action_committed" : "aura_skin_action_rejected",
                CanPublish);
            if (result.Accepted)
            {
                // 515成功消费者自行扣一张；抑制源14，随后只用已提交Player同步光环bit3。
                await inventoryProjection!.RefreshAsync(context,result,
                    new HashSet<InventorySlot> { new(InventoryListType.Main,(short)request.Slot) });
            }
            return CommandResult.Handled;
        }
        if (request.Action is CreatureChangeDefinition.Action or CreatureGrowthItemDefinition.Action)
            return await ChangeCreatureAsync(context, request);
        if (context.Activity?.BlocksTownActions == true) return CommandResult.Unhandled;
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
            await SkillProjection.PublishAsync(context,receipt.Character,"quest_ticket_profession_skills","quest_ticket_profession_combo",Guard);
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
        await SkillSwitchInventoryProjection.RefreshStateAsync(
            context,
            receipt.Commit.Plan.Expected.SkillSwitchInventory,
            receipt.Commit.Plan.Expected.Inventory,
            receipt.Character,
            Guard,
            "quest_ticket_skill_switch_inventory");
        if (receipt.Character.EquipmentSlotFlags!=receipt.Commit.Plan.Expected.EquipmentSlotFlags)
            await (levelProjection ?? throw new InvalidOperationException("指定券槽位发布缺少角色投影"))
                .PlayerAsync(context,receipt.Character,"quest_ticket_slots",Guard);
        if (receipt.Commit.Plan.ProfessionGrowType is not null)
        {
            await context.Notify(NotiPacketType.USERINFO,
                TownPackets.Actor(receipt.Character, context.Channel, adventureName: context.Session.ReadAdventureName(),
                    premiumPcRoom: context.Session.PremiumPcRoom),
                "quest_ticket_profession_actor",Guard);
            await (levelProjection ?? throw new InvalidOperationException("觉醒券发布缺少角色投影"))
                .PlayerAsync(context,receipt.Character,"quest_ticket_profession_player",Guard);
        }
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),"quest_ticket_active",Guard);
        await projection.RefreshAsync(context,receipt.Character,Guard, restoreCompleted: false);
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(receipt.Character),"quest_ticket_completed",Guard);
        return CommandResult.Handled;
    }

    internal static Func<bool> CaptureAuraRejectionGuard(GameConnectionContext context)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected,context.Session.SelectionEpoch,context.Session.Phase));
        return () => context.Owner.Execute(() => ReferenceEquals(captured.Selected,context.Session.Selected) &&
            captured.SelectionEpoch == context.Session.SelectionEpoch && captured.Phase == context.Session.Phase);
    }

    async Task<CommandResult> UseCreatureSkinAsync(GameConnectionContext context, QuestTicketUseRequest request)
    {
        if (skinService is null || inventoryProjection is null)
            throw new InvalidOperationException("宠物幻化右键缺少库存服务或投影");
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(captured.Selected, context.Session.Selected) &&
            captured.SelectionEpoch == context.Session.SelectionEpoch && captured.Phase == context.Session.Phase);
        bool validWire = request.Reserved == 0 && request.FirstArgument == 0 && request.SecondArgument == 0 &&
            request.List == (byte)InventoryListType.Main && request.Slot <= short.MaxValue;
        InventoryResult result;
        try
        {
            result = !validWire || context.BlocksTownActions ? InventoryResult.Reject(InventoryCreatureSkinService.Rejection) :
            context.Session.PlanAndCommitInventory(character => skinService.PlanUnlock(character, (short)request.Slot,
                InventoryCreatureSkinService.TargetSlot, skinService.Clock.GetUtcNow().ToUnixTimeSeconds()),
                    InventoryCreatureSkinService.Rejection);
        }
        catch (DbException error)
        {
            // 库存事务已回滚；用515拒绝结束等待，客户端不会对失败动作扣券。
            log.Write("creature_skin_ticket_rollback", new { Connection = context.Id, Error = error.Message });
            result = InventoryResult.Reject(InventoryCreatureSkinService.Rejection);
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : SameSelection();
        log.Write("creature_skin_ticket_use", new { Connection = context.Id, request.Slot, request.List, request.Action,
            result.Accepted, result.Error, Revision = result.Receipt?.Character.Revision });
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, QuestTicketPackets.Rejected(request),
                "creature_skin_ticket_rejected", Guard);
            return CommandResult.Handled;
        }
        // 成功ACK515由客户端自行扣一张；随后只恢复权威Player开栏位，不再通知同一源槽。
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, QuestTicketPackets.Used(request),
            "creature_skin_ticket_used", Guard);
        await inventoryProjection.RefreshAsync(context, result, acknowledgedSlots: new HashSet<InventorySlot>
        { new(InventoryListType.Main, (short)request.Slot) });
        return CommandResult.Handled;
    }

    async Task<CommandResult> ChangeCreatureAsync(GameConnectionContext context, QuestTicketUseRequest request)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool grouped = request.Action == CreatureGrowthItemDefinition.Action;
        uint targetIndex = grouped ? request.FirstArgument : request.SecondArgument;
        var result = context.Activity?.BlocksTownActions == true || request.Reserved != 0 || request.Slot > short.MaxValue || request.List != 0 ||
            !grouped && request.FirstArgument != (uint)InventoryListType.Pet || targetIndex > short.MaxValue ||
            creatureChanges is null || inventoryProjection is null ? InventoryResult.Reject(0) :
            context.Session.PlanAndCommitInventory(character => grouped
                ? creatureChanges.PlanGroup(character, new(InventoryListType.Main, (short)request.Slot),
                    new(InventoryListType.Pet, (short)targetIndex), request.SecondArgument, capture.Phase == Game.Session.SessionPhase.Town)
                : creatureChanges.Plan(character, new(InventoryListType.Main, (short)request.Slot), new(InventoryListType.Pet, (short)targetIndex)),
                0, requireTown: !grouped, requiredPhase: capture.Phase);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, capture.Selected) &&
                context.Session.SelectionEpoch == capture.SelectionEpoch && context.Session.Phase == capture.Phase);
        log.Write("creature_change", new { Connection = context.Id, request.Slot, request.List,
            request.Action, TargetSlot = targetIndex, Choice = grouped ? request.SecondArgument : 0, result.Accepted,
            Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? QuestTicketPackets.Used(request) : QuestTicketPackets.Rejected(request), "creature_change_ack", Guard);
        if (result.Accepted)
        {
            var source = new InventorySlot(InventoryListType.Main, (short)request.Slot);
            // action64的ACK消费一本；转换组按表消费，由库存差量同步实际数量。
            var acknowledged = !grouped && result.Plan!.BeforeIndex.Find(source)!.Core.Count -
                (result.Receipt!.Index.Find(source)?.Core.Count ?? 0) == 1;
            await inventoryProjection!.RefreshAsync(context, result, acknowledged ? new HashSet<InventorySlot> { source } : null);
        }
        return CommandResult.Handled;
    }
}
