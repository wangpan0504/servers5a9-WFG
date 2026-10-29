using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EmblemCompoundHandler(InventoryEmblemCompoundService compounds,
    InventoryProjection projection, ServerLog log, GuildNpcBenefitService? guildBenefits = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.COMPOUND_EMBLEM, Handle, nameof(EmblemCompoundHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        EmblemCompoundRequest request;
        try { request = EmblemCompoundRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("emblem_compound", new
            {
                Connection = context.Id,
                Length = packet.Body.Length,
                Accepted = false,
                Reason = "malformed"
            });
            await context.Reply(CmdPacketType.COMPOUND_EMBLEM, EmblemCompoundPackets.Failure(),
                "emblem_compound_malformed");
            return CommandResult.Handled;
        }

        var original = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        EmblemCompoundReceipt? receipt = null;
        string? rejection = null;
        GuildNpcBenefitCommit? benefitCommit = null;
        var result = context.BlocksTownActions
            ? InventoryResult.Reject(22)
            : context.Session.CompoundEmblem(request, compounds, out receipt, out rejection, out benefitCommit, guildBenefits);
        rejection ??= result.Accepted ? null : "blocked-state";
        bool CanSend() => result.Receipt is { } committed
            ? context.Session.CanPublishInventory(committed)
            : context.Owner.Execute(() => context.Session.SelectionEpoch == original.SelectionEpoch &&
                context.Session.Phase == original.Phase && ReferenceEquals(context.Session.Selected, original.Selected));

        log.Write("emblem_compound", new
        {
            Connection = context.Id,
            result.Accepted,
            result.Error,
            Reason = result.Accepted ? "committed" : rejection,
            Inputs = request.Inputs.Length,
            MinimumGrade = receipt?.MinimumGrade,
            Booster = receipt?.BoosterItemId,
            Reward = receipt?.RewardItemId,
            RewardCount = receipt?.RewardCount,
            receipt?.PlatinumReroll,
            Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.COMPOUND_EMBLEM,
                EmblemCompoundPackets.Failure(result.Error == 23 ? (ushort)23 : (ushort)22),
                "emblem_compound_rejected", CanSend);
            return CommandResult.Handled;
        }
        if (receipt is null)
            throw new InvalidOperationException("徽章合成成功缺少业务回执");

        await context.Reply(CmdPacketType.COMPOUND_EMBLEM, EmblemCompoundPackets.Success(receipt),
            "emblem_compound_committed", CanSend);
        await projection.RefreshAsync(context, result);
        if (benefitCommit is not null) guildBenefits!.Publish(benefitCommit);
        return CommandResult.Handled;
    }
}
