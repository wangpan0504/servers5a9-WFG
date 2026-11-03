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
using DfoServer.Game.Titles;

namespace DfoServer.Network.Handlers;

internal sealed partial class StackableActionHandler(QuestCatalog quests,ItemCatalog items,QuestProjection projection,ServerLog log,
    CharacterLevelProjection? levelProjection=null,SkillLearningCatalog? skills=null,
    InventoryCreatureSkinService? skinService=null,InventoryProjection? inventoryProjection=null,
    CreatureChangeService? creatureChanges=null,LevelUpTicketHandler? levelUpTickets=null,
    Inventory.SkinStorageHandler? skinStorage=null,FatigueConsumableHandler? fatigueConsumables=null,
    RentalHandler? rental=null,CreatureHandler? creatures=null,InventoryPeriodExtensionService? periodExtensions=null,
    InventoryMileageService? mileage=null,InventoryEquipmentPeriodChangeService? periodChanges=null,
    AvatarPresetHandler? avatarPresets=null,GuildEconomyHandler? guildEconomy=null,
    GuildExperienceHandler? guildExperience=null, InventoryNpcFavorService? npcFavor=null, MarriageHandler? marriage=null,
    DimensionEntryTicketService? dimensionTickets=null, DungeonIncountTicketHandler? incountTickets=null,
    GuildDungeonScrollHandler? guildDungeonScroll=null) : IGameCommandModule
{
    const uint CreatureSkinAction = 224;
    const uint PeriodExtensionAction = 69;
    const uint EquipmentPeriodChangeAction = 136;
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_STACKABLE_ACTION,UseAsync,nameof(StackableActionHandler));

    async Task<CommandResult> UseAsync(GameConnectionContext context,Packet packet)
    {
        if (!StackableActionRequests.TryUse(packet.Body,out var request))
            return CommandResult.Unhandled;
        if (request.Action == ActionConsumableDefinition.StoryBookAction)
            return await UseStoryBookAsync(context, request);
        if (request.Action == 146 && guildEconomy is not null)
            return await guildEconomy.UseGiftAsync(context, request, packet.RequestSerial);
        if (request.Action == 145 && guildExperience is not null)
            return await guildExperience.UseAsync(context, request, packet.RequestSerial);
        if (request.Action == 167 && guildDungeonScroll is not null)
            return await guildDungeonScroll.UseAsync(context, request, packet.RequestSerial);
        // 这两类由原提交回执识别重放：源可能已耗尽，不能用当前空槽否认原操作。
        // 首次消费的动作身份仍由任务券/成长定义校验，原owner与回执门禁保持不变。
        if (request.Action is QuestTicketDefinition.Action or QuestTicketDefinition.AchievementAction or QuestTicketDefinition.SideAction or QuestTicketDefinition.LevelAction or QuestTicketDefinition.EpicAction or QuestTicketDefinition.AllAction or QuestTicketDefinition.GmAction or QuestTicketDefinition.SpecifiedAction or
            QuestTicketDefinition.FirstAwakeningAction or QuestTicketDefinition.SecondAwakeningAction)
            return await UseQuestTicketAsync(context, request);
        if (request.Action == LevelUpByTableDefinition.JumpingAction)
            return await (levelUpTickets ?? throw new InvalidOperationException("按表成长动作缺少升级道具处理器"))
                .UseJumpingAsync(context,request);
        // 请求动作来自客户端，但必须与本次源STK的原生动作一致；不能拿任务券触发宠物或其他业务。
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var source = request.List == (byte)InventoryListType.Main && request.Slot <= short.MaxValue
            ? captured.Selected?.Inventory.Find(new(InventoryListType.Main, (short)request.Slot)) : null;
        StackableActionIdentity? definition = null;
        try { if (source is not null) definition = items.StackableAction(source.Core.ItemId); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
        { log.Write("stackable_action_resource_rejected", new { Connection = context.Id, Item = source?.Core.ItemId, Error = error.Message }); }
        if (definition?.ClientType != request.Action)
        {
            bool SameSourceContext() => context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, captured.Selected) &&
                context.Session.SelectionEpoch == captured.SelectionEpoch && context.Session.Phase == captured.Phase);
            log.Write("stackable_action_rejected", new { Connection = context.Id, request.Slot, request.List, request.Action,
                Item = source?.Core.ItemId, Label = definition?.Label, Reason = "source-action-mismatch-or-unknown" });
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, StackableActionPackets.Rejected(request), "stackable_action_rejected", SameSourceContext);
            return CommandResult.Handled;
        }
        if (request.Action == FatigueConsumableDefinition.Action)
            return fatigueConsumables is not null ? await fatigueConsumables.UseAsync(context, request) : CommandResult.Unhandled;
        if (request.Action == DfoServer.Game.Dungeons.DungeonIncountTicketService.Action)
            return incountTickets is not null ? await incountTickets.UseAsync(context, request) : CommandResult.Unhandled;
        if (request.Action is 90 or 92 or 93)
            return marriage is not null ? await marriage.UseItemActionAsync(context, request) : CommandResult.Unhandled;
        if (request.Action == 106)
            return rental is not null ? await rental.UsePointsAsync(context, request) : CommandResult.Unhandled;
        if (request.Action == DimensionEntryTicketService.Action && dimensionTickets is not null)
            return await UseDimensionTicketAsync(context, request);
        if (request.Action == NpcFavorItemDefinition.Action)
            return await UseNpcFavorAsync(context, request);
        if (request.Action == 72)
            return await UseMileageAsync(context, request);
        if (request.Action == 151)
            return creatures is not null ? await creatures.UseEffectAsync(context, request) : CommandResult.Unhandled;
        if (request.Action == PeriodExtensionAction)
            return await ExtendPeriodAsync(context, request, items.ActionConsumable(source!.Core.ItemId));
        if (request.Action == InventorySkinStorageService.AddAction)
            return skinStorage is not null ? await skinStorage.UseAsync(context,request) : CommandResult.Unhandled;
        if (request.Action == EquipmentPeriodChangeAction)
            return periodChanges is not null ? await ChangeEquipmentAsync(context, request) : CommandResult.Unhandled;
        if (request.Action == CreatureSkinAction)
            return await UseCreatureSkinAsync(context, request);
        if (request.Action == AvatarPresetService.AddCapacityAction)
            return avatarPresets is not null ? await avatarPresets.UseCapacityAsync(context, request) : CommandResult.Unhandled;
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
                        InventoryCreatureSkinService.Rejection, effectItem: new(InventoryListType.Main, (short)request.Slot));
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
                result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
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
        if (request.Action is CreatureChangeDefinition.Action or CreatureChangeDefinition.RandomAction or CreatureGrowthItemDefinition.Action)
            return await ChangeCreatureAsync(context, request);
        log.Write("stackable_action_unsupported", new { Connection = context.Id, request.Action,
            Item = source?.Core.ItemId, Label = definition?.Label });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, StackableActionPackets.Rejected(request), "stackable_action_unsupported",
            () => context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, captured.Selected) &&
                context.Session.SelectionEpoch == captured.SelectionEpoch && context.Session.Phase == captured.Phase));
        return CommandResult.Handled;
    }

    async Task<CommandResult> UseDimensionTicketAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = request.List == 0 && request.Slot <= short.MaxValue && request.Reserved == 0 &&
            request.FirstArgument == 0 && request.SecondArgument == 0 && context.Activity?.BlocksTownActions != true
            ? context.Session.UseDimensionEntryTicket(new(InventoryListType.Main, (short)request.Slot), dimensionTickets!) : InventoryResult.Reject(19);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(captured.Selected, context.Session.Selected) && captured.SelectionEpoch == context.Session.SelectionEpoch && captured.Phase == context.Session.Phase);
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
            result.Accepted ? "dimension_ticket_committed" : "dimension_ticket_rejected", Guard);
        if (result.Accepted && inventoryProjection is not null)
            await inventoryProjection.RefreshAsync(context, result, new HashSet<InventorySlot> { new(InventoryListType.Main, (short)request.Slot) });
        if (result.Accepted) await DimensionEntranceProjection.RefreshAsync(context, Guard, entranceOnly: false);
        log.Write("dimension_entry_ticket", new { Connection = context.Id, result.Accepted, result.Error, Revision = result.Receipt?.Character.Revision });
        return CommandResult.Handled;
    }

    async Task<CommandResult> UseNpcFavorAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool valid = npcFavor is not null && request.List == 0 && request.Slot <= short.MaxValue && request.Reserved == 0 &&
            request.FirstArgument is > 0 and <= int.MaxValue && request.SecondArgument == 0 && context.Activity?.BlocksTownActions != true;
        var result = valid ? context.Session.PlanAndCommitInventory(character => npcFavor!.Plan(character,
            new(InventoryListType.Main, (short)request.Slot), (int)request.FirstArgument,
            captured.Phase == Game.Session.SessionPhase.Town), 19, requireTown: false,
            requiredPhase: captured.Phase, usedItem: new(InventoryListType.Main, (short)request.Slot)) : InventoryResult.Reject(19);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, captured.Selected) && context.Session.SelectionEpoch == captured.SelectionEpoch &&
            context.Session.Phase == captured.Phase);
        log.Write("npc_favor_item", new { Connection = context.Id, request.Slot, Npc = request.FirstArgument,
            result.Accepted, result.Error, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
            result.Accepted ? "npc_favor_committed" : "npc_favor_rejected", Guard);
        // 015AADBD提交NPC编号，ACK515已自行扣一封；仅回放绝对好感度，不再用NOTI14重复扣源。
        if (result.Receipt is { } committed)
            await context.Notify(NotiPacketType.NPC_FAVOR_POINT_INFO, NpcFavorPackets.Snapshot(committed.Character.Inventory.NpcFavor, npcFavor!.Clock.GetUtcNow()),
                "npc_favor_updated", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> UseMileageAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool valid = request.List == 0 && request.Slot <= short.MaxValue && request.Reserved == 0 &&
            request.FirstArgument == 0 && request.SecondArgument == 0 && mileage is not null && inventoryProjection is not null;
        var source = new InventorySlot(InventoryListType.Main, unchecked((short)request.Slot));
        InventoryResult result;
        try
        {
            result = !valid || context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
                context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, before.Selected)
                    ? mileage!.Plan(character, source, before.Phase == Game.Session.SessionPhase.Town) : InventoryResult.Reject(19),
                    19, requireTown: false, requiredPhase: before.Phase, usedItem: source);
        }
        catch (Exception error) when (error is DbException or InvalidDataException or KeyNotFoundException)
        {
            log.Write("mileage_item_commit_failed", new { Connection = context.Id, request.Slot, Error = error.Message });
            result = InventoryResult.Reject(19);
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, before.Selected) && context.Session.SelectionEpoch == before.SelectionEpoch && context.Session.Phase == before.Phase);
        log.Write("mileage_item", new { Connection = context.Id, request.Slot, result.Accepted, result.Error,
            Before = before.Selected?.Inventory.AccountWallet.Mileage, After = result.Receipt?.Character.Inventory.AccountWallet.Mileage });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
            result.Accepted ? "mileage_item_committed" : "mileage_item_rejected", Guard);
        // 动作72的默认成功消费者自行扣一件源；随后只发布已提交的537积分通知。
        if (inventoryProjection is not null)
            await inventoryProjection.RefreshAsync(context, result, new HashSet<InventorySlot> { source }, refreshEquipment: false);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ExtendPeriodAsync(GameConnectionContext context, StackableActionUseRequest request,
        ActionConsumableDefinition? definition)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool valid = request.List == 0 && request.Slot <= short.MaxValue && request.Reserved == 0 &&
            request.FirstArgument is 0 or 3 && request.SecondArgument <= short.MaxValue &&
            periodExtensions is not null && inventoryProjection is not null;
        var source = new InventorySlot(InventoryListType.Main, unchecked((short)request.Slot));
        var target = new InventorySlot((InventoryListType)request.FirstArgument, unchecked((short)request.SecondArgument));
        var result = !valid || context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, before.Selected)
                ? periodExtensions!.Plan(character, source, target, definition, before.Phase == Game.Session.SessionPhase.Town)
                : InventoryResult.Reject(19), 19, requireTown: false, requiredPhase: before.Phase, usedItem: source);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, before.Selected) && context.Session.SelectionEpoch == before.SelectionEpoch &&
            context.Session.Phase == before.Phase);
        log.Write("item_period_extension", new { Connection = context.Id, Item = definition?.Item.Id, Source = source.Index,
            TargetList = target.List, Target = target.Index, result.Accepted, result.Error,
            Before = before.Selected?.Inventory.Find(target)?.ExpireTime,
            After = result.Receipt?.Character.Inventory.Find(target)?.ExpireTime, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
            result.Accepted ? "item_period_extended" : "item_period_extension_rejected", Guard);
        // 原生69成功ACK自行扣一张源券；随后只更新目标完整核心，不重复扣源或播报新获得。
        if (inventoryProjection is not null)
            await inventoryProjection.RefreshAsync(context, result, new HashSet<InventorySlot> { source }, suppressMainNotice: true);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ChangeEquipmentAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool valid = request.List == 0 && request.Slot <= short.MaxValue && request.Reserved == 0 &&
            request.FirstArgument <= byte.MaxValue && request.SecondArgument <= short.MaxValue &&
            periodChanges is not null && inventoryProjection is not null;
        var source = new InventorySlot(InventoryListType.Main, unchecked((short)request.Slot));
        var target = new InventorySlot((InventoryListType)request.FirstArgument, unchecked((short)request.SecondArgument));
        var result = !valid || context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => ReferenceEquals(character, before.Selected)
                ? periodChanges!.Plan(character, source, target, before.Phase == Game.Session.SessionPhase.Town)
                : InventoryResult.Reject(19), 19, requireTown: false, requiredPhase: before.Phase, effectItem: source);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, before.Selected) && context.Session.SelectionEpoch == before.SelectionEpoch &&
            context.Session.Phase == before.Phase);
        log.Write("equipment_period_change", new { Connection = context.Id, Source = source.Index,
            TargetList = target.List, Target = target.Index, result.Accepted, result.Error,
            Before = before.Selected?.Inventory.Find(target)?.Core.ItemId,
            After = result.Receipt?.Character.Inventory.Find(target)?.Core.ItemId,
            Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
            result.Accepted ? "equipment_period_changed" : "equipment_period_change_rejected", Guard);
        // 成功ACK由客户端自行扣一张源；随后只发布目标新ID与源槽剩余量，不重复扣源。
        if (inventoryProjection is not null)
            await inventoryProjection.RefreshAsync(context, result, new HashSet<InventorySlot> { source }, suppressMainNotice: true);
        return CommandResult.Handled;
    }

    async Task<CommandResult> UseQuestTicketAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        if (context.Activity?.BlocksTownActions == true) return CommandResult.Unhandled;
        if (request.Reserved != 0 || request.FirstArgument != 0 || request.SecondArgument != 0 ||
            request.Action is not (QuestTicketDefinition.Action or QuestTicketDefinition.AchievementAction or QuestTicketDefinition.SideAction or QuestTicketDefinition.LevelAction or QuestTicketDefinition.EpicAction or QuestTicketDefinition.AllAction or QuestTicketDefinition.GmAction or QuestTicketDefinition.SpecifiedAction or
                QuestTicketDefinition.FirstAwakeningAction or QuestTicketDefinition.SecondAwakeningAction))
        {
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,StackableActionPackets.Rejected(request),"quest_ticket_unsupported");
            return CommandResult.Handled;
        }
        var receipt = context.Session.UseQuestTicket(request.Slot,request.List,quests,items,request.Action,skills);
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishQuestQuickClear(receipt);
        log.Write("quest_ticket_use",new { Connection = context.Id,request.Slot,request.List,request.Action,receipt.Reason,
            receipt.Commit?.Duplicate,receipt.Commit?.Plan.OperationId,receipt.TicketAcknowledged,
            GeneralTitles=receipt.Commit?.Plan.GeneralTitleQuestIds.Length });
        if (receipt.Commit is null)
        {
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,StackableActionPackets.Rejected(request),"quest_ticket_rejected",Guard);
            return CommandResult.Handled;
        }
        // 绝对快照可安全重放；与正常职业领奖一样，先装载真实技能，再由客户端券动作重建职业。
        if (receipt.Commit.Plan.ProfessionGrowType is not null)
            await SkillProjection.PublishAsync(context,receipt.Character,"quest_ticket_profession_skills","quest_ticket_profession_combo",Guard);
        if (!receipt.TicketAcknowledged)
        {
            // ACK515的已确认券消费者自行扣一张；不发NOTI14“获得剩余量”，同一操作不重复发送消费ACK。
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,StackableActionPackets.Used(request),"quest_ticket_used",Guard);
            context.Session.MarkQuestTicketAcknowledged(receipt);
        }
        if (receipt.Commit.Plan.ProfessionGrowType is null &&
            !ReferenceEquals(receipt.Commit.Plan.Expected.Learning,receipt.Commit.Plan.TargetLearning))
            await SkillProjection.PublishSkillsAsync(context,receipt.Character,"quest_ticket_permanent_skills",Guard);
        var added=receipt.Commit.Plan.Inventory.ChangedSlots.Where(slot=>receipt.Commit.Plan.Inventory.Before.Find(slot)?.Core.Kind==ItemKind.Quest ||
            receipt.Character.Inventory.Find(slot)?.Core.Kind==ItemKind.Quest).ToArray();
        if (receipt.Commit.Plan.HasTitleRewards)
        {
            var source=receipt.Commit.Plan.Ticket!.Item;
            var changed=receipt.Commit.Plan.Inventory.ChangedSlots.Where(slot=>!TitleBookService.IsBook(slot.List) &&
                (slot!=source.Position || receipt.Character.Inventory.Find(slot) is { } replacement && replacement.Core.ItemId!=source.Core.ItemId));
            foreach (var group in changed.GroupBy(slot=>slot.List))
                await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Character.Inventory,group.Key,group.ToArray()),
                    "quest_ticket_title_rewards",Guard);
            if (receipt.Commit.Plan.Effects?.Titles is { } titles)
            {
                // 称号簿走完整列表，不把专用容器伪装成NOTI14背包增量，也不重放逐项获得弹窗。
                await context.Notify(NotiPacketType.ACHIEVEMENT_LIST,TitlePackets.Progress(receipt.Character),"quest_ticket_title_progress",Guard);
                await TitleProjection.BooksAsync(context,receipt.Character.Inventory,Guard);
                if (!titles.Postals.IsEmpty)
                    await context.Notify(NotiPacketType.MAILBOX_ALARM,MailboxPackets.Alarm(),"quest_ticket_title_mail",Guard);
            }
        }
        else if(added.Length>0)
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
                TownPackets.Actor(receipt.Character, context.Session.ReadPvpGrade(receipt.Character), context.Channel, adventureName: context.Session.ReadAdventureName(),
                    premiumPcRoom: context.Session.PremiumPcRoom),
                "quest_ticket_profession_actor",Guard);
            await (levelProjection ?? throw new InvalidOperationException("觉醒券发布缺少角色投影"))
                .PlayerAsync(context,receipt.Character,"quest_ticket_profession_player",Guard);
        }
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),"quest_ticket_active",Guard);
        await projection.RefreshAsync(context,receipt.Character,Guard, restoreCompleted: false);
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(receipt.Character),"quest_ticket_completed",Guard);
        await projection.StoryBooksAsync(context, receipt.Character, Guard);
        return CommandResult.Handled;
    }

    internal static Func<bool> CaptureAuraRejectionGuard(GameConnectionContext context)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected,context.Session.SelectionEpoch,context.Session.Phase));
        return () => context.Owner.Execute(() => ReferenceEquals(captured.Selected,context.Session.Selected) &&
            captured.SelectionEpoch == context.Session.SelectionEpoch && captured.Phase == context.Session.Phase);
    }

    async Task<CommandResult> UseCreatureSkinAsync(GameConnectionContext context, StackableActionUseRequest request)
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
                    InventoryCreatureSkinService.Rejection, effectItem: new(InventoryListType.Main, (short)request.Slot));
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
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, StackableActionPackets.Rejected(request),
                "creature_skin_ticket_rejected", Guard);
            return CommandResult.Handled;
        }
        // 成功ACK515由客户端自行扣一张；随后只恢复权威Player开栏位，不再通知同一源槽。
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, StackableActionPackets.Used(request),
            "creature_skin_ticket_used", Guard);
        await inventoryProjection.RefreshAsync(context, result, acknowledgedSlots: new HashSet<InventorySlot>
        { new(InventoryListType.Main, (short)request.Slot) });
        return CommandResult.Handled;
    }

    async Task<CommandResult> ChangeCreatureAsync(GameConnectionContext context, StackableActionUseRequest request)
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
                : creatureChanges.Plan(character, new(InventoryListType.Main, (short)request.Slot), new(InventoryListType.Pet, (short)targetIndex),
                    capture.Phase == Game.Session.SessionPhase.Town),
                0, requireTown: false, requiredPhase: capture.Phase,
                usedItem: new(InventoryListType.Main, (short)request.Slot));
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, capture.Selected) &&
                context.Session.SelectionEpoch == capture.SelectionEpoch && context.Session.Phase == capture.Phase);
        log.Write("creature_change", new { Connection = context.Id, request.Slot, request.List,
            request.Action, TargetSlot = targetIndex, Choice = grouped ? request.SecondArgument : 0, result.Accepted,
            Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request), "creature_change_ack", Guard);
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
