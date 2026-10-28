using System.Data.Common;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Marriage;
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
    InventoryProjection inventory, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;
    readonly object editorGate = new();
    readonly Dictionary<Guid, (GameSession Session, long Generation, long Epoch)> editors = [];

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
                return await Reject(context, packet.Opcode, -2);
            return packet.Opcode <= 1041
                ? await WeddingAsync(context, packet, capture)
                : await CoupleAsync(context, packet, capture);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException or DbException)
        {
            log.Write("marriage_rejected", new { Connection = context.Id, packet.Opcode, Error = error.Message });
            return await Reject(context, packet.Opcode, -1);
        }
    }

    static async Task<CommandResult> Reject(GameConnectionContext context, ushort opcode, int result)
    {
        byte[] body = opcode switch
        {
            1034 => MarriageWeddingPackets.RequestAck(result, 0, 0),
            1035 => MarriageWeddingPackets.ResponseAck(result, 0, 0, 1),
            1037 => [0],
            1038 => MarriageWeddingPackets.RingAck(result, 0),
            1043 => MarriageCouplePackets.DeleteCommentAck(result, 0),
            1045 => MarriageCouplePackets.PartnerCheckAck(result, 0, 0, 0, 0, 0, 0),
            1047 => MarriageCouplePackets.OpenCloseAck(result, false),
            _ => MarriageCouplePackets.Result(result)
        };
        await context.Reply((CmdPacketType)opcode, body, "marriage_rejected");
        return CommandResult.Handled;
    }

    static bool AtHome(Character character) => character.Position is { Town: 101, Area: 0 };

    bool Edit(GameSession session, MarriageCapture capture, Guid relation, bool acquire)
    {
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
                return ReferenceEquals(current.Session, session) && current.Generation == capture.Generation && current.Epoch == capture.Epoch;
            if (!acquire) return false;
            editors.Add(relation, (session, capture.Generation, capture.Epoch));
            return true;
        }
    }

    void CloseEditor(GameSession session, Guid relation)
    {
        lock (editorGate)
            if (editors.TryGetValue(relation, out var current) && ReferenceEquals(current.Session, session))
                editors.Remove(relation);
    }

    async Task<MarriageSessionCommit?> CommitCouple(GameConnectionContext context, MarriageCapture capture,
        MarriageRecord before, MarriageCoupleTransition transition)
    {
        if (!transition.Success) return null;
        var plan = planner.Plan(capture.Character, transition.Items, timer.GetUtcNow());
        if (plan is null) return null;
        var result = context.Session.CommitMarriage(capture, plan, before, transition.Record);
        if (result is null) return null;
        await inventory.RefreshAsync(context, result.Actor);
        return result;
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
