using System.Data.Common;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Marriage;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class MarriageHandler(IMarriageStore store, MarriageCatalog catalog, ItemCatalog items,
    MarriageInventoryPlanner planner, TownCatalog maps, TownProjection town, OnlineSessionDirectory online,
    InventoryProjection inventory, ServerLog log, CharacterCatalog characters, TimeProvider? clock = null) : IGameCommandModule
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;
    readonly object editorGate = new();
    readonly Dictionary<Guid, (GameSession Session, long Generation, long Epoch)> editors = [];

    MarriageProjection Projection() => new(store, catalog, online, characters.DefaultAppearance, items, timer);

    public void Register(GameCommandRegistry commands)
    {
        for (ushort opcode = 1034; opcode <= 1056; opcode++)
            commands.Register((CmdPacketType)opcode, HandleAsync, nameof(MarriageHandler), UserStateRequirement.WorldReady);
    }

    async Task<CommandResult> HandleAsync(GameConnectionContext context, Packet packet)
    {
        try
        {
            if (context.BlocksTownActions || context.Session.CaptureMarriage() is not { } capture)
                return await RejectRequest(context, packet, context.Session.Phase != SessionPhase.Town
                    ? MarriageFailurePackets.WrongPlace(packet.Opcode) : MarriageFailurePackets.Unspecified(packet.Opcode));
            return packet.Opcode <= 1041
                ? await WeddingAsync(context, packet, capture)
                : await CoupleAsync(context, packet, capture);
        }
        catch (MarriageRuleException error)
        {
            int result = MarriageFailurePackets.Rule(packet.Opcode, error.Reason);
            log.Write("marriage_rejected", new { Connection = context.Id, packet.Opcode, result, error.Reason, Error = error.Message });
            return await RejectRequest(context, packet, result);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException or DbException)
        {
            log.Write("marriage_rejected", new { Connection = context.Id, packet.Opcode, Error = error.Message });
            return await RejectRequest(context, packet, MarriageFailurePackets.Unspecified(packet.Opcode));
        }
    }

    async Task<CommandResult> Reject(GameConnectionContext context, ushort opcode, int result)
    {
        log.Write("marriage_failure", new { Connection = context.Id, Opcode = opcode, Result = result });
        await context.Reply((CmdPacketType)opcode, MarriageFailurePackets.Body(opcode, result), "marriage_rejected");
        return CommandResult.Handled;
    }

    async Task<CommandResult> RejectRequest(GameConnectionContext context, Packet packet, int result)
    {
        byte[]? body = null;
        try
        {
            switch (packet.Opcode)
            {
                case 1034:
                    var request = MarriageWeddingRequests.Request(packet.Body);
                    body = MarriageFailurePackets.Body(1034, result, request.Target, request.Operation, extraA: request.ExtraA, extraB: request.ExtraB);
                    break;
                case 1035:
                    var response = MarriageWeddingRequests.Response(packet.Body);
                    if (result == MarriageFailurePackets.Unspecified(1035))
                        result = MarriageFailurePackets.Unspecified(1035, response.Operation);
                    return await RejectResponse(context, response, result);
                case 1043:
                    var comment = MarriageCoupleRequests.CommentDelete(packet.Body);
                    body = MarriageFailurePackets.Body(1043, result, commentId: comment.CommentId);
                    break;
                case 1047:
                    body = MarriageFailurePackets.Body(1047, result, close: MarriageCoupleRequests.OpenClose(packet.Body));
                    break;
            }
        }
        catch (InvalidDataException) { }
        if (body is null) return await Reject(context, packet.Opcode, result);
        log.Write("marriage_failure", new { Connection = context.Id, packet.Opcode, Result = result });
        await context.Reply((CmdPacketType)packet.Opcode, body, "marriage_rejected");
        return CommandResult.Handled;
    }

    static bool AtHome(Character character) => character.Position is { Town: 101, Area: 0 };

    bool Edit(GameSession session, MarriageCapture capture, Guid relation, bool acquire, out int failure)
    {
        failure = MarriageCoupleRules.MoveInvalidRequest;
        lock (editorGate)
        {
            // 每次访问都清理失效占用，避免已结束的婚约长期保留离线Session引用。
            foreach (var entry in editors.ToArray())
            {
                var holder = entry.Value;
                if (!holder.Session.MarriageOwner.IsCurrent || holder.Session.SelectionEpoch != holder.Epoch ||
                    !holder.Session.Ready || holder.Session.Selected is not { } selected || !AtHome(selected))
                    editors.Remove(entry.Key);
            }
            if (editors.TryGetValue(relation, out var current))
            {
                bool granted = ReferenceEquals(current.Session, session) && current.Generation == capture.Generation && current.Epoch == capture.Epoch;
                failure = granted ? 0 : MarriageCoupleRules.MoveEditorBusy;
                return granted;
            }
            if (!acquire) return false;
            editors.Add(relation, (session, capture.Generation, capture.Epoch));
            failure = 0;
            return true;
        }
    }

    void CloseEditor(GameSession session, Guid relation)
    {
        lock (editorGate)
            if (editors.TryGetValue(relation, out var current) && ReferenceEquals(current.Session, session))
                editors.Remove(relation);
    }

    async Task<(MarriageSessionCommit? Commit, int Result)> CommitCouple(GameConnectionContext context, MarriageCapture capture,
        MarriageRecord before, MarriageCoupleTransition transition, ushort command)
    {
        if (!transition.Success) return (null, transition.Result);
        var plan = planner.Plan(capture.Character, transition.Items, timer.GetUtcNow());
        if (plan is null) return (null, command == 1056 ? -6 : MarriageFailurePackets.Unspecified(command));
        var result = context.Session.CommitMarriage(capture, plan, before, transition.Record);
        if (result is null) return (null, MarriageFailurePackets.Unspecified(command));
        await inventory.RefreshAsync(context, result.Actor);
        return (result, 0);
    }

    async Task PublishCouple(GameConnectionContext context, MarriageRecord record, ushort opcode, byte[] body)
    {
        await context.Notify((NotiPacketType)opcode, body, "marriage_state");
        if (!record.IsMember(context.Session.Selected?.Id ?? 0)) return;
        var partner = online.FindConnectionByCharacterId(record.Other(context.Session.Selected!.Id));
        if (partner?.Marriage is not { } transport || partner.Session.ChannelEntry?.Identity != context.Session.ChannelEntry?.Identity) return;
        partner.Session.RefreshMarriageProjection();
        if (partner.Session.CaptureMarriage(false) is not { } capture || !record.IsMember(capture.Character.Id) ||
            (opcode is not (1018 or 1019) && !AtHome(capture.Character))) return;
        await transport.Notify(opcode, body, "marriage_partner_state", () => partner.Session.MatchesMarriage(capture, false));
    }
}
