using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class DisjointHandler(InventoryDisjointService disjoint, InventoryProjection projection,
    ExpertJobScriptCatalog? expertJobs = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.DISJOINT_ITEM, Handle, nameof(DisjointHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = DisjointRequests.Read(packet.Body);
        var original = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase,
            RejectedSource: context.Session.CaptureDisjointRejectedSource(request)));
        DisjointReceipt? receipt = null;
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.DisjointEquipment(request, disjoint, out receipt);
        bool CanSend() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) : context.Owner.Execute(() =>
            context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase &&
            ReferenceEquals(context.Session.Selected, original.Selected));
        await context.Reply(CmdPacketType.DISJOINT_ITEM, DisjointPackets.Ack(result, receipt),
            result.Accepted ? "equipment_disjoint" : "equipment_disjoint_rejected", CanSend);
        if (!result.Accepted && original.RejectedSource is { } rejectedSource)
        {
            bool CanRestore() => context.Session.CanPublishDisjointRejectedSource(rejectedSource);
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, DisjointPackets.RejectedSource(rejectedSource.Source),
                "equipment_disjoint_rejected_source", CanRestore);
            await InventoryProjection.RestoreLocksAsync(context, rejectedSource.Character.Inventory,
                [rejectedSource.Source.Position], canSend: CanRestore);
        }
        await projection.RefreshAsync(context, result);
        if (result.Accepted && expertJobs is not null)
        {
            var current = context.Owner.Execute(() => context.Session.Selected);
            if (current is { ExpertJobType: 3 })
            {
                int experience = ExpertJobCraft.DisjointExperience(current, expertJobs, Random.Shared.Next);
                var updated = experience == current.ExpertJobExperience ? current
                    : context.Session.CommitExpertJobExperience(experience) ?? current;
                bool Guard() => context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, updated));
                await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(updated, context.Channel,
                    adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom),
                    "expert_job_equipment_disjoint_actor", Guard);
                await context.Notify(NotiPacketType.EXPERT_JOB_INFO, ExpertJobPackets.Info(updated, expertJobs),
                    "expert_job_equipment_disjoint_info", Guard);
            }
        }
        return CommandResult.Handled;
    }
}
