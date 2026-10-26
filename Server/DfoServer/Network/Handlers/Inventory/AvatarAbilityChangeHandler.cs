using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class AvatarAbilityChangeHandler(InventoryAvatarAbilityChangeService service, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.AVATAR_OPTION_CHANGE, Handle, nameof(AvatarAbilityChangeHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        AvatarAbilityChangeRequest request;
        try { request = AvatarAbilityChangeRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("avatar_ability_change", new { Connection = context.Id, Length = packet.Body.Length, Accepted = false, Reason = "malformed" });
            await context.Reply(CmdPacketType.AVATAR_OPTION_CHANGE, AvatarAbilityChangePackets.Failure(), "avatar_ability_change_malformed");
            return CommandResult.Handled;
        }
        string? rejection = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(0) :
            context.Session.PlanAndCommitInventory(character => service.Apply(character, request, out rejection), 0);
        var receipt = result.Receipt;
        var target = receipt?.Index.Find(request.Target);
        log.Write("avatar_ability_change", new { Connection = context.Id, request.SourceSlot, request.SourceId,
            request.AvatarSlot, request.AvatarId, request.Ability, result.Accepted, Revision = receipt?.Character.Revision,
            Reason = result.Accepted ? "committed" : rejection ?? result.RejectReason ?? "state-or-snapshot" });
        if (!result.Accepted)
            await context.Reply(CmdPacketType.AVATAR_OPTION_CHANGE, AvatarAbilityChangePackets.Failure(), "avatar_ability_change_rejected");
        else
        {
            if (receipt is null || target?.Avatar is null) throw new InvalidOperationException("装扮属性转换缺少已提交实例");
            // 原生回执在窗口保留的原对象上改属性；仅投影源消费，避免NOTI14重建该对象。
            await projection.RefreshAsync(context, result, acknowledgedSlots: new HashSet<InventorySlot> { request.Target });
            await context.Reply(CmdPacketType.AVATAR_OPTION_CHANGE,
                AvatarAbilityChangePackets.Success(request.AvatarSlot, checked((byte)target.Core.Durability)), "avatar_ability_changed",
                () => context.Session.CanPublishInventory(receipt));
        }
        return CommandResult.Handled;
    }
}
