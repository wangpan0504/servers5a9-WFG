using DfoServer.Game.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task<CommandResult> SelectAntonAsync(GameConnectionContext context, DungeonSelectionRequest selection)
    {
        var service = context.Session.ChannelEntry?.Type == 50 ? preyRaid : antonRaid;
        var party = context.Session.CurrentParty?.Invoke();
        var scene = party ?? service?.CapturePreySoloScene(context.Session);
        string? reason = "anton-entry-waiting-client-resources";
        PartyDungeonNotice[] selected = [], entered = [];
        if (service is { Enabled: true } && partyDungeons is not null)
        {
            reason = "anton-entry-party-dispatch-gate-mismatch";
            if (scene is not null && context.DispatchGates?.Holds(context, party?.Commands ?? context.CommandGate) == true)
            {
                using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, scene.Members);
                reason = "anton-entry-request-unproven";
                // 实机 CMD16 是 25 字节，Unknown21 为 0xFFFFFFFF（pcap-manual-1235）。自测构造包该字段为 0。
                // 只接受这两个值；其它 Unknown21 仍是 anton-entry-request-unproven。
                if (context.Session.Phase is DfoServer.Game.Session.SessionPhase.MapSelect or DfoServer.Game.Session.SessionPhase.Loading &&
                    selection.DungeonIndex <= int.MaxValue && selection.Parameter == 2 && selection.Unknown5 == 0 &&
                    selection.Unknown7 == 0 && selection.Unknown8 == 0 && selection.Unknown9 == ushort.MaxValue &&
                    selection.Unknown11 == 0 && selection.Unknown15 == 0 && selection.Unknown16 == 0 &&
                    selection.Unknown20 == 0 && selection.Unknown21 is 0 or uint.MaxValue)
                {
                    // 大图确认可直接发送16；首次从原Battle签发，Loading只验证并重放原Run。
                    bool replay = context.Session.Phase == DfoServer.Game.Session.SessionPhase.Loading;
                    selected = service.Select(context.Session, (int)selection.DungeonIndex, scene, out reason, replayLoading: replay);
                    try
                    {
                        if (replay) entered = selected;
                        else if (selected.Length != 0)
                            entered = partyDungeons.Enter(context.Session, selection.DungeonIndex, selection.Parameter,
                                dungeons, rooms, null, null, null, null, preySolo: party is null ? scene : null);
                        if (entered.Length == 0 && reason is null)
                            reason = context.Session.LastFormalDungeonRejection ?? "anton-entry-authorization-changed";
                    }
                    finally
                    {
                        if (entered.Length == 0) context.Session.CancelRaidDungeonSelection(selected);
                    }
                }
            }
        }
        log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = entered.Length != 0, Party = party is not null,
            RaidType = context.Session.ChannelEntry?.Type == 50 ? 3 : 0, Replay = entered.Any(notice => notice.Kind == PartyDungeonNoticeKind.LoadingReplay),
            Dungeon = selection.DungeonIndex, RaidInstance = context.Session.StartedRaid?.InstanceId,
            Members = entered.Length, Runs = entered.Select(notice => notice.Scene?.Snapshot.Identity.InstanceId).Distinct().ToArray(),
            RejectReason = reason, MissingMaps = service?.MissingMaps });
        if (entered.Length != 0) await PartyDungeonProjection.PublishAsync(entered);
        else await context.Reply(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(), "anton_entry_rejected");
        return CommandResult.Handled;
    }
}
