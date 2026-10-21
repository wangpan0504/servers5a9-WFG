using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class ExperienceConsumableHandler(ExperienceConsumableService service, InventoryProjection inventory,
    CharacterLevelProjection levels, ServerLog log, LevelUpTicketHandler? levelUpTickets = null,
    PermanentGrowthHandler? permanentGrowth = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.INCREASE_STATUS, UseAsync, nameof(ExperienceConsumableHandler));

    async Task<CommandResult> UseAsync(GameConnectionContext context, Packet packet)
    {
        var slot = ExperienceConsumableRequests.Read(packet.Body);
        if (permanentGrowth is not null && await permanentGrowth.TryUseAsync(context, slot) is { } permanentResult)
            return permanentResult;
        if (levelUpTickets is not null && await levelUpTickets.TryUseTableAsync(context,slot) is { } tableResult)
            return tableResult;
        var result = context.Session.UseExperienceConsumable(slot, service, context.Activity?.BlocksTownActions == true);
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        var guard = result.Guard ?? throw new InvalidOperationException("经验物品缺少原场景身份");
        bool Guard() => context.Session.CanPublishConsumable(guard);
        log.Write("consumable_use", new { Connection = context.Id, Opcode = packet.Opcode, Slot = slot.Index,
            Item = result.Experience?.Source.Core.ItemId ?? guard.Character?.Inventory.Find(slot)?.Core.ItemId,
            Accepted = result.Outcome == ConsumableUseOutcome.Committed,
            result.Reason, Character = guard.Character?.Id, Revision = guard.Character?.Revision, result.SourceUid });
        if (result.Experience is not { } plan || result.Inventory?.Receipt is not { } receipt)
        {
            await context.Reply(CmdPacketType.INCREASE_STATUS, ExperienceConsumablePackets.Rejected(), "experience_consumable_rejected", Guard);
            return CommandResult.Handled;
        }
        var character = receipt.Character;
        await context.Reply(CmdPacketType.INCREASE_STATUS, ExperienceConsumablePackets.Used(plan), "experience_consumable_used", Guard);
        await inventory.RefreshAsync(context, result.Inventory, new HashSet<InventorySlot> { slot }, refreshEquipment: false);
        var state = character.DungeonState ?? throw new InvalidOperationException("经验物品缺少成长附属状态");
        await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level, character.Learning,
            new(checked((uint)character.Inventory.WinPoints), state.GrowthCapsuleExperience, state.ProficiencyLevel, state.ProficiencyExperience),
            CharacterExperienceDisplay.Ordinary), "experience_consumable_growth", Guard);
        if (plan.Expected.Level == character.Level && SkillPackets.BindingsChanged(plan.Expected.Learning, character.Learning))
            await SkillProjection.PublishAsync(context, character, "experience_consumable_skills", "experience_consumable_combo", Guard);
        if (!plan.Expected.ActiveMainQuests.SequenceEqual(character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(character.ActiveMainQuests), "experience_consumable_quests", Guard);
        await levels.RefreshAsync(context, character, plan.Expected.Level, "experience_consumable_level", Guard,
            previousLearning: plan.Expected.Learning);
        return CommandResult.Handled;
    }
}
