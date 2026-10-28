using System.Collections.Immutable;
using System.Data.Common;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Marriage;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Marriage;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Projection;

internal sealed class MarriageWeddingScheduler(IMarriageStore store, MarriageCatalog catalog, ItemCatalog items,
    OnlineSessionDirectory online, MarriageProjection projection, ServerLog log, TimeProvider? clock = null)
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken stop)
    {
        await TickAsync(startup: true);
        while (!stop.IsCancellationRequested)
        {
            // 轮询使用真实时间；注入时钟只决定账本时刻，不占用其他玩法的业务定时器。
            try { await Task.Delay(TimeSpan.FromSeconds(1), stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            await TickAsync();
        }
    }

    internal async Task TickAsync(bool startup = false)
    {
        long now = timer.GetUtcNow().ToUnixTimeSeconds();
        foreach (var before in store.FindPendingWeddings().Where(row => row.Wedding!.StartsAt <= now))
        {
            try
            {
                if (startup || now >= before.Wedding!.EndsAt)
                {
                    await Cancel(before, now);
                    continue;
                }
                var first = online.FindConnectionByCharacterId(before.FirstId);
                var second = online.FindConnectionByCharacterId(before.SecondId);
                if (first is null || second is null)
                {
                    await Cancel(before, now);
                    continue;
                }
                if (!first.Session.CommandGate.Wait(0)) continue;
                try
                {
                    if (!second.Session.CommandGate.Wait(0)) continue;
                    try
                    {
                        var firstCapture = first.Session.CaptureMarriage();
                        var secondCapture = second.Session.CaptureMarriage();
                        bool success = firstCapture is not null && secondCapture is not null &&
                            AccountOwners.Lease.ExecuteTogether(first.Session.MarriageOwner, second.Session.MarriageOwner, () =>
                            {
                                if (!first.Session.MatchesMarriage(firstCapture, true) || !second.Session.MatchesMarriage(secondCapture, true) ||
                                    !InHall(first, before.Wedding!) || !InHall(second, before.Wedding!)) return false;
                                var after = Complete(before, now);
                                store.CommitMarriageSettlement(before, after, SuccessMail(before, now));
                                first.Session.RefreshMarriageProjection();
                                second.Session.RefreshMarriageProjection();
                                return true;
                            });
                        if (!success) { await Cancel(before, now); continue; }
                        var committed = store.FindMarriage(before.Id) ?? throw new InvalidOperationException("婚礼结算记录丢失");
                        await PublishMember(before.FirstId, committed);
                        await PublishMember(before.SecondId, committed);
                        await BeginMovie(committed);
                    }
                    finally { second.Session.CommandGate.Release(); }
                }
                finally { first.Session.CommandGate.Release(); }
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or DbException or OverflowException)
            {
                log.Write("marriage_scheduler_rejected", new { before.Id, before.Revision, Error = error.Message });
            }
        }
    }

    internal static bool InHall(ChatProbeConnection connection, MarriageWeddingState wedding) =>
        connection.Session.Ready && connection.Session.Phase == SessionPhase.Town &&
        connection.Session.Selected?.WeddingHall?.CeremonyId == wedding.Id &&
        connection.Session.Selected?.Position is { Town: 39 } position && position.Area == wedding.HallArea &&
        connection.Session.ChannelEntry?.Identity is { } channel &&
        channel.ServerIndex == wedding.ServerIndex && channel.Number == wedding.ChannelNumber;

    internal static MarriageRecord Complete(MarriageRecord before, long now)
    {
        if (before.State is not (2 or 3) || before.Wedding is not { State: 1 } wedding || now < wedding.StartsAt || now >= wedding.EndsAt)
            throw new InvalidOperationException("婚礼未到开始时刻或已经处理");
        return before with
        {
            Revision = checked(before.Revision + 1), State = 4, MarriedAt = wedding.StartsAt,
            Wedding = wedding with { State = 2, Gifts = wedding.Gifts.Select(row => row with { Settled = true }).ToImmutableArray() }
        };
    }

    internal static MarriageRecord Cancelled(MarriageRecord before, long now)
    {
        if (before.State is not (2 or 3) || before.Wedding is not { State: 1 } wedding || now < wedding.StartsAt)
            throw new InvalidOperationException("婚礼尚未到期或已经处理");
        return before with
        {
            Revision = checked(before.Revision + 1), State = 1,
            Wedding = wedding with { State = 3, Gifts = wedding.Gifts.Select(row => row with { Settled = true }).ToImmutableArray() }
        };
    }

    async Task Cancel(MarriageRecord before, long now)
    {
        var ledger = MarriageWeddingRules.RefundableLedger(before);
        List<MailboxDelivery> mail = [];
        foreach (var row in ledger)
        {
            uint remaining = row.Gold;
            int part = 0;
            do
            {
                uint gold = Math.Min(remaining, 100_000_000u);
                mail.Add(Delivery(before, $"refund:{row.CharacterId}:{row.GiftId:N}:{part}", row.CharacterId, gold,
                    part == 0 && row.Item.Count > 0 ? [row.Item] : [], now, "婚礼未能按时举行，实际支付的费用已退还。"));
                remaining -= gold;
                part++;
            } while (remaining > 0);
        }
        var after = store.CommitMarriageSettlement(before, Cancelled(before, now), mail);
        await PublishMember(before.FirstId, after);
        await PublishMember(before.SecondId, after);
    }

    async Task PublishMember(ushort id, MarriageRecord record)
    {
        try { await projection.PublishMemberAsync(id, record); }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // 账本已经提交；一方断线不回滚结算，也不能阻断另一方或后续婚礼。
            log.Write("marriage_projection_disconnected", new { record.Id, Character = id, Error = error.Message });
        }
    }

    internal static uint SpouseShare(uint gift, int chargePercent)
    {
        if (chargePercent is < 0 or > 100) throw new InvalidDataException("礼金手续费无效");
        // 官方资料规定先扣除手续费并去小数，再平分新人；平分余数不额外增发。
        ulong net = (ulong)gift * checked((uint)(100 - chargePercent)) / 100;
        return checked((uint)(net / 2));
    }

    IReadOnlyList<MailboxDelivery> SuccessMail(MarriageRecord record, long now)
    {
        var wedding = record.Wedding!;
        List<MailboxDelivery> mail = [];
        foreach (var gift in wedding.Gifts.Where(row => !row.Settled))
        {
            uint share = SpouseShare(gift.Gold, catalog.MoneyGiftChargeRaw);
            foreach (ushort spouse in new[] { record.FirstId, record.SecondId })
                mail.Add(Delivery(record, $"gift:{gift.Id:N}:{spouse}", spouse, share, [], now,
                    string.IsNullOrWhiteSpace(gift.Text) ? "婚礼礼金扣除手续费后平分。" : $"{gift.Name}：{gift.Text}"));
            mail.Add(Delivery(record, $"guest:{gift.Id:N}", gift.CharacterId, 0,
                MarriageWeddingRules.GuestGift(catalog, wedding.Type, gift.Gold), now, "感谢参加婚礼，祝福回礼请查收。"));
        }
        if (catalog.WeddingGifts.TryGetValue(wedding.Type, out var reward))
            foreach (ushort spouse in new[] { record.FirstId, record.SecondId })
                mail.Add(Delivery(record, $"complete:{spouse}", spouse, 0, [reward], now, "婚礼举行成功，祝新婚快乐。"));
        return mail;
    }

    MailboxDelivery Delivery(MarriageRecord record, string key, ushort id, uint gold,
        IReadOnlyList<MarriageItemCount> rewards, long now, string text)
    {
        var recipient = store.FindMarriageRecipient(id) ?? throw new InvalidOperationException("婚礼邮件收件人不存在");
        var attachments = ImmutableArray.CreateBuilder<InventoryItem>();
        foreach (var reward in rewards)
        {
            var template = items.Get(reward.ItemId);
            int remaining = reward.Count;
            while (remaining > 0)
            {
                int count = Math.Min(remaining, template.StackLimit);
                attachments.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0),
                    InventoryRewardGrantService.Create(template, count, DateTimeOffset.FromUnixTimeSeconds(now))));
                remaining -= count;
            }
        }
        return new($"marriage:{record.Id:N}:{record.Wedding!.Id:N}:{key}", recipient, "结婚系统", text, gold, attachments.ToImmutable(), now);
    }

    async Task BeginMovie(MarriageRecord record)
    {
        var wedding = record.Wedding!;
        var wire = projection.Wire(record);
        var members = wedding.Invitations.Select(row => row.CharacterId).Append(record.FirstId).Append(record.SecondId).Distinct();
        foreach (ushort id in members)
        {
            if (online.FindConnectionByCharacterId(id) is not { Marriage: { } transport } peer || !InHall(peer, wedding) ||
                peer.Session.CaptureMarriage() is not { } capture) continue;
            try
            {
                await transport.Notify(1010, MarriageWeddingPackets.CeremonyBegin(record.ContextId, wedding.HallArea, wire.FirstName, wire.SecondName),
                    "marriage_ceremony_begin", () => peer.Session.MatchesMarriage(capture, true) && InHall(peer, wedding));
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                log.Write("marriage_movie_disconnected", new { record.Id, Character = id, Error = error.Message });
            }
        }
    }
}
