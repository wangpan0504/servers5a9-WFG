using DfoServer.Game.Town;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class TeleportHandler(TeleportService service, TownProjection town, InventoryProjection inventory,
    TownPresence presence, ServerLog log, GuildTeleportHandler? guildTeleport = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.TELEPORT, PotionAsync, nameof(TeleportHandler));
        commands.Register(CmdPacketType.PRECHECK_SOLO_TELEPOART, PrecheckAsync, nameof(TeleportHandler));
        commands.Register(CmdPacketType.SOLO_TELEPOART, SoloAsync, nameof(TeleportHandler));
        commands.Register(CmdPacketType.PARTY_TELEPORT, PartyAsync, nameof(TeleportHandler));
        commands.Register(CmdPacketType.PRIVILEGE_SERVICE_TELEPORT, PrivilegeAsync, nameof(TeleportHandler));
    }

    async Task<CommandResult> PotionAsync(GameConnectionContext context, Packet packet)
    {
        var request = TeleportRequests.Potion(packet.Body);
        bool guildMap = TeleportService.IsGuildMap(request);
        var result = guildMap
            ? context.Session.TeleportGuildMap(request, service, context.Session.GuildRequestIdentity(packet.RequestSerial), context.BlocksTownActions)
            : context.Session.TeleportPotion(request, service, context.BlocksTownActions);
        Log(context, packet, result);
        if (result.Replayed)
        {
            await context.Reply(CmdPacketType.TELEPORT, TeleportPackets.Potion(request), "guild_map_teleport_replayed");
            return CommandResult.Handled;
        }
        if (result.Receipt is not { } receipt)
        {
            await context.Reply(CmdPacketType.TELEPORT, guildMap ? new byte[] { 0, 0, 0 } : TeleportPackets.PotionRejected(),
                guildMap ? "guild_map_teleport_rejected" : "teleport_potion_rejected");
            return CommandResult.Handled;
        }
        bool CanSend() => context.Session.CanPublishTravel(receipt.Travel);
        await context.Reply(CmdPacketType.TELEPORT, TeleportPackets.Potion(request),
            guildMap ? "guild_map_teleport_committed" : "teleport_potion_committed", CanSend);
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
        await town.TravelAsync(context, receipt.Travel, auraTeleport: true);
        await context.Notify(NotiPacketType.AURA_TYPE_COOLTIME, TeleportPackets.Cooldown(receipt.Travel.Character,
            service.Clock.GetUtcNow().ToUnixTimeMilliseconds(), receipt.AuraKind), "teleport_aura_cooldown", CanSend);
        return CommandResult.Handled;
    }

    async Task<CommandResult> PartyAsync(GameConnectionContext context, Packet packet)
    {
        var request = TeleportRequests.Party(packet.Body);
        if (request.Source == 0 && guildTeleport is not null) return await guildTeleport.BeginAsync(context, packet);
        var result = context.Session.TeleportSingleMemberParty(request, service, context.BlocksTownActions);
        Log(context, packet, result);
        long now = service.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (result.Receipt is not { } receipt)
        {
            int remaining = TeleportPackets.RemainingSeconds(context.Session.Selected?.TeleportCooldowns.PartyAuraUntil ?? 0, now);
            await context.Reply(CmdPacketType.PARTY_TELEPORT,
                TeleportPackets.PartyRejected(request, remaining, result.Reason == "aura-cooldown" ? (ushort)22 : (ushort)19),
                "teleport_party_rejected");
            return CommandResult.Handled;
        }
        bool CanSend() => context.Session.CanPublishTravel(receipt.Travel);
        await context.Reply(CmdPacketType.PARTY_TELEPORT, [1], "teleport_party_committed", CanSend);
        await town.TravelAsync(context, receipt.Travel, auraTeleport: true, partyTeleport: true);
        await context.Notify(NotiPacketType.AURA_TYPE_COOLTIME, TeleportPackets.Cooldown(receipt.Travel.Character,
            service.Clock.GetUtcNow().ToUnixTimeMilliseconds(), receipt.AuraKind), "teleport_party_cooldown", CanSend);
        return CommandResult.Handled;
    }

    void Log(GameConnectionContext context, Packet packet, TeleportResult result) => log.Write("teleport",
        new { Connection = context.Id, packet.Opcode, result.Accepted, result.Reason,
            context.Session.Selected?.Id, context.Session.Selected?.Position, context.Session.Selected?.Revision,
            AuraItem = context.Session.Selected?.Inventory.Find(new(DfoServer.Game.Inventory.Models.InventoryListType.Equipment, 9))?.Core.ItemId,
            Body = Convert.ToHexString(packet.Body) });
}
