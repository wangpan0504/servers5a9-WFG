using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class PermanentGrowthHandler(PermanentGrowthService service, InventoryProjection inventory,
    CharacterLevelProjection levels, ServerLog log)
{
    public async Task<CommandResult?> TryUseAsync(GameConnectionContext context, InventorySlot slot)
    {
        if (!context.Owner.Execute(() => PermanentGrowthDefinition.IsNativeItem(context.Session.Selected?.Inventory.Find(slot)?.Core.ItemId ?? 0)))
            return null;
        var result = context.Session.UsePermanentGrowth(slot, service, context.Activity?.BlocksTownActions == true);
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        var guard = result.Guard ?? throw new InvalidOperationException("永久成长缺少原场景身份");
        bool Guard() => context.Session.CanPublishConsumable(guard);
        log.Write("consumable_use", new { Connection = context.Id, Opcode = (ushort)CmdPacketType.INCREASE_STATUS,
            Slot = slot.Index, Item = result.PermanentGrowth?.Source.Core.ItemId ?? guard.Character?.Inventory.Find(slot)?.Core.ItemId,
            Accepted = result.Outcome == ConsumableUseOutcome.Committed, result.Reason,
            Character = guard.Character?.Id, Revision = guard.Character?.Revision, result.SourceUid });
        if (result.PermanentGrowth is not { } plan || result.Inventory?.Receipt is not { } receipt)
        {
            await context.Reply(CmdPacketType.INCREASE_STATUS, ExperienceConsumablePackets.Rejected(), "permanent_growth_rejected", Guard);
            return CommandResult.Handled;
        }
        var character = receipt.Character;
        await context.Reply(CmdPacketType.INCREASE_STATUS, ExperienceConsumablePackets.Permanent(plan), "permanent_growth_used", Guard);
        await inventory.RefreshAsync(context, result.Inventory, new HashSet<InventorySlot> { slot }, refreshEquipment: false);
        if (plan.Definition.Kind is PermanentGrowthKind.Sp or PermanentGrowthKind.Tp)
        {
            var state = character.DungeonState ?? throw new InvalidOperationException("技能书缺少成长附属状态");
            // ACK30 给客户端增加点数后，再用37校准两树绝对余额；不重建19，避免清除图内被动状态。
            await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level, character.Learning,
                new(checked((uint)character.Inventory.WinPoints), state.GrowthCapsuleExperience, state.ProficiencyLevel, state.ProficiencyExperience),
                CharacterExperienceDisplay.Ordinary), "permanent_growth_points", Guard);
        }
        else
            await levels.PlayerAsync(context, character, "permanent_growth_stats", Guard);
        return CommandResult.Handled;
    }
}
