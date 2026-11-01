using DfoServer.Game.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    static Task PublishFiendwarKeyMessageAsync(GameConnectionContext context, FiendwarKeyLeaseMessage message) =>
        context.Sender.Send(0, FiendwarKeyLeasePackets.Opcode, FiendwarKeyLeasePackets.Build(message), "fiendwar_key_lease",
            () => context.Owner.IsCurrent && context.Session.CanPublishFiendwarKeyMessage(message));

    static async Task PublishFiendwarKeyOffersAsync(FiendwarEntryPreparation pending)
    {
        var offers = pending.Leader.FiendwarKeyOffers(pending);
        for (int index = 0; index < offers.Length; index++)
            if (pending.Prepared[index].Member.Actor.Session.FiendwarKeyLeaseArrived is { } publish) await publish(offers[index]);
    }

    static async Task CancelFiendwarKeyOffersAsync(FiendwarEntryPreparation pending, byte reason)
    {
        pending.Cancel();
        try
        {
            for (int index = 0; index < pending.Offers.Length; index++)
                if (pending.Prepared[index].Member.Actor.Session.FiendwarKeyLeaseArrived is { } publish)
                    await publish(pending.Offers[index] with { Kind = FiendwarKeyLeaseKind.Cancel, Status = reason, Keys = [] });
        }
        finally { pending.Dispose(); }
    }

    async Task<CommandResult> FiendwarKeysAsync(GameConnectionContext context, Packet packet)
    {
        var request = FiendwarKeyLeaseRequests.Parse(packet.Body);
        var party = context.Session.CurrentParty?.Invoke();
        if (partyDungeons is null || party is null || context.DispatchGates?.Holds(context, party.Commands) != true)
            return CommandResult.Handled;
        using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
        if (!ReferenceEquals(context.Session.CurrentParty?.Invoke(), party)) return CommandResult.Handled;
        if (context.Session.CurrentFiendwarKeyPreparation() is { } pending)
        {
            var result = context.Session.ReportPreparedFiendwarKeys(pending, request);
            if (!pending.Active)
            {
                await CancelFiendwarKeyOffersAsync(pending, request.Status == 0 ? (byte)1 : request.Status);
                return CommandResult.Handled;
            }
            if (result is not null) await PublishFiendwarKeyMessageAsync(context, result);
            if (request.Kind != FiendwarKeyLeaseKind.Ready || !partyDungeons.CanCompleteFiendwarKeys(pending))
                return CommandResult.Handled;
            PartyDungeonNotice[] entered = [];
            try
            {
                // 选择回执只在MapSelect有效；全员ready后按原15/27再提交安装，随后发布完整28/MAI/29。
                await PartyDungeonProjection.PublishAsync(pending.Selections);
                foreach (var row in pending.Prepared) row.Member.Actor.Session.MarkFiendwarKeySelectionProjected();
                entered = partyDungeons.CompleteFiendwarKeys(pending, dungeons, rooms);
                if (entered.Length != 0) await PartyDungeonProjection.PublishAsync(entered);
            }
            finally
            {
                if (entered.Length == 0)
                {
                    context.Session.CancelRaidDungeonSelection(pending.Selections);
                    await CancelFiendwarKeyOffersAsync(pending, 2);
                }
            }
            log.Write("fiendwar_entry", new { Connection = context.Id, Dungeon = pending.Dungeon, Members = entered.Length,
                Reason = entered.Length == 0 ? "fiendwar-key-entry-rejected" : null });
            return CommandResult.Handled;
        }
        if (context.Session.PartyDungeon is not { } instance || context.FiendwarCounterRoom is not { } room)
            return CommandResult.Handled;
        var response = context.Session.ReportLiveFiendwarKeys(instance, room.Receipt, room.Serial, request);
        if (response is null) return CommandResult.Handled;
        if (response.Kind != FiendwarKeyLeaseKind.Cancel)
        {
            await PublishFiendwarKeyMessageAsync(context, response);
            return CommandResult.Handled;
        }
        // 故障先锁住原Run，SQL失败保留恢复标记；相同精确请求可重放，不能继续缺失one-shot后的战斗。
        // 先通知全员自己的原场景身份；首报告连接异常退出后，存活成员也能重放准确恢复请求。
        foreach (var member in instance.Members)
            if (member.Actor.Session.FiendwarKeyRecoveryMessage() is { } paused &&
                member.Actor.Session.FiendwarKeyLeaseArrived is { } publish) await publish(paused);
        var recovery = partyDungeons.RecoverFiendwarKeys(context.Session, instance);
        var returned = recovery.Notices;
        if (returned.Length != 0) await PartyDungeonProjection.PublishAsync(returned);
        else await PublishFiendwarKeyMessageAsync(context, response with { Status = 2 });
        if (recovery.FailureReason is not null)
            log.Write("fiendwar_key_recovery_failed", new { Connection = context.Id, request.Scene.Run, Message = recovery.FailureReason });
        log.Write("fiendwar_key_recovery", new { Connection = context.Id, request.Scene.Run, request.Scene.Dungeon,
            request.Scene.Serial, request.Handshake, Members = returned.Length, Reason = request.Status });
        return CommandResult.Handled;
    }
}
