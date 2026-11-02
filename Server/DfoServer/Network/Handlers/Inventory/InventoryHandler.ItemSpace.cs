using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler
{
    async Task<CommandResult> SynchronizeItemSpaceAsync(GameConnectionContext context, Packet packet)
    {
        // 保留的精确扩展只向主动探测的助手回能力；旧服对此长度静默拒绝，不产生聊天。
        if (HelperRequests.ParseCapability(packet.Body) is { } probe)
        {
            var capability = context.Session.ReadHelperCapabilities(probe.CharacterId, probe.CapabilityVersion == 2);
            if (capability is not null)
            {
                string text = string.Join('|', "1", probe.Nonce, capability.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    capability.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    probe.CapabilityVersion == 2 ? "hell,cargo,direct-bag/1" : "hell,cargo");
                var body = ServerNoticePackets.Build(probe.CapabilityVersion == 2 ? "95_HELPER_CAPS/2" : "95_HELPER_CAPS/1", text, out bool truncated);
                if (truncated) throw new InvalidDataException("助手能力回执不得截断");
                await context.Notify(NotiPacketType.AAS_TIPS, body, "helper_capabilities",
                    () => context.Session.CanPublishHelperCapabilities(capability));
            }
            return CommandResult.Handled;
        }
        if (packet.Body.Length != 1)
        {
            log?.Write("inventory_sync_rejected", new { Connection = context.Id, Reason = "malformed" });
            return CommandResult.Handled;
        }
        byte selector = InventoryRequests.SynchronizeItemSpace(packet.Body);
        var snapshot = context.Session.ReadInventoryForSynchronization();
        if (snapshot is not null)
            await projection.SynchronizeItemSpaceAsync(context, snapshot);
        log?.Write("inventory_sync", new { Connection = context.Id, Selector = selector,
            Outcome = snapshot is null ? "not_ready" : "snapshot_queued" });
        return CommandResult.Handled;
    }
}
