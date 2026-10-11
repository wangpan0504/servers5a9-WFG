using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class SkillHandler(SkillLearningCatalog catalog, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.BUY_SKILL, PurchaseAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.CHANGE_SKILLSLOT, MoveSlotAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.SKILL_INIT, ResetAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.SKILL_COMMAND_CUSTOMIZING, CommandsAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.SKILL_COMMAND_ALL_DEFAULT, DefaultCommandsAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.COMBO_SKILL_INFO, ComboAsync, nameof(SkillHandler));
        commands.Register(CmdPacketType.COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET, ComboSlotsAsync, nameof(SkillHandler));
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
        await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(receipt.Character.Learning,
            id => catalog.Get(receipt.Character.Job, id).DefaultCommand), "skill_commands_info", Guard);
        return CommandResult.Handled;
    }

    private async Task<CommandResult> ComboAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParseCombo(packet.Body);
        if (parsed.Mode != 0) return CommandResult.Handled;
        var result = context.Session.CustomizeCombo(parsed.Context, parsed.Combos, catalog);
        if (!result.Success) { log.Write("skill_combo_rejected", new { context.Id, result.Failure }); return CommandResult.Handled; }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing combo receipt");
        await context.Notify(NotiPacketType.COMBO_SKILL_INFO, SkillPackets.Combos(receipt.Character.Learning, refresh: true),
            "skill_combo_committed", () => context.Session.CanPublishLearning(receipt));
        return CommandResult.Handled;
    }

    private async Task<CommandResult> ComboSlotsAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParseReset(packet.Body);
        if (parsed.Mode != 0) return CommandResult.Handled;
        var result = context.Session.ClearComboQuickSlots(parsed.Context, catalog);
        if (!result.Success) { log.Write("skill_combo_slots_rejected", new { context.Id, result.Failure }); return CommandResult.Handled; }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing combo slots receipt");
        bool Guard() => context.Session.CanPublishLearning(receipt);
        // ACK触发客户端自动编排；先下发清理后的绑定，随后508保存客户端生成的连招。
        if (result.RequiresSynchronization)
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(receipt.Character.Learning), "skill_combo_slots_info", Guard);
        await context.Reply(CmdPacketType.COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET, [1], "skill_combo_slots_committed", Guard);
        return CommandResult.Handled;
    }

    private async Task<CommandResult> ResetAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParseReset(packet.Body);
        var result = context.Session.ResetSkills(parsed.Context, parsed.Mode, catalog);
        if (!result.Success)
        {
            log.Write("skill_reset_rejected", new { Connection = context.Id, result.Failure, parsed.Context, parsed.Mode });
            return CommandResult.Handled;
        }
        var receipt = result.Receipt ?? throw new InvalidOperationException("Missing skill reset receipt");
        bool Guard() => context.Session.CanPublishLearning(receipt);
        await context.Reply(CmdPacketType.SKILL_INIT, SkillPackets.Reset(parsed.Context), "skill_reset_committed", Guard);
        await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(receipt.Character.Learning), "skill_reset_info", Guard);
        if (receipt.Character.Job == 9)
            await context.Notify(NotiPacketType.COMBO_SKILL_INFO, SkillPackets.Combos(receipt.Character.Learning, refresh: true), "skill_reset_combo", Guard);
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
        var result = context.Session.LearnSkills(request, catalog);
        if (result.Success)
        {
            // 扣点、等级、绑定及revision已提交，ACK仅发布该事实，不以发送失败重跑。
            var receipt = result.Receipt ?? throw new InvalidOperationException("Missing skill commit receipt");
            await context.Reply(CmdPacketType.BUY_SKILL, SkillPackets.Success(parsed.Context, parsed.Mode, receipt.Character.Learning, result.Changes),
                "skill_purchase_committed", () => context.Session.CanPublishLearning(receipt));
            if (result.RequiresSynchronization)
                await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(receipt.Character.Learning),
                    "skill_automatic_synchronized", () => context.Session.CanPublishLearning(receipt));
            if (receipt.Character.Job == 9)
                await context.Notify(NotiPacketType.COMBO_SKILL_INFO, SkillPackets.Combos(receipt.Character.Learning, refresh: true),
                    "skill_purchase_combo", () => context.Session.CanPublishLearning(receipt));
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
