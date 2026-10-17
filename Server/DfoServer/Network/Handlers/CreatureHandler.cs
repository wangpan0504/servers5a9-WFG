using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class CreatureHandler(CreatureService service, CreatureProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.RENAME_CREATURE, RenameAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.RESPONSE_CREATURE, ResponseAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CREATURE_SEND_MESSAGE, MessageAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CREATURE_SCRIPT_MESSAGE, MessageAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.VERIFY_CREATURE_QUEST, VerifyQuestAsync, nameof(CreatureHandler), UserStateRequirement.WorldReady);
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
                service.Clock.GetUtcNow().ToUnixTimeSeconds()), 0, requireTown: false);
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
}
