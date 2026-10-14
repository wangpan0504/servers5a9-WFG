using DfoServer.Game.Town;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class TeleportHandler(TeleportService service, TownProjection town, InventoryProjection inventory,
    TownPresence presence, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.TELEPORT, PotionAsync, nameof(TeleportHandler));
        commands.Register(CmdPacketType.PRECHECK_SOLO_TELEPOART, PrecheckAsync, nameof(TeleportHandler));
        commands.Register(CmdPacketType.SOLO_TELEPOART, SoloAsync, nameof(TeleportHandler));
    }

    async Task<CommandResult> PotionAsync(GameConnectionContext context, Packet packet)
    {
        var request = TeleportRequests.Potion(packet.Body);
        var result = context.Session.TeleportPotion(request, service, context.BlocksTownActions);
        Log(context, packet, result);
        if (result.Receipt is not { } receipt)
        {
            await context.Reply(CmdPacketType.TELEPORT, TeleportPackets.PotionRejected(), "teleport_potion_rejected");
            return CommandResult.Handled;
        }
        bool CanSend() => context.Session.CanPublishTravel(receipt.Travel);
        await context.Reply(CmdPacketType.TELEPORT, TeleportPackets.Potion(request), "teleport_potion_committed", CanSend);
        await inventory.RefreshAsync(context, receipt.Inventory, refreshEquipment: false, suppressMainNotice: true);
        await town.TravelAsync(context, receipt.Travel);
        return CommandResult.Handled;
    }

    async Task<CommandResult> PrecheckAsync(GameConnectionContext context, Packet packet)
    {
        string name = CharacterRequests.CheckName(packet.Body);
        var target = context.Session.CanPrecheckTeleport(service, context.BlocksTownActions)
            ? presence.TeleportTarget(context.Channel, name) : null;
        if (target?.CharacterId == context.Session.Selected?.Id) target = null;
        await context.Reply(CmdPacketType.PRECHECK_SOLO_TELEPOART,
            target is null ? TeleportPackets.PrecheckRejected() : TeleportPackets.Precheck(target), "teleport_player_precheck");
        return CommandResult.Handled;
    }

    async Task<CommandResult> SoloAsync(GameConnectionContext context, Packet packet)
    {
        var request = TeleportRequests.Solo(packet.Body);
        var target = request.CharacterId > 0 ? presence.TeleportTarget(context.Channel, characterId: request.CharacterId) : null;
        var result = context.Session.TeleportSolo(request, service, target,
            request.CharacterId > 0 ? () => target is not null && presence.CurrentTeleportTarget(target) : null, context.BlocksTownActions);
        Log(context, packet, result);
        if (result.Receipt is not { } receipt)
        {
            await context.Reply(CmdPacketType.SOLO_TELEPOART, TeleportPackets.SoloRejected(), "teleport_aura_rejected");
            return CommandResult.Handled;
        }
        bool CanSend() => context.Session.CanPublishTravel(receipt.Travel);
        await context.Reply(CmdPacketType.SOLO_TELEPOART, new byte[] { 1 }, "teleport_aura_committed", CanSend);
        await context.Notify(NotiPacketType.AURA_TYPE_COOLTIME, TeleportPackets.Cooldown(receipt.Travel.Character,
            service.Clock.GetUtcNow().ToUnixTimeMilliseconds()), "teleport_aura_cooldown", CanSend);
        await town.TravelAsync(context, receipt.Travel);
        return CommandResult.Handled;
    }

    void Log(GameConnectionContext context, Packet packet, TeleportResult result) => log.Write("teleport",
        new { Connection = context.Id, packet.Opcode, result.Accepted, result.Reason,
            context.Session.Selected?.Id, context.Session.Selected?.Position, context.Session.Selected?.Revision });
}
