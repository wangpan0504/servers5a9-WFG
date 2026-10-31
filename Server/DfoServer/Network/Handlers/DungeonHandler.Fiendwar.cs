using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task<CommandResult> FiendwarBossAsync(GameConnectionContext context, Packet request)
    {
        var report = FiendwarRequests.Boss(request.Body);
        var instance = context.Session.PartyDungeon;
        using var peers = instance is null ? null : await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members);
        var change = instance?.ReportFiendwarBoss(context.Session, report.Operation, report.Value);
        log.Write("fiendwar_boss_report", new { Connection = context.Id, report.Operation, report.Value, Accepted = change is not null });
        if (change is null) return CommandResult.Handled;
        context.Session.PublishRaidCombat(change);
        // 00D310D0只读一个世界索引字节；拒绝请求不伪造成功回执。
        await context.Reply(CmdPacketType.FIEND_WAR_BOSS_PROC,
            [(byte)(context.Session.Selected!.Position.Area == 30 ? 0 : 1)], "fiendwar_boss_report");
        return CommandResult.Handled;
    }

    async Task<CommandResult> SelectFiendwarAsync(GameConnectionContext context, DungeonSelectionRequest selection)
    {
        PartyDungeonNotice[] selected = [], entered = [];
        string? reason = "fiendwar-entry-request-unproven";
        if (fiendwarRaid is not null && partyDungeons is not null && selection.DungeonIndex <= int.MaxValue &&
            selection.Parameter == 2 && selection.Unknown5 == 0 && selection.Unknown7 == 0 && selection.Unknown8 == 0 &&
            selection.Unknown9 == ushort.MaxValue && selection.Unknown11 == 0 && selection.Unknown15 == 0 &&
            selection.Unknown16 == 0 && selection.Unknown20 == 0 && selection.Unknown21 is 0 or uint.MaxValue &&
            fiendwarRaid.Find(context.Session) is { } raid)
        {
            var existing = context.Session.CurrentParty?.Invoke();
            // 已有遭遇队伍由分派层持共享门禁，可等待其成员命令结束；不能把队友刚发完的加载/心跳误判成入场失败。
            using var commands = existing is not null && context.DispatchGates?.Holds(context, existing.Commands) == true
                ? await PartyDungeonProjection.AcquirePeersAsync(context, existing.Members)
                : RaidStartCommands.TryEnter(fiendwarRaid.EncounterMembers(context.Session, raid), context.Session);
            if (commands is not null)
            {
                bool replay = context.Session.Phase == SessionPhase.Loading;
                var party = context.Session.Phase == SessionPhase.Town
                    ? fiendwarRaid.Group(context.Session, (int)selection.DungeonIndex, out reason) : context.Session.CurrentParty?.Invoke();
                if (party is not null)
                {
                    selected = fiendwarRaid.Select(context.Session, (int)selection.DungeonIndex, party, out reason, replay);
                    try
                    {
                        entered = replay ? selected : selected.Length == 0 ? [] :
                            partyDungeons.Enter(context.Session, selection.DungeonIndex, selection.Parameter, dungeons, rooms, null, null, null, null,
                                onRejected: (rejection, slot) => reason = rejection + ":member=" + slot);
                        if (entered.Length == 0 && reason is null) reason = context.Session.LastFormalDungeonRejection;
                    }
                    finally { if (entered.Length == 0) context.Session.CancelRaidDungeonSelection(selected); }
                }
            }
            else reason = "fiendwar-member-command-busy";
        }
        log.Write("fiendwar_entry", new { Connection = context.Id, Dungeon = selection.DungeonIndex, Members = entered.Length, Reason = reason });
        if (entered.Length != 0) await PartyDungeonProjection.PublishAsync(entered);
        else await context.Reply(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(), "fiendwar_entry_rejected");
        return CommandResult.Handled;
    }
}
