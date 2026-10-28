using DfoServer.Game.Session;
using System.Collections.Immutable;

namespace DfoServer.Game.Pvp;

// 发布凭据只携带真实提交的原局与本人回执；显示字段由协议层在证据闭合后投影。
internal sealed record PvpArenaResultDelivery(PvpArenaMembership Recipient, PvpArenaMatch Match,
    PvpArenaResultRecord Record, PvpRatingReceipt Receipt)
{
    public int WinPoints { get; init; }
    public ImmutableArray<PvpArenaResultDisplayRow> Rows { get; init; } = [];
}
internal sealed record PvpArenaResultDisplayRow(ushort Character, byte Seat, uint Kills, int Grade);

internal sealed partial class PvpArenaService
{
    // 调用方持房间Commands；不在这里补写数据库或替其他账号消费来源。
    public PvpArenaResultDelivery? CaptureResultDelivery(GameSession session, Guid match) =>
        session.CurrentPvpArena is not { } member ? null : member.Actor.Execute(() =>
        {
            lock (gate)
            {
                if (!Current(member) || member.Room.Match is not { CommittedRecord: { } record } current ||
                    current.Id != match || current.RatingReceipt(member.Token) is not { } receipt) return null;
                return new PvpArenaResultDelivery(member, current, record, receipt)
                {
                    WinPoints = session.Selected!.Inventory.WinPoints,
                    Rows = current.Fighters.Where(fighter => member.Room.Member(fighter.Membership) is not null &&
                        current.RatingReceipt(fighter.Membership) is not null).OrderBy(fighter => fighter.Seat)
                        .Select(fighter => new PvpArenaResultDisplayRow(fighter.CharacterId, fighter.Seat,
                            current.Kills(fighter.Membership), current.RatingReceipt(fighter.Membership)!.Change.Grade))
                        .ToImmutableArray()
                };
            }
        });

    public bool CanPublishResultDelivery(PvpArenaResultDelivery delivery)
    {
        lock (gate) return CurrentDelivery(delivery);
    }

    // 协议层须先完整构包并确认发送队列接受，再在原房间门禁内调用；失败不能确认。
    public bool ConfirmResultQueued(PvpArenaResultDelivery delivery, DateTimeOffset? now = null) => delivery.Recipient.Actor.Execute(() =>
    {
        lock (gate) return CurrentDelivery(delivery) && delivery.Match.ConfirmResultQueued(delivery.Match.Id,
            delivery.Recipient.Token, delivery.Receipt, now);
    });

    public PvpArenaNotice[] ReturnResult(GameSession session, Guid match)
    {
        var room = session.CurrentPvpArena?.Room;
        var notices = Change(session, member => member.Room.EndResult(member.Token, match), PvpArenaNoticeKind.State);
        // 匹配房间只属于这一预约；最后一名原参战者返回后清双方原成员，允许重新排队。
        return notices.Length != 0 && room is { IsMatching: true, Match: null }
            ? CancelMatchingRoom(room, DateTimeOffset.UtcNow) : notices;
    }

    // 调用方持原房间Commands。每名接收者只持自己的owner，回调仅构包入队，不能等待网络。
    internal void PublishResults(PvpArenaRoom room, DateTimeOffset? now = null)
    {
        PvpArenaMembership[] recipients;
        lock (gate) recipients = Registered(room) && room.Match is { CommittedRecord: not null } match &&
            match.Phase is (PvpArenaMatchPhase.Finished or PvpArenaMatchPhase.Cancelled) && match.Fighters.All(fighter =>
                room.Member(fighter.Membership) is null || match.HasRatingReceipt(fighter.Membership))
            ? members.SnapshotValues().Where(member => ReferenceEquals(member.Room, room) && member.IsCurrent &&
                match.HasRatingReceipt(member.Token) && !match.IsResultQueued(member.Token)).ToArray() : [];
        foreach (var member in recipients)
        {
            try
            {
                member.Actor.Execute(() =>
                {
                    var delivery = room.Match is { } current ? CaptureResultDelivery(member.Actor.Session, current.Id) : null;
                    if (delivery is not null && CanPublishResultDelivery(delivery) &&
                        member.Actor.Session.PvpArenaResultArrived?.Invoke(delivery) == true)
                        ConfirmResultQueued(delivery, now);
                    return true;
                });
            }
            catch (InvalidOperationException) when (!member.IsCurrent) { }
        }
    }

    bool CurrentDelivery(PvpArenaResultDelivery delivery) => Current(delivery.Recipient) &&
        ReferenceEquals(delivery.Recipient.Room.Match, delivery.Match) &&
        ReferenceEquals(delivery.Match.CommittedRecord, delivery.Record) &&
        ReferenceEquals(delivery.Match.RatingReceipt(delivery.Recipient.Token), delivery.Receipt);
}
