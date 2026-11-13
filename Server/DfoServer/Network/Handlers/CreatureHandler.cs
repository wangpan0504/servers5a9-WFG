using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection.Inventory;

namespace DfoServer.Network.Handlers;

internal sealed class CreatureHandler(CreatureService service, CreatureProjection projection, ServerLog log,
    InventoryProjection? inventory = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.RENAME_CREATURE, RenameAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.RESPONSE_CREATURE, ResponseAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CREATURE_SEND_MESSAGE, MessageAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CREATURE_SCRIPT_MESSAGE, MessageAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.VERIFY_CREATURE_QUEST, VerifyQuestAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CREATURE_EFFECT_TIME_EXPIRE, ExpireEffectAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.USE_CREATURE_EVOLUTION_ITEM, UseEvolutionItemAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CREATURE_FILLPOINT, FillPointAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CREATURE_EXP_BOOK, UseExpBookAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
    }

    public async Task<CommandResult> UseEffectAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var position = new InventorySlot(InventoryListType.Main, unchecked((short)request.Slot));
        bool valid = request.Action == 151 && request.Reserved == 0 && request.FirstArgument == 0 && request.SecondArgument == 0 &&
            request.List == 0 && request.Slot <= short.MaxValue;
        var result = !valid || context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => service.UseEffect(character, position,
                before.Phase == SessionPhase.Town), 19, requireTown: false, effectItem: position);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, before.Selected) && context.Session.SelectionEpoch == before.SelectionEpoch &&
            context.Session.Phase == before.Phase);
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request, result.Error),
            result.Accepted ? "creature_effect_used" : "creature_effect_rejected", Guard);
        if (inventory is not null) await inventory.RefreshAsync(context, result, new HashSet<InventorySlot> { position }, refreshEquipment: false);
        log.Write("creature_effect_item", new { Connection = context.Id, request.Slot, result.Accepted, result.Error });
        return CommandResult.Handled;
    }

    Task<CommandResult> ExpireEffectAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 0) return Task.FromResult(CommandResult.Unhandled);
        if (context.Activity?.BlocksTownActions != true)
            _ = context.Session.PlanAndCommitInventory(service.ExpireEffect, 19, requireTown: false);
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> VerifyQuestAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 0) return CommandResult.Unhandled;
        var character = context.Owner.Execute(() => context.Session.Ready ? context.Session.Selected : null);
        if (character is null) return CommandResult.Handled;
        await projection.QuestsAsync(context, character, () => context.Owner.Execute(() =>
            context.Session.Ready && ReferenceEquals(context.Session.Selected, character)));
        return CommandResult.Handled;
    }

    async Task<CommandResult> RenameAsync(GameConnectionContext context, Packet packet)
    {
        var request = CreatureRequests.Rename(packet.Body);
        var selected = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(0) :
            context.Session.PlanAndCommitInventory(character => service.Rename(character, request.Card, request.Name,
                service.Clock.GetUtcNow().ToUnixTimeSeconds()), 0, requireTown: false,
                effectItem: request.Card);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, selected.Selected) &&
                context.Session.SelectionEpoch == selected.SelectionEpoch && context.Session.Phase == selected.Phase);
        await context.Reply(CmdPacketType.RENAME_CREATURE, CreaturePackets.RenameAck(request.Card, result.Accepted), "creature_rename_ack", Guard);
        await projection.ChangedAsync(context, result);
        log.Write("creature_rename", new { Connection = context.Id, result.Accepted, Revision = result.Receipt?.Character.Revision });
        return CommandResult.Handled;
    }

    async Task<CommandResult> ResponseAsync(GameConnectionContext context, Packet packet)
    {
        _ = CreatureRequests.Response(packet.Body);
        var character = context.Owner.Execute(() => context.Session.Ready && context.Session.Phase == SessionPhase.Town ? context.Session.Selected : null);
        if (character is null || service.Equipped(character, service.Clock.GetUtcNow().ToUnixTimeSeconds())?.Creature is not { Satiety: > 0 })
            return CommandResult.Handled;
        bool Guard() => context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, character) && context.Session.Ready &&
            context.Session.Phase == SessionPhase.Town);
        await projection.BroadcastAsync(context, NotiPacketType.RESPONSE_CREATURE, CreaturePackets.Actor(character.Id), "creature_response", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> MessageAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length == 0 || packet.Body[0] is not (2 or 3)) return CommandResult.Unhandled;
        var request = CreatureRequests.Message(packet.Body);
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase, context.Session.Ready));
        if (!capture.Ready || capture.Selected is not { } character || capture.Phase is not (SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement) ||
            packet.Opcode == (ushort)CmdPacketType.CREATURE_SCRIPT_MESSAGE && capture.Phase != SessionPhase.Town ||
            service.Equipped(character, service.Clock.GetUtcNow().ToUnixTimeSeconds())?.Creature is not { Satiety: > 0 })
            return CommandResult.Handled;
        bool Guard() => context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, character) && context.Session.Ready &&
            context.Session.SelectionEpoch == capture.SelectionEpoch && context.Session.Phase == capture.Phase);
        var opcode = packet.Opcode == (ushort)CmdPacketType.CREATURE_SEND_MESSAGE ? NotiPacketType.CREATURE_MESSAGE : NotiPacketType.CREATURE_SCRIPT_MESSAGE;
        await projection.BroadcastAsync(context, opcode, CreaturePackets.Message(request.Type, character.Id, request.Text), "creature_message", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> UseEvolutionItemAsync(GameConnectionContext context, Packet packet)
    {
        var sourceSlot = CreatureRequests.EvolutionOrBookSlot(packet.Body, InventoryListType.Main);
        var selected = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(123) :
            context.Session.PlanAndCommitInventory(character => service.UseEvolutionItem(character, sourceSlot,
                service.Clock.GetUtcNow().ToUnixTimeSeconds()), 123, requireTown: false, effectItem: sourceSlot);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, selected.Selected) &&
                context.Session.SelectionEpoch == selected.SelectionEpoch && context.Session.Phase == selected.Phase);
        int evolvedItemId = result.Plan?.After.Find(CreatureRules.EquippedSlot)?.Core.ItemId ?? 0;
        await context.Reply(CmdPacketType.USE_CREATURE_EVOLUTION_ITEM,
            CreaturePackets.EvolutionItemAck(result.Accepted, evolvedItemId, sourceSlot.Index, result.Error == 0 ? (ushort)123 : result.Error),
            "creature_evolution_item_ack", Guard);
        if (inventory is not null) await inventory.RefreshAsync(context, result, new HashSet<InventorySlot> { sourceSlot }, refreshEquipment: false);
        await projection.ChangedAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> FillPointAsync(GameConnectionContext context, Packet packet)
    {
        int points = CreatureRequests.FillPoint(packet.Body);
        var selected = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(123) :
            context.Session.PlanAndCommitInventory(character => service.FillPoint(character, points,
                service.Clock.GetUtcNow().ToUnixTimeSeconds()), 123, requireTown: false);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, selected.Selected) &&
                context.Session.SelectionEpoch == selected.SelectionEpoch && context.Session.Phase == selected.Phase);
        await context.Reply(CmdPacketType.CREATURE_FILLPOINT,
            CreaturePackets.Ack(result.Accepted, result.Error == 0 ? (ushort)123 : result.Error),
            "creature_fillpoint_ack", Guard);
        await projection.ChangedAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> UseExpBookAsync(GameConnectionContext context, Packet packet)
    {
        var sourceSlot = CreatureRequests.EvolutionOrBookSlot(packet.Body, InventoryListType.Pet);
        var selected = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(123) :
            context.Session.PlanAndCommitInventory(character => service.UseExpBook(character, sourceSlot,
                service.Clock.GetUtcNow().ToUnixTimeSeconds()), 123, requireTown: false, effectItem: sourceSlot);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, selected.Selected) &&
                context.Session.SelectionEpoch == selected.SelectionEpoch && context.Session.Phase == selected.Phase);
        await context.Reply(CmdPacketType.CREATURE_EXP_BOOK,
            CreaturePackets.Ack(result.Accepted, result.Error == 0 ? (ushort)123 : result.Error),
            "creature_exp_book_ack", Guard);
        if (inventory is not null) await inventory.RefreshAsync(context, result, new HashSet<InventorySlot> { sourceSlot }, refreshEquipment: false);
        await projection.ChangedAsync(context, result);
        return CommandResult.Handled;
    }
}
