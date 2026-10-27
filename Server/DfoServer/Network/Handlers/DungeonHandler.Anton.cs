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
        var party = context.Session.CurrentParty?.Invoke();
        string? reason = "anton-entry-waiting-client-resources";
        PartyDungeonNotice[] selected = [], entered = [];
        if (antonRaid is { Enabled: true } && partyDungeons is not null)
        {
            reason = "anton-entry-party-dispatch-gate-mismatch";
            if (party is not null && context.DispatchGates?.Holds(context, party.Commands) == true)
            {
                using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
                reason = "anton-entry-request-unproven";
                if (context.Session.Phase == DfoServer.Game.Session.SessionPhase.MapSelect &&
                    selection.DungeonIndex <= int.MaxValue && selection.Parameter == 2 && selection.Unknown5 == 0 &&
                    selection.Unknown7 == 0 && selection.Unknown8 == 0 && selection.Unknown9 == ushort.MaxValue &&
                    selection.Unknown11 == 0 && selection.Unknown15 == 0 && selection.Unknown16 == 0 &&
                    selection.Unknown20 == 0 && selection.Unknown21 == 0)
                {
                    // 大图确认可直接发送16；每次从原Battle和分队重新签发，不能借普通选图进入团本。
                    selected = antonRaid.Select(context.Session, (int)selection.DungeonIndex, party, out reason);
                    try
                    {
                        if (selected.Length != 0)
                            entered = partyDungeons.Enter(context.Session, selection.DungeonIndex, selection.Parameter,
                                dungeons, rooms, null, null, null, null);
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
        log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = entered.Length != 0, Party = true,
            RaidType = 0, Dungeon = selection.DungeonIndex, RaidInstance = context.Session.StartedRaid?.InstanceId,
            Members = entered.Length, Runs = entered.Select(notice => notice.Scene?.Snapshot.Identity.InstanceId).Distinct().ToArray(),
            RejectReason = reason, MissingMaps = antonRaid?.MissingMaps });
        if (entered.Length != 0) await PartyDungeonProjection.PublishAsync(entered);
        else await context.Reply(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(), "anton_entry_rejected");
        return CommandResult.Handled;
    }
}
