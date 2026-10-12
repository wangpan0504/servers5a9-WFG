using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Handlers;

internal sealed class QuestQuickClearHandler(QuestCatalog quests, QuestConnectionCatalog connections, QuestProjection projection,
    ServerLog log,ItemCatalog? items = null,SkillLearningCatalog? skills = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.QUEST_QUICK_CLEAR,QuickClearAsync,nameof(QuestQuickClearHandler));
        commands.Register(CmdPacketType.CLEAR_QUEST_TICKET,PrepareManualAsync,nameof(QuestQuickClearHandler));
        commands.Register(CmdPacketType.CLEAR_BRANCH_QUEST,AnswerManualAsync,nameof(QuestQuickClearHandler));
        commands.Register(CmdPacketType.QUEST_CLEAR_BY_BUTTON,FinishManualAsync,nameof(QuestQuickClearHandler));
    }

    async Task<CommandResult> PrepareManualAsync(GameConnectionContext context,Packet packet)
    {
        if (!QuestSkipRequests.TryManual(packet.Body,out var request) || request.ItemContext != 0 && items is null ||
            context.Activity?.BlocksTownActions == true) return CommandResult.Unhandled;
        var receipt = request.ItemContext == 0 ? context.Session.PrepareManualQuestSkip(quests)
            : context.Session.PrepareQuestTicket(request.ItemContext,quests,items!,skills);
        if (receipt is null) return CommandResult.Unhandled;
        return await PublishManualPreparationAsync(context,CmdPacketType.CLEAR_QUEST_TICKET,receipt);
    }

    async Task<CommandResult> AnswerManualAsync(GameConnectionContext context,Packet packet)
    {
        if (!QuestSkipRequests.TryClearBranch(packet.Body,out var answers) || context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        var receipt = context.Session.AnswerManualQuestSkip(answers.Select(row => new QuestSkipAnswer(row.QuestionQuestId,row.Answer)).ToArray(),quests);
        if (receipt is null) return CommandResult.Unhandled;
        if (receipt.Commit is not null)
        {
            bool Guard() => context.Session.CanPublishQuestQuickClear(receipt);
            log.Write("quest_manual_complete",new { Connection = context.Id, Opcode = (ushort)CmdPacketType.CLEAR_BRANCH_QUEST,
                receipt.Reason, receipt.Commit.Duplicate, receipt.Commit.Plan.OperationId, receipt.Commit.Plan.CutoffLevel });
            await context.Reply(CmdPacketType.CLEAR_BRANCH_QUEST,[1],"quest_manual_answer_committed",Guard);
            await PublishCompletedAsync(context,receipt,Guard,"quest_manual");
            return CommandResult.Handled;
        }
        return await PublishManualPreparationAsync(context,CmdPacketType.CLEAR_BRANCH_QUEST,receipt);
    }

    async Task<CommandResult> PublishManualPreparationAsync(GameConnectionContext context,CmdPacketType opcode,QuestQuickClearReceipt receipt)
    {
        bool Guard() => context.Session.CanPublishQuestQuickClear(receipt);
        log.Write("quest_manual_prepare",new { Connection = context.Id, Opcode = (ushort)opcode, receipt.Reason,
            receipt.Prepared?.OperationId, receipt.Prepared?.CutoffLevel, receipt.Prepared?.PendingQuestions });
        // 有问题时1381是预处理应答；不能以成功ACK提前清理未答问题。
        if (receipt.Prepared is { PendingQuestions.Length: > 0 } plan)
            await context.Notify(NotiPacketType.SELECT_BRANCH_QUEST,QuestSkipPackets.Questions(plan.PendingQuestions),"quest_manual_questions",Guard);
        else
            await context.Reply(opcode,receipt.Prepared is null ? QuestPackets.Rejected(0) : [1],"quest_manual_ready",Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> FinishManualAsync(GameConnectionContext context,Packet packet)
    {
        if (!QuestSkipRequests.TryFinishManual(packet.Body) || context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        var receipt = context.Session.FinishManualQuestSkip();
        if (receipt is null) return CommandResult.Handled;
        bool Guard() => context.Session.CanPublishQuestQuickClear(receipt);
        log.Write("quest_manual_complete",new { Connection = context.Id, receipt.Reason, receipt.Commit?.Duplicate,
            receipt.Commit?.Plan.OperationId, receipt.Commit?.Plan.CutoffLevel });
        // 1457无已确认专用ACK；只有原上下文完成提交后发布真实任务投影。
        if (receipt.Commit is not null) await PublishCompletedAsync(context,receipt,Guard,"quest_manual");
        return CommandResult.Handled;
    }

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
        await PublishCompletedAsync(context,receipt,Guard,"quest_quick_clear");
        return CommandResult.Handled;
    }

    async Task PublishCompletedAsync(GameConnectionContext context,QuestQuickClearReceipt receipt,Func<bool> guard,string action)
    {
        var plan=receipt.Commit!.Plan;
        var added=plan.Inventory.ChangedSlots.Where(slot=>plan.Inventory.Before.Find(slot)?.Core.Kind==ItemKind.Quest ||
            receipt.Character.Inventory.Find(slot)?.Core.Kind==ItemKind.Quest).ToArray();
        if(added.Length>0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Character.Inventory,InventoryListType.Main,added),action+"_items",guard);
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),action+"_active",guard);
        await projection.RefreshAsync(context,receipt.Character,guard, restoreCompleted: false);
        // 完成表放在完整活动/可接投影之后，恢复答案和本角色级联标记。
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(receipt.Character),action+"_completed",guard);
    }
}
