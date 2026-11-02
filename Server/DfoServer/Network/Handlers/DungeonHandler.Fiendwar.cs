using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    Task<CommandResult> FiendwarSupportAsync(GameConnectionContext context, Packet request)
    {
        byte lane;
        try { lane = FiendwarRequests.SupportLane(request.Body); }
        catch (InvalidDataException) { return Task.FromResult(CommandResult.Unhandled); }
        var change = context.Session.RequestFiendwarSupport(lane);
        log.Write("fiendwar_support_request", new { Connection = context.Id, Lane = lane, Accepted = change is not null });
        if (change is not null) context.Session.PublishRaidCombat(change);
        // ACK1570登记的046D14A0直接返回；只广播真实NOTI1554，不编造成功正文。
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> FiendwarBossAsync(GameConnectionContext context, Packet request)
    {
        var report = FiendwarRequests.Boss(request.Body);
        var instance = context.Session.PartyDungeon;
        using var peers = instance is null ? null : await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members);
        var change = instance?.ReportFiendwarBoss(context.Session, report.Operation, report.Value);
        log.Write("fiendwar_boss_report", new { Connection = context.Id, report.Operation, report.Value, Accepted = change is not null });
        if (change is null) return CommandResult.Handled;
        context.Session.PublishRaidCombat(change);
        // 00D310D0是NOTI1554的团长支援消费者，与CMD1554的Boss报告无关。
        // 成果由原战况快照发布，不把同号通知的路线字节伪造成Boss成功ACK。
        return CommandResult.Handled;
    }

    internal async Task<bool> EnterFiendwarAreaAsync(GameConnectionContext context, AreaRequest request)
    {
        if (fiendwarRaid?.CaptureAreaEncounter(context.Session, request) is not { } encounter) return false;
        using var commands = RaidStartCommands.TryEnter(encounter.Members, context.Session);
        if (commands is null)
        {
            log.Write("fiendwar_area_travel", new { Connection = context.Id, Accepted = false,
                Reason = "fiendwar-member-command-busy", Destination = request.Area });
            return true;
        }
        FiendwarAreaTravelReceipt[] receipts;
        string? reason;
        try { receipts = fiendwarRaid.TravelEncounter(encounter, out reason); }
        catch (System.Data.Common.DbException error)
        {
            // 整队位置事务已回滚；保持原连接与原地区，不能给客户端发送半队成功切图。
            log.Write("fiendwar_area_travel", new { Connection = context.Id, Accepted = false,
                Reason = "fiendwar-area-sql-rollback", Destination = request.Area,
                Error = error.GetType().Name, Code = error.ErrorCode });
            return true;
        }
        log.Write("fiendwar_area_travel", new { Connection = context.Id, Accepted = receipts.Length != 0,
            Reason = reason, Destination = request.Area, Members = receipts.Length });
        if (receipts.Length == 0) return true;
        // 同一reader在Dispatch释放本人、队伍及额外成员门禁后等待真实23，不能把慢FIFO锁在全队命令内。
        context.AfterDispatch = () => PublishFiendwarAreaAsync(context, encounter, receipts);
        return true;
    }

    async Task PublishFiendwarAreaAsync(GameConnectionContext context, FiendwarAreaEncounter encounter,
        FiendwarAreaTravelReceipt[] receipts)
    {
        // 全员SQL和内存位置已提交；每条真实连接先发布23，再由原FWL1全员ready发布27/28/29。
        var published = await Task.WhenAll(receipts.Select(async row =>
        {
            try { return await row.Publish(row.Travel); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            { row.Actor.Session.Disconnect(); return false; }
            catch (InvalidOperationException error) when (error.Message == "Stale account generation" && !row.Actor.Owner.IsCurrent)
            { return false; }
        }));
        if (published.Any(success => !success)) return;
        using var dispatch = await DispatchGateScope.EnterAsync(context, context.Stop);
        context.DispatchGates = dispatch;
        using var commands = RaidStartCommands.TryEnter(encounter.Members, context.Session);
        string? reason = commands is null ? "fiendwar-member-command-busy-after-area" :
            fiendwarRaid!.CanResumeAreaEncounter(encounter, receipts) ? null : "fiendwar-area-receipt-changed";
        if (reason is not null)
        {
            log.Write("fiendwar_area_prepare", new { Connection = context.Id, Accepted = false,
                Reason = reason, Destination = encounter.Request.Area });
            return;
        }
        // 重新取得共享门禁后准备仍只排队，不持全队门禁等待任何offer写出。
        using var publication = context.Sender.QueuePublication(() => context.Owner.IsCurrent && !context.Stop.IsCancellationRequested);
        _ = await PrepareFiendwarAsync(context, checked((uint)encounter.Dungeon), 2,
            replyRejected: false, commandsHeld: true, expectedMembers: encounter.Members);
    }

    Task<CommandResult> SelectFiendwarAsync(GameConnectionContext context, DungeonSelectionRequest selection)
    {
        bool verified = selection.Parameter == 2 && selection.Unknown5 == 0 && selection.Unknown7 == 0 && selection.Unknown8 == 0 &&
            selection.Unknown9 == ushort.MaxValue && selection.Unknown11 == 0 && selection.Unknown15 == 0 &&
            selection.Unknown16 == 0 && selection.Unknown20 == 0 && selection.Unknown21 is 0 or uint.MaxValue;
        return PrepareFiendwarAsync(context, selection.DungeonIndex, selection.Parameter, verified);
    }

    async Task<CommandResult> PrepareFiendwarAsync(GameConnectionContext context, uint dungeon, byte difficulty,
        bool verified = true, bool replyRejected = true, bool commandsHeld = false, RaidParty? expectedMembers = null)
    {
        PartyDungeonNotice[] entered = [];
        bool waitingForKeys = false;
        string? reason = "fiendwar-entry-request-unproven";
        if (verified && fiendwarRaid is not null && partyDungeons is not null && dungeon <= int.MaxValue &&
            fiendwarRaid.Find(context.Session) is { } raid)
        {
            var existing = context.Session.CurrentParty?.Invoke();
            using var commands = commandsHeld ? null : existing is not null && context.DispatchGates?.Holds(context, existing.Commands) == true
                ? await PartyDungeonProjection.AcquirePeersAsync(context, existing.Members)
                : RaidStartCommands.TryEnter(fiendwarRaid.EncounterMembers(context.Session, raid), context.Session);
            if (commandsHeld || commands is not null)
            {
                bool replay = context.Session.Phase == SessionPhase.Loading;
                var party = context.Session.Phase == SessionPhase.Town
                    ? fiendwarRaid.Group(context.Session, (int)dungeon, out reason, expectedMembers) : context.Session.CurrentParty?.Invoke();
                if (party is not null)
                {
                    if (replay) entered = fiendwarRaid.Select(context.Session, (int)dungeon, party, out reason, replay: true);
                    else if (context.Session.CurrentFiendwarKeyPreparation() is { } previous)
                    {
                        if (previous.Dungeon == dungeon)
                        {
                            waitingForKeys = true;
                            reason = null;
                            await PublishFiendwarKeyOffersAsync(previous);
                        }
                        else reason = "fiendwar-key-preparation-pending";
                    }
                    else
                    {
                        var selected = context.Session.HeldFiendwarKeySelection(dungeon);
                        if (selected.Length == 0)
                        {
                            selected = fiendwarRaid.Select(context.Session, (int)dungeon, party, out reason);
                            if (selected.Length != 0) selected = DfoServer.Game.Session.GameSession.HoldFiendwarKeySelections(selected);
                        }
                        if (selected.Length != 0 && partyDungeons.PrepareFiendwarKeys(context.Session, dungeon,
                                difficulty, dungeons, rooms, selected) is { } pending)
                        {
                            waitingForKeys = true;
                            reason = null;
                            // 只排出offer并释放本次命令，不等待同一reader将来的candidate/ready。
                            await PublishFiendwarKeyOffersAsync(pending);
                        }
                        else reason ??= context.Session.LastFormalDungeonRejection ?? "fiendwar-key-prepare-rejected";
                    }
                }
            }
            else reason = "fiendwar-member-command-busy";
        }
        log.Write("fiendwar_entry", new { Connection = context.Id, Dungeon = dungeon, Members = entered.Length,
            WaitingForKeys = waitingForKeys, Reason = reason });
        if (entered.Length != 0) await PartyDungeonProjection.PublishAsync(entered);
        else if (!waitingForKeys && replyRejected) await context.Reply(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(), "fiendwar_entry_rejected");
        return CommandResult.Handled;
    }
}
