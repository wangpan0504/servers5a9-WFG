using System.Security.Cryptography;
using DfoServer.Game.Marriage;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Marriage;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class MarriageHandler
{
    async Task<CommandResult> CoupleAsync(GameConnectionContext context, Packet packet, MarriageCapture capture)
    {
        var actor = capture.Character;
        var before = store.FindMarriage(actor.Id);
        ushort command = packet.Opcode;
        uint comment = 0;
        MarriageCoupleTransition? change = null;
        var now = timer.GetUtcNow();
        int day = QuestCycle.Day(now);
        if (command is >= 1042 and <= 1044)
        {
            uint id;
            if (command == 1042)
            {
                var request = MarriageCoupleRequests.CommentUpdate(packet.Body);
                id = request.ContextId;
                before = store.FindMarriageByContext(id);
                if (before is not null && CanComment(context, before, capture, now.ToUnixTimeSeconds()))
                    change = MarriageCoupleRules.AddComment(before, actor.Id, actor.Name, request.Text,
                        checked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)), now.ToUnixTimeSeconds(), 200);
            }
            else if (command == 1043)
            {
                var request = MarriageCoupleRequests.CommentDelete(packet.Body);
                id = request.ContextId; comment = request.CommentId;
                before = store.FindMarriageByContext(id);
                if (before?.IsMember(actor.Id) == true)
                    change = MarriageCoupleRules.DeleteComment(before, actor.Id, comment);
            }
            else
            {
                var request = MarriageCoupleRequests.CommentLoad(packet.Body);
                before = store.FindMarriageByContext(request.ContextId);
                bool allowed = before is not null && CanComment(context, before, capture, now.ToUnixTimeSeconds());
                await context.Reply((CmdPacketType)command, MarriageCouplePackets.CommentsAck(allowed ? 0 : -2,
                    request.ContextId, allowed ? before!.Comments : []), "marriage_comments");
                return CommandResult.Handled;
            }
        }
        else
        {
            if (before is not { State: 4 }) return await Reject(context, command, -2);
            if (command is 1045 or 1046) return await PartnerTeleportAsync(context, packet, capture, before);
            if (command is >= 1047 and <= 1053 && !AtHome(actor)) return await Reject(context, command, -2);
            switch (command)
            {
                case 1047:
                    bool close = MarriageCoupleRequests.OpenClose(packet.Body);
                    if (close) CloseEditor(context.Session, before.Id);
                    bool granted = close || Edit(context.Session, capture, before.Id, true);
                    await context.Reply((CmdPacketType)command, MarriageCouplePackets.OpenCloseAck(granted ? 0 : -4, close), "marriage_storage");
                    if (granted && !close)
                        await context.Notify((NotiPacketType)1012, MarriageCouplePackets.Storage(before, 0), "marriage_storage_contents");
                    return CommandResult.Handled;
                case 1048:
                    var move = MarriageCoupleRequests.MoveItem(packet.Body);
                    if (Edit(context.Session, capture, before.Id, false))
                        change = MarriageCoupleRules.Move(before, actor.Id, move.Source, move.Target, catalog);
                    break;
                case 1049:
                    var delete = MarriageCoupleRequests.DeleteItem(packet.Body);
                    if (Edit(context.Session, capture, before.Id, false))
                        change = MarriageCoupleRules.Delete(before, actor.Id, delete.Slot, delete.ItemId, delete.Quantity);
                    break;
                case 1050:
                case 1051:
                    var item = MarriageCoupleRequests.UseItem(packet.Body);
                    change = command == 1050
                        ? MarriageCoupleRules.Plant(before, actor.Id, item.Slot, item.ItemId, now.ToUnixTimeSeconds(), day, catalog)
                        : MarriageCoupleRules.Water(before, actor.Id, item.Slot, item.ItemId, now.ToUnixTimeSeconds(), day, catalog);
                    break;
                case 1052:
                    MarriageCoupleRequests.Empty(packet.Body);
                    change = MarriageCoupleRules.Harvest(before, actor.Id, now.ToUnixTimeSeconds(), catalog,
                        total => RandomNumberGenerator.GetInt32(checked((int)total)));
                    break;
                case 1053:
                    MarriageCoupleRequests.Empty(packet.Body);
                    change = MarriageCoupleRules.RemoveTree(before, actor.Id);
                    break;
                case 1054:
                case 1055:
                case 1056:
                    MarriageCoupleRequests.Empty(packet.Body);
                    change = command == 1054 ? MarriageCoupleRules.MakeCapsule(before, actor.Id, day, catalog)
                        : command == 1055 ? MarriageCoupleRules.GiveCapsule(before, actor.Id, day)
                        : MarriageCoupleRules.TakeCapsule(before, actor.Id, day, catalog);
                    break;
            }
        }
        if (before is null || change is null) return await Reject(context, command, -2);
        var committed = await CommitCouple(context, capture, before, change);
        int result = committed is not null ? 0 : change.Success ? -3 : change.Result;
        byte[] ack = command is >= 1054 and <= 1056
            ? MarriageCouplePackets.CapsuleAck(command, result, (committed?.Record ?? before).Members[actor.Id])
            : command == 1043 ? MarriageCouplePackets.DeleteCommentAck(result, comment)
            : MarriageCouplePackets.Result(result);
        await context.Reply((CmdPacketType)command, ack, "marriage_couple_result");
        if (committed is null) return CommandResult.Handled;
        var record = committed.Record;
        if (command is 1048 or 1049)
        {
            var updates = before.Storage.Keys.Union(record.Storage.Keys).Select(slot => new MarriageCoupleItemUpdate(1,
                record.Storage.GetValueOrDefault(slot).ItemId, slot, record.Storage.GetValueOrDefault(slot).Count))
                .Concat(before.Furniture.Keys.Union(record.Furniture.Keys).Select(slot => new MarriageCoupleItemUpdate(0,
                    record.Furniture.GetValueOrDefault(slot).ItemId, slot, record.Furniture.GetValueOrDefault(slot).Count))).ToArray();
            await PublishCouple(context, record, 1014, MarriageCouplePackets.ItemUpdates(updates));
            // 1011的本版消费者会重算婚房加成；双方在普通城镇也必须更新这份派生输入。
            await new DfoServer.Network.Projection.MarriageProjection(store, catalog, online, timer).PublishAsync(context, record);
        }
        if (command is >= 1050 and <= 1053)
            await PublishCouple(context, record, 1015, MarriageCouplePackets.TreeInformation(record.Tree));
        if (command == 1052)
            await context.Notify((NotiPacketType)1017, MarriageCouplePackets.HarvestRewards(change.Items
                .Where(row => row.Count > 0).Select(row => new MarriageItemCount(row.ItemId, row.Count)).ToArray()), "marriage_tree_reward");
        if (command is >= 1054 and <= 1056)
        {
            await context.Notify((NotiPacketType)1018, MarriageCouplePackets.CapsuleInformation(record.Members[actor.Id], false), "marriage_capsules");
            if (online.FindConnectionByCharacterId(record.Other(actor.Id)) is { Marriage: { } transport } partner)
            {
                partner.Session.RefreshMarriageProjection();
                if (partner.Session.CaptureMarriage(false) is { } peerCapture)
                    await transport.Notify(1018, MarriageCouplePackets.CapsuleInformation(record.Members[record.Other(actor.Id)], command == 1055),
                        "marriage_partner_capsules", () => partner.Session.MatchesMarriage(peerCapture, false));
            }
        }
        return CommandResult.Handled;
    }

    static bool CanComment(GameConnectionContext context, MarriageRecord record, MarriageCapture capture, long now) => record.Active &&
        (record.IsMember(capture.Character.Id) || record.Wedding is { State: 1 or 2 } wedding &&
            capture.Character.WeddingHall?.CeremonyId == wedding.Id &&
            now <= wedding.EndsAt && capture.Character.Position is { Town: 39 } position && position.Area == wedding.HallArea &&
            context.Session.ChannelEntry?.Identity is { } channel && channel.ServerIndex == wedding.ServerIndex &&
            channel.Number == wedding.ChannelNumber && wedding.Invitations.Any(row => row.CharacterId == capture.Character.Id));
}
