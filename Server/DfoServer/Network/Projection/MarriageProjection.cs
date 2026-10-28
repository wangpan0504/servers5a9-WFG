using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Marriage;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class MarriageProjection(IMarriageStore store, MarriageCatalog catalog,
    OnlineSessionDirectory online, TimeProvider? clock = null)
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;

    public void PrepareInitialization(GameConnectionContext context) => context.Session.PrepareMarriageInitialization(catalog, timer.GetUtcNow());

    public async Task InitializeAsync(GameConnectionContext context)
    {
        if (context.Session.CaptureMarriageInitialization() is not { } capture) return;
        var record = store.FindMarriage(capture.Character.Id);
        if (record is not null) await SendMember(context, capture, record);
        else await context.Notify((NotiPacketType)1003, MarriageWeddingPackets.Info(0, 0, 0, 0, 0, 0),
            "marriage_initialize_empty", () => context.Session.CanPublishMarriageInitialization(capture));
        foreach (var invitation in store.FindPendingWeddings().Where(row => row.Wedding!.StartsAt > timer.GetUtcNow().ToUnixTimeSeconds() &&
                     row.Wedding.Invitations.Any(guest => guest.CharacterId == capture.Character.Id)))
            await context.Notify((NotiPacketType)1008, MarriageWeddingPackets.Letter(Wire(invitation), 0, 0),
                "marriage_invitation_restore", () => context.Session.CanPublishMarriageInitialization(capture));
    }

    public async Task PublishAsync(GameConnectionContext context, MarriageRecord record, byte reason = 0)
    {
        if (reason != 0 && context.Session.Selected is { } actor)
            await LeaveEndedHome(actor.Id);
        if (context.Session.CaptureMarriage(false) is { } capture && record.IsMember(capture.Character.Id))
        {
            await SendMember(context, capture, record, reason);
            online.FindConnectionByCharacterId(capture.Character.Id)?.Marriage?.RefreshScene();
        }
        foreach (ushort id in new[] { record.FirstId, record.SecondId })
        {
            if (id == context.Session.Selected?.Id) continue;
            await PublishMemberAsync(id, record, reason);
        }
    }

    public async Task PublishMemberAsync(ushort id, MarriageRecord record, byte reason = 0)
    {
        if (online.FindConnectionByCharacterId(id) is not { Marriage: { } transport } peer) return;
        // 只合并关系及其专属任务字段；Session不得以重载全角色替代此同步。
        peer.Session.RefreshMarriageProjection();
        if (reason != 0 && peer.Session.LeaveEndedMarriageHome() is { } departure)
            await transport.Travel(departure);
        if (peer.Session.CaptureMarriage(false) is not { } capture) return;
        transport.RefreshScene();
        bool Guard() => peer.Session.MatchesMarriage(capture, false);
        if (reason != 0) await transport.Notify(1005, MarriageWeddingPackets.Divorce(reason), "marriage_ended", Guard);
        await transport.Notify(1003, Information(record, id), "marriage_information", Guard);
        if (reason != 0)
        {
            await transport.Notify((ushort)NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(capture.Character.ActiveMainQuests), "marriage_quest_progress", Guard);
            await transport.Notify((ushort)NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(capture.Character), "marriage_quest_history", Guard);
        }
        if (record.State == 4)
        {
            await transport.Notify(1018, MarriageCouplePackets.CapsuleInformation(DisplayMember(record, id), false), "marriage_capsules", Guard);
            await transport.Notify(1011, MarriageCouplePackets.RoomInformation(DisplayRecord(record)), "marriage_home_effects", Guard);
            if (capture.Character.Position is { Town: 101, Area: 0 })
            {
                await transport.Notify(1012, MarriageCouplePackets.Storage(record, 0), "marriage_storage", Guard);
            }
        }
        else if (reason != 0)
            await transport.Notify(1011, MarriageCouplePackets.RoomInformation(record), "marriage_home_effects_clear", Guard);
        if (record.Wedding is { State: 1 } wedding && wedding.StartsAt > timer.GetUtcNow().ToUnixTimeSeconds())
            await transport.Notify(1007, MarriageWeddingPackets.Ceremony(Wire(record), checked((ushort)wedding.InviteRemaining.GetValueOrDefault(id))),
                "marriage_reservation", Guard);
    }

    async Task LeaveEndedHome(ushort id)
    {
        if (online.FindConnectionByCharacterId(id) is not { Marriage: { } transport } peer) return;
        peer.Session.RefreshMarriageProjection();
        if (peer.Session.LeaveEndedMarriageHome() is { } receipt) await transport.Travel(receipt);
    }

    async Task SendMember(GameConnectionContext context, MarriageCapture capture, MarriageRecord record, byte reason = 0)
    {
        bool Guard() => context.Session.CanPublishMarriageInitialization(capture);
        if (reason != 0) await context.Notify((NotiPacketType)1005, MarriageWeddingPackets.Divorce(reason), "marriage_ended", Guard);
        await context.Notify((NotiPacketType)1003, Information(record, capture.Character.Id), "marriage_information", Guard);
        if (reason != 0)
        {
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(capture.Character.ActiveMainQuests), "marriage_quest_progress", Guard);
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(capture.Character), "marriage_quest_history", Guard);
        }
        if (!record.Active)
        {
            await context.Notify((NotiPacketType)1011, MarriageCouplePackets.RoomInformation(record), "marriage_home_effects_clear", Guard);
            return;
        }
        if (record.State == 4)
        {
            await context.Notify((NotiPacketType)1018, MarriageCouplePackets.CapsuleInformation(DisplayMember(record, capture.Character.Id), false),
                "marriage_capsules", Guard);
            await context.Notify((NotiPacketType)1011, MarriageCouplePackets.RoomInformation(DisplayRecord(record)), "marriage_home_effects", Guard);
            if (capture.Character.Position is { Town: 101, Area: 0 })
            {
                await context.Notify((NotiPacketType)1012, MarriageCouplePackets.Storage(record, 0), "marriage_storage", Guard);
            }
        }
        if (record.Wedding is { State: 1 } wedding && wedding.StartsAt > timer.GetUtcNow().ToUnixTimeSeconds())
            await context.Notify((NotiPacketType)1007, MarriageWeddingPackets.Ceremony(Wire(record),
                checked((ushort)wedding.InviteRemaining.GetValueOrDefault(capture.Character.Id))), "marriage_reservation", Guard);
    }

    internal static byte[] Information(MarriageRecord record, ushort id) => MarriageWeddingPackets.Info(
        record.Active ? record.ContextId : 0, record.State,
        0, // 尚无业务消费者证据的保留字节，不用它承载婚礼类型或角色身份。
        checked((uint)record.MarriedAt), checked((uint)record.Members.GetValueOrDefault(id, new()).LovePoints), record.RingLevel);

    public async Task HomeSceneAsync(GameConnectionContext context, Character character, Func<bool> canSend)
    {
        if (character.Position is not { Town: 101, Area: 0 } || store.FindMarriage(character.Id) is not { State: 4 } record) return;
        await context.Notify((NotiPacketType)1011, MarriageCouplePackets.RoomInformation(DisplayRecord(record)), "marriage_home_arrival", canSend);
        await context.Notify((NotiPacketType)1012, MarriageCouplePackets.Storage(record, 0), "marriage_storage_arrival", canSend);
    }

    public async Task WeddingSceneAsync(GameConnectionContext context, Character character, bool arrival, Func<bool> canSend)
    {
        if (character.WeddingHall is not { } hall || store.FindMarriage(hall.RelationshipId) is not { } record ||
            record.Wedding?.Id != hall.CeremonyId) return;
        bool inside = character.Position is { Town: 39 } position && position.Area == hall.HallArea;
        if (inside != arrival) return;
        var wire = Wire(record);
        await context.Notify((NotiPacketType)1002, MarriageWeddingPackets.Avatar(inside ? (byte)0 : (byte)1,
            checked((byte)catalog.Ceremonies[record.Wedding.Type].AvatarType), wire.FirstName, wire.SecondName,
            hall.HallArea, 0, hall.ServerIndex), inside ? "marriage_wedding_avatar" : "marriage_wedding_avatar_clear", canSend);
    }

    MarriageMemberState DisplayMember(MarriageRecord record, ushort id)
    {
        var member = record.Members[id];
        int day = QuestCycle.Day(timer.GetUtcNow());
        return day > member.CapsuleDay ? member with { CapsulesTaken = 0 } : member;
    }

    MarriageRecord DisplayRecord(MarriageRecord record) => record.Tree is { } tree && QuestCycle.Day(timer.GetUtcNow()) > tree.WaterDay
        ? record with { Tree = tree with { Waters = 0 } } : record;

    internal MarriageCeremonyWire Wire(MarriageRecord record)
    {
        var wedding = record.Wedding ?? throw new InvalidOperationException("婚礼预约不存在");
        if (!catalog.Ceremonies.ContainsKey(wedding.Type)) throw new InvalidDataException("婚礼类型不存在");
        string first = store.FindMarriageRecipient(record.FirstId)?.Name ?? throw new InvalidOperationException("婚礼角色不存在");
        string second = store.FindMarriageRecipient(record.SecondId)?.Name ?? throw new InvalidOperationException("婚礼角色不存在");
        return new(record.ContextId, wedding.HallArea, checked((uint)wedding.StartsAt), wedding.ChannelNumber,
            checked((byte)wedding.Type), first, second);
    }

    internal static byte[] Card(uint contextId, MarriageCharacterCard card)
    {
        if (card.Equipment.Count != 13) throw new InvalidDataException("婚姻角色卡装备数无效");
        byte[] body = new byte[97];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), contextId);
        byte[] name = CharacterName.Encoding.GetBytes(card.Recipient.Name);
        if (name.Length >= 30) throw new InvalidDataException("婚姻角色卡名称过长");
        name.CopyTo(body, 12);
        body[42] = card.Recipient.Level; body[43] = card.Recipient.Job; body[44] = card.Recipient.GrowType;
        for (int index = 0; index < 13; index++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(45 + index * 4), card.Equipment[index]);
        return MarriageWeddingPackets.CharacterAck(body);
    }
}
