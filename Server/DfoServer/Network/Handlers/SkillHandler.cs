using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class SkillHandler(SkillLearningCatalog catalog, ServerLog log, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.BUY_SKILL, PurchaseAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.CHANGE_ANOTHER_SKILL_TREE, ChangeTreeAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.CHANGE_SKILLSLOT, MoveSlotAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.SKILL_INIT, ResetAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.SKILL_COMMAND_CUSTOMIZING, CommandsAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.SKILL_COMMAND_ALL_DEFAULT, DefaultCommandsAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.COMBO_SKILL_INFO, ComboAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET, ComboSlotsAsync, nameof(SkillHandler));
    }

    async Task<CommandResult> ChangeTreeAsync(GameConnectionContext context, Packet packet)
    {
        byte current;
        try { current = SkillRequests.ParseTreeChange(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("skill_tree_change", new { context.Id, Accepted = false, Result = "malformed", Length = packet.Body.Length });
            await context.Reply(CmdPacketType.CHANGE_ANOTHER_SKILL_TREE, [0, 1, 0], "skill_tree_change_malformed");
            return CommandResult.Handled;
        }
        var result = context.Activity?.BlocksTownActions == true
            ? new SkillLearningResult(SkillLearningFailure.Unsupported, null, Array.Empty<DfoServer.Game.Characters.LearnedSkill>())
            : context.Session.ChangeSkillTree(current);
        log.Write("skill_tree_change", new { context.Id, Current = current, Accepted = result.Success,
            Selected = result.State?.SelectedTree, Revision = result.Receipt?.Character.Revision });
        if (!result.Success)
        {
            // 0234E170已确认00+u16及1分支；不为错误码补写未经确认的业务名称。
            await context.Reply(CmdPacketType.CHANGE_ANOTHER_SKILL_TREE, [0, 1, 0], "skill_tree_change_rejected");
            return CommandResult.Handled;
        }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing skill tree receipt");
        bool Guard() => context.Session.CanPublishLearning(receipt);
        await context.Reply(CmdPacketType.CHANGE_ANOTHER_SKILL_TREE, SkillPackets.TreeChanged(result.State!), "skill_tree_change_committed", Guard);
        await SkillProjection.PublishCombosAsync(context, receipt.Character, "skill_tree_change_combos", Guard);
        await SkillSwitchInventoryProjection.RefreshLearningAsync(context, receipt);
        await projection.RefreshSkillTreeAsync(context, receipt.Character, Guard);
        return CommandResult.Handled;
    }

    private Task<CommandResult> CommandsAsync(GameConnectionContext context, Packet packet) =>
        PublishCommandsAsync(context, context.Session.CustomizeSkills(SkillRequests.ParseCommands(packet.Body), false, catalog), CmdPacketType.SKILL_COMMAND_CUSTOMIZING);

    private Task<CommandResult> DefaultCommandsAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 0) throw new InvalidDataException("整树默认指令正文必须为空");
        return PublishCommandsAsync(context, context.Session.CustomizeSkills(Array.Empty<SkillCommandChange>(), true, catalog), CmdPacketType.SKILL_COMMAND_ALL_DEFAULT);
    }

    private async Task<CommandResult> PublishCommandsAsync(GameConnectionContext context, SkillLearningResult result, CmdPacketType opcode)
    {
        if (!result.Success) { log.Write("skill_commands_rejected", new { context.Id, result.Failure }); return CommandResult.Handled; }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing skill commands receipt");
        bool Guard() => context.Session.CanPublishLearning(receipt);
        await context.Reply(opcode, [1], "skill_commands_committed", Guard);
        await SkillProjection.PublishAsync(context, receipt.Character, "skill_commands_info", "skill_commands_combo", Guard,
            id => catalog.Get(receipt.Character.Job, id).DefaultCommand);
        await SkillSwitchInventoryProjection.RefreshLearningAsync(context, receipt);
        return CommandResult.Handled;
    }

    private async Task<CommandResult> ComboAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParseCombo(packet.Body);
        if (parsed.Mode != 0) return CommandResult.Handled;
        var result = context.Session.CustomizeCombo(parsed.Context, parsed.Combos, catalog);
        if (!result.Success) { log.Write("skill_combo_rejected", new { context.Id, result.Failure }); return CommandResult.Handled; }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing combo receipt");
        bool Guard() => context.Session.CanPublishLearning(receipt);
        // 客户端以同 opcode 的 CMD 回包判定保存完成；只推 NOTI447 会让客户端持续重发 C2S508。
        await context.Reply(CmdPacketType.COMBO_SKILL_INFO, [1], "skill_combo_committed", Guard);
        // 自动组合的快捷栏清理已在510阶段完成；这里原样保存客户端排列，不再回放技能树。
        await SkillSwitchInventoryProjection.RefreshLearningAsync(context, receipt);
        return CommandResult.Handled;
    }

    private async Task<CommandResult> ComboSlotsAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParseReset(packet.Body);
        if (parsed.Mode != 0) return CommandResult.Handled;
        var result = context.Session.BeginAutoCombo(parsed.Context, catalog);
        if (!result.Success) { log.Write("skill_combo_slots_rejected", new { context.Id, result.Failure }); return CommandResult.Handled; }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing combo slots receipt");
        bool Guard() => context.Session.CanPublishLearning(receipt);
        // 先把将要被收进连招列的上排技能移出快捷栏并回放完整技能树：客户端收到回执后才本地生成排列，
        // 必须在重建面板之前改好槽位，否则重建出的旧槽位会覆盖刚排好的六列。
        if (result.RequiresSynchronization)
            await SkillProjection.PublishSkillsAsync(context, receipt.Character, "skill_combo_auto_skills", Guard,
                id => catalog.Get(receipt.Character.Job, id).DefaultCommand);
        // 自动组合排列由客户端本地生成并显示，随后用C2S508提交；510只确认开始，不改连招存档。
        await context.Reply(CmdPacketType.COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET, [1], "skill_combo_slots_committed", Guard);
        return CommandResult.Handled;
    }

    private async Task<CommandResult> ResetAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParseReset(packet.Body);
        if(context.Session.InPvpTrainingSkillArea)
        {
            var training=context.Session.ResetPvpTrainingSkills(parsed.Context,parsed.Mode,catalog);
            if(!training.Success || training.Receipt is not { } committed) return CommandResult.Handled;
            bool GuardTraining()=>context.Session.CanPublishLearning(committed);
            await context.Reply(CmdPacketType.SKILL_INIT,SkillPackets.Reset(parsed.Context),"training_skill_reset",GuardTraining);
            await context.Notify(NotiPacketType.ARCADE_MODE_SKILL_DATA,PvpTrainingPackets.Skills(training.State!),"training_skill_reset_snapshot",GuardTraining);
            return CommandResult.Handled;
        }
        var result = context.Session.ResetSkills(parsed.Context, parsed.Mode, catalog);
        if (!result.Success)
        {
            log.Write("skill_reset_rejected", new { Connection = context.Id, result.Failure, parsed.Context, parsed.Mode });
            return CommandResult.Handled;
        }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing skill reset receipt");
        bool Guard() => context.Session.CanPublishLearning(receipt);
        await context.Reply(CmdPacketType.SKILL_INIT, SkillPackets.Reset(parsed.Context), "skill_reset_committed", Guard);
        await SkillProjection.PublishAsync(context, receipt.Character, "skill_reset_info", "skill_reset_combo", Guard);
        await SkillSwitchInventoryProjection.RefreshLearningAsync(context, receipt);
        return CommandResult.Handled;
    }

    private async Task<CommandResult> MoveSlotAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParseSlotChange(packet.Body);
        log.Write("skill_slot_change_request", new
        {
            Connection = context.Id, parsed.Context, parsed.From, parsed.To, parsed.ContextIndex, parsed.Mode,
            Length = packet.Body.Length, parsed.BodyChecksum, parsed.ChecksumMatches
        });
        if(context.Session.InPvpTrainingSkillArea)
        {
            var training=context.Session.MovePvpTrainingSkills(new(parsed.Context,parsed.From,parsed.To,parsed.ContextIndex,parsed.Mode),catalog);
            if(training.Success && training.Receipt is { } committed)
                await context.Reply(CmdPacketType.CHANGE_SKILLSLOT,SkillPackets.SlotChanged(parsed.Context,parsed.From,parsed.To),
                    "training_skill_slot_committed",()=>context.Session.CanPublishLearning(committed));
            return CommandResult.Handled;
        }
        var result = context.Session.MoveSkillSlot(new(parsed.Context, parsed.From, parsed.To, parsed.ContextIndex, parsed.Mode), catalog);
        if (!result.Success)
        {
            log.Write("skill_slot_change_rejected", new { Connection = context.Id, Reason = result.Failure.ToString() });
            return CommandResult.Handled;
        }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing skill layout commit receipt");
        await context.Reply(CmdPacketType.CHANGE_SKILLSLOT, SkillPackets.SlotChanged(parsed.Context, parsed.From, parsed.To),
            "skill_slot_change_committed", () => context.Session.CanPublishLearning(receipt));
        return CommandResult.Handled;
    }

    private async Task<CommandResult> PurchaseAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParsePurchase(packet.Body);
        // 原版坏检查值的拒绝合同尚未确认；只记录匹配结果，不把检查值当成学习资格。
        log.Write("skill_purchase_request", new
        {
            Connection = context.Id, parsed.Context, parsed.Mode, Count = parsed.Items.Count,
            Length = packet.Body.Length, parsed.BodyChecksum, parsed.ChecksumMatches
        });
        if (parsed.Items.Any(item => item.Decrease > 1))
            return CommandResult.CloseConnection;
        var request = new SkillLearningCommand(parsed.Context, parsed.Mode, Array.AsReadOnly(parsed.Items
            .Select(item => new SkillLearningChangeRequest(item.SkillId, item.Decrease != 0, item.Magnitude)).ToArray()));
        if(context.Session.InPvpTrainingSkillArea || parsed.Mode==3)
        {
            var training=context.Session.LearnPvpTrainingSkills(request,catalog);
            if(!training.Success || training.Receipt is not { } committed)
                await context.Reply(CmdPacketType.BUY_SKILL,SkillPackets.Failure(0xD9),"training_skill_rejected");
            else await context.Reply(CmdPacketType.BUY_SKILL,SkillPackets.TrainingSuccess(parsed.Mode,training.State!,training.Changes),
                "training_skill_committed",()=>context.Session.CanPublishLearning(committed));
            return CommandResult.Handled;
        }
        var result = context.Session.LearnSkills(request, catalog);
        if (result.Success)
        {
            // 扣点、等级、绑定及revision已提交，ACK仅发布该事实，不以发送失败重跑。
            var receipt = result.Receipt ?? throw new InvalidOperationException("Missing skill commit receipt");
            await context.Reply(CmdPacketType.BUY_SKILL, SkillPackets.Success(parsed.Context, parsed.Mode, result.State!, result.Changes),
                "skill_purchase_committed", () => context.Session.CanPublishLearning(receipt));
            // 普通学习/回退只回ACK29：客户端不会因加点清空连招列，
            // 额外推送NOTI447会用服务端存档覆盖客户端尚未提交的当前排列。
            if (result.RequiresSynchronization)
                await SkillProjection.PublishAsync(context, receipt.Character, "skill_automatic_synchronized", "skill_purchase_combo",
                    () => context.Session.CanPublishLearning(receipt));
            await SkillSwitchInventoryProjection.RefreshLearningAsync(context, receipt);
            return CommandResult.Handled;
        }

        ushort? failure = result.Failure switch
        {
            SkillLearningFailure.MissingSkill => 0x0015,
            SkillLearningFailure.InsufficientSp => 0x0016,
            // S4A21的TP不足码2；本版无专用文案，但公共失败尾会解除学习等待。
            SkillLearningFailure.InsufficientTp => 0x0002,
            SkillLearningFailure.Mastered => 0x0012,
            SkillLearningFailure.RequiredLevel or SkillLearningFailure.Prerequisite => 0x00D9,
            _ => null
        };
        log.Write("skill_purchase_rejected", new { Connection = context.Id, Reason = result.Failure.ToString(), parsed.Context, parsed.Mode });
        if (failure is not ushort knownCode)
            return CommandResult.Handled;
        await context.Reply(CmdPacketType.BUY_SKILL, SkillPackets.Failure(knownCode), "skill_purchase_rejected");
        return CommandResult.Handled;
    }
}
