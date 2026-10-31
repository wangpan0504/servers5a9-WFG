using System.Collections.Immutable;
using DfoServer.GameWorld.Marriage;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Marriage;

internal readonly record struct MarriageWeddingCost(uint Gold, MarriageItemCount Item);
internal readonly record struct MarriageWeddingChange(MarriageRecord Record, MarriageWeddingCost Cost);
internal readonly record struct MarriageWeddingRefund(ushort CharacterId, uint Gold, MarriageItemCount Item, Guid GiftId);
internal readonly record struct MarriageWeddingEnd(MarriageRecord Record, ImmutableArray<MarriageWeddingRefund> Principal);

internal static class MarriageWeddingRules
{
    public const byte Engaged = 1;
    public const byte Reserved = 2;
    public const byte InCeremony = 3;
    public const byte Married = 4;

    public static bool EligibleProposal(ushort firstId, int firstLevel, int firstJob, bool firstAvailable,
        ushort secondId, int secondLevel, int secondJob, bool secondAvailable, int minimumLevel) =>
        firstId != 0 && secondId != 0 && firstId != secondId && firstAvailable && secondAvailable &&
        minimumLevel > 0 && firstLevel >= minimumLevel && secondLevel >= minimumLevel &&
        firstJob >= 0 && secondJob >= 0;

    public static MarriageRecord CreateEngagement(Guid id, uint contextId, ushort firstId, ushort secondId, long now)
    {
        if (id == Guid.Empty || contextId == 0 || firstId == 0 || secondId == 0 || firstId == secondId || now < 0)
            throw new ArgumentException("婚约身份或时间无效");
        return new(id, contextId, 1, firstId, secondId, Engaged)
        {
            CreatedAt = now,
            Members = ImmutableDictionary<ushort, MarriageMemberState>.Empty
                .Add(firstId, new()).Add(secondId, new())
        };
    }

    public static MarriageWeddingChange Reserve(MarriageRecord record, MarriageCatalog catalog, Guid weddingId,
        ushort payerId, int type, int selectionIndex, long now, long startsAt, bool prepared)
    {
        Member(record, payerId);
        if (record.State == Reserved || record.Wedding is { State: 1 })
            throw new MarriageRuleException(MarriageFailureReason.AlreadyReserved, "婚礼已预约");
        if (!prepared) throw new MarriageRuleException(MarriageFailureReason.TasksIncomplete, "双方结婚任务未完成");
        if (!DayIntervalElapsed(record.LastWeddingAt, now, catalog.Timing.OnceHowManyDays))
            throw new MarriageRuleException(MarriageFailureReason.WeddingDailyLimit, "当日已申请举办婚礼");
        if (record.State != Engaged || weddingId == Guid.Empty ||
            now < 0 || startsAt < now || !catalog.Ceremonies.TryGetValue(type, out var definition) ||
            (uint)selectionIndex >= (uint)catalog.Timing.SelectionSeconds.Length)
            throw new InvalidOperationException("当前婚约不能预约该婚礼");
        int seconds = catalog.Timing.SelectionSeconds[selectionIndex];
        var ticket = catalog.ChargedWeddingTickets.GetValueOrDefault(type);
        var wedding = new MarriageWeddingState
        {
            Id = weddingId, Type = type, PayerId = payerId,
            PaidGold = checked((uint)definition.Gold), PaidTicketId = ticket.ItemId, PaidTicketCount = ticket.Count,
            // 选择秒数是预约到开场的等待；通知接收容差独立于电影时长。
            ReservedAt = now, StartsAt = startsAt, EndsAt = checked(startsAt + catalog.Timing.NotifyEndSeconds),
            State = 1, SelectionSeconds = seconds,
            InviteRemaining = ImmutableDictionary<ushort, int>.Empty
                .Add(record.FirstId, definition.LettersPerPersonRaw).Add(record.SecondId, definition.LettersPerPersonRaw)
        };
        return new(Next(record) with { State = Reserved, Wedding = wedding, LastWeddingAt = now }, new(wedding.PaidGold, ticket));
    }

    public static bool CanEngageAgain(MarriageRecord? latest, MarriageCatalog catalog, long now) =>
        latest is null || !latest.Active && DayIntervalElapsed(latest.EndedAt, now, catalog.Timing.DaysAfterDivorce);

    static bool DayIntervalElapsed(long previous, long now, int minimumDays)
    {
        if (previous == 0) return true;
        if (now < previous) return false;
        int Day(long value)
        {
            int date = QuestCycle.Day(DateTimeOffset.FromUnixTimeSeconds(value));
            return new DateOnly(date / 10000, date / 100 % 100, date % 100).DayNumber;
        }
        return Day(now) - Day(previous) >= minimumDays;
    }

    public static MarriageWeddingChange UpgradeRing(MarriageRecord record, MarriageCatalog catalog,
        ushort memberId, byte requestedLevel, int suppliedItemId)
    {
        if (record.State != Married || !record.IsMember(memberId))
            throw new MarriageRuleException(MarriageFailureReason.NotMarried, "只有已婚成员能升级婚戒");
        Member(record, memberId);
        if (requestedLevel != record.RingLevel + 1 ||
            !catalog.Rings.ContainsKey(requestedLevel) || requestedLevel >= catalog.UpgradeRingTickets.Length)
            throw new MarriageRuleException(MarriageFailureReason.InvalidRingLevel, "婚戒不能越级或超过资源上限");
        var ticket = catalog.UpgradeRingTickets[requestedLevel];
        if (ticket.ItemId <= 0 || ticket.Count <= 0 || suppliedItemId != ticket.ItemId)
            throw new MarriageRuleException(MarriageFailureReason.MissingItem, "婚戒升级券不匹配");
        // 券的金币属于商店售价，升级只消耗资源声明的券，不能再扣一次金币。
        return new(Next(record) with { RingLevel = requestedLevel }, new(0, ticket));
    }

    public static MarriageWeddingChange UseLovePointItem(MarriageRecord record, MarriageCatalog catalog,
        ushort memberId, int suppliedItemId)
    {
        if (record.State != Married || !record.IsMember(memberId))
            throw new MarriageRuleException(MarriageFailureReason.NotMarried, "只有已婚成员能使用心意道具");
        var member = Member(record, memberId);
        if (!catalog.LovePoints.ItemPoints.TryGetValue(suppliedItemId, out int points) || points <= 0 || member.LovePoints < 0)
            throw new InvalidOperationException("爱心道具不适用");
        if (points > catalog.LovePoints.Max - member.LovePoints)
            throw new MarriageRuleException(MarriageFailureReason.LovePointLimit, "心意点已达到上限");
        var next = Next(record) with
        {
            Members = record.Members.SetItem(memberId, member with { LovePoints = member.LovePoints + points })
        };
        return new(next, new(0, new(suppliedItemId, 1)));
    }

    // 日标识由现有业务日时钟提供；此处不另立时区或以每24小时替代日切。
    public static MarriageRecord GrantDailyLove(MarriageRecord record, MarriageCatalog catalog, ushort memberId, int day)
    {
        var member = Member(record, memberId);
        if (day <= 0 || day <= member.LoveDay || catalog.LovePoints.DailyRaw == 0)
            return record;
        int points = (int)Math.Min(catalog.LovePoints.Max, (long)member.LovePoints + catalog.LovePoints.DailyRaw);
        return Next(record) with { Members = record.Members.SetItem(memberId, member with { LovePoints = points, LoveDay = day }) };
    }

    public static MarriageRecord AddGift(MarriageRecord record, MarriageCatalog catalog, MarriageGuestGift gift, long now)
    {
        var wedding = ActiveWedding(record, now);
        if (now >= wedding.StartsAt)
            throw new MarriageRuleException(MarriageFailureReason.GiftClosed, "婚礼已开始，不能再送礼金");
        if (wedding.Gifts.Any(row => row.CharacterId == gift.CharacterId))
            throw new MarriageRuleException(MarriageFailureReason.AlreadyGifted, "该来宾已送过礼金");
        if (gift.Gold < catalog.MoneyGiftMin)
            throw new MarriageRuleException(MarriageFailureReason.GiftTooSmall, "礼金低于资源下限");
        if (gift.Gold > catalog.MoneyGiftMax)
            throw new MarriageRuleException(MarriageFailureReason.GiftTooLarge, "礼金超过资源上限");
        if (gift.Text is null || gift.Text.Any(char.IsControl) ||
            DfoServer.Game.Characters.CharacterName.Encoding.GetByteCount(gift.Text) > 255 ||
            gift.Id == Guid.Empty || gift.CharacterId == 0 || gift.Settled || gift.Gold < catalog.MoneyGiftMin ||
            gift.Gold > catalog.MoneyGiftMax || wedding.Gifts.Any(row => row.Id == gift.Id || row.CharacterId == gift.CharacterId))
            throw new InvalidOperationException("婚礼礼金无效或已提交");
        return Next(record) with { Wedding = wedding with { Gifts = wedding.Gifts.Add(gift with { SentAt = now }) } };
    }

    public static ImmutableArray<MarriageItemCount> GuestGift(MarriageCatalog catalog, int weddingType, uint gold)
    {
        if (gold < catalog.MoneyGiftMin || gold > catalog.MoneyGiftMax || !catalog.Ceremonies.ContainsKey(weddingType))
            throw new InvalidOperationException("礼金或婚礼类型超出资源范围");
        foreach (var tier in catalog.GuestGiftTiers)
            if (gold <= tier.UpperBound && tier.GiftsByWeddingType.TryGetValue(weddingType, out var items)) return items;
        throw new InvalidOperationException("礼金奖励没有匹配档位");
    }

    public static MarriageRecord SendInvitation(MarriageRecord record, ushort senderId, ushort targetId, string targetName, long now)
    {
        Member(record, senderId);
        var wedding = ActiveWedding(record, now);
        if (record.IsMember(targetId))
            throw new MarriageRuleException(MarriageFailureReason.InvalidInvitationTarget, "婚礼举办者无需请帖");
        if (wedding.Invitations.Any(row => row.CharacterId == targetId))
            throw new MarriageRuleException(MarriageFailureReason.DuplicateInvitation, "已向该来宾发过请帖");
        if (!wedding.InviteRemaining.TryGetValue(senderId, out int remaining) || remaining <= 0)
            throw new MarriageRuleException(MarriageFailureReason.NoInvitations, "剩余请帖不足");
        if (now >= wedding.StartsAt || targetId == 0 || string.IsNullOrWhiteSpace(targetName))
            throw new InvalidOperationException("婚礼邀请时机或目标无效");
        var invitation = new MarriageInvitation
        { CharacterId = targetId, Name = targetName, State = 1, SentAt = now, InviterId = senderId };
        return Next(record) with
        {
            Wedding = wedding with
            {
                Invitations = wedding.Invitations.Add(invitation),
                InviteRemaining = wedding.InviteRemaining.SetItem(senderId, remaining - 1)
            }
        };
    }

    public static MarriageRecord ClaimCeremonyGifts(MarriageRecord record, ushort guestId, long now)
    {
        var wedding = ActiveWedding(record, now);
        if (now >= wedding.StartsAt || record.IsMember(guestId) ||
            !wedding.Gifts.Any(row => row.CharacterId == guestId && !row.Settled))
            throw new InvalidOperationException("来宾尚不能领取婚礼入场礼");
        int index = -1;
        for (int i = 0; i < wedding.Invitations.Length; i++)
        {
            if (wedding.Invitations[i].CharacterId != guestId) continue;
            index = i;
            break;
        }
        if (index < 0) throw new InvalidOperationException("来宾没有本场婚礼请帖");
        var invitation = wedding.Invitations[index];
        if (invitation.CeremonyGiftsReceived) return record;
        return Next(record) with
        {
            Wedding = wedding with
            {
                Invitations = wedding.Invitations.SetItem(index, invitation with { CeremonyGiftsReceived = true })
            }
        };
    }

    public static MarriageRecord CompleteCeremony(MarriageRecord record, long now)
    {
        var wedding = ActiveWedding(record, now);
        if (record.State is not (Reserved or InCeremony) || now < wedding.StartsAt)
            throw new InvalidOperationException("婚礼尚不能完成");
        return Next(record) with { State = Married, Wedding = wedding with { State = 2 } };
    }

    public static uint StageEndFee(MarriageRecord record, MarriageCatalog catalog) => record.State switch
    {
        Engaged or Reserved or InCeremony => checked((uint)catalog.AlimonyRaw[0]),
        Married => checked((uint)catalog.AlimonyRaw[1]),
        _ => throw new InvalidOperationException("不存在可解除的婚姻关系")
    };

    // 此账单只描述未结清本金；返还方式、礼金分账与税费必须在外层已有事务中明确处理。
    public static ImmutableArray<MarriageWeddingRefund> RefundableLedger(MarriageRecord record)
    {
        if (!record.Active || record.Wedding is not { } wedding) return [];
        var result = ImmutableArray.CreateBuilder<MarriageWeddingRefund>();
        if (wedding.State == 1 && (wedding.PaidGold > 0 || wedding.PaidTicketCount > 0))
            result.Add(new(wedding.PayerId, wedding.PaidGold, new(wedding.PaidTicketId, wedding.PaidTicketCount), Guid.Empty));
        foreach (var gift in wedding.Gifts.Where(row => !row.Settled))
            result.Add(new(gift.CharacterId, gift.Gold, default, gift.Id));
        return result.ToImmutable();
    }

    public static MarriageWeddingEnd EndRelationship(MarriageRecord record, long now)
    {
        if (!record.Active || now < record.CreatedAt) throw new InvalidOperationException("婚姻关系已经解除或时间无效");
        var ledger = RefundableLedger(record);
        var wedding = record.Wedding;
        if (wedding is not null) wedding = wedding with
        { State = 3, Gifts = wedding.Gifts.Select(row => row with { Settled = true }).ToImmutableArray() };
        return new(Next(record) with
        {
            State = 0, EndedAt = now, RingLevel = 0, Wedding = wedding, Tree = null,
            Furniture = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty,
            Storage = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty,
            Members = record.Members.ToImmutableDictionary(row => row.Key, _ => new MarriageMemberState())
        }, ledger);
    }

    static MarriageRecord Next(MarriageRecord record) => record with { Revision = checked(record.Revision + 1) };

    static MarriageMemberState Member(MarriageRecord record, ushort id)
    {
        if (!record.Active || !record.IsMember(id) || !record.Members.TryGetValue(id, out var member))
            throw new InvalidOperationException("角色不属于当前婚姻关系");
        return member;
    }

    static MarriageWeddingState ActiveWedding(MarriageRecord record, long now)
    {
        if (!record.Active || record.Wedding is not { State: 1 } wedding || now < wedding.ReservedAt || now >= wedding.EndsAt)
            throw new MarriageRuleException(MarriageFailureReason.InvalidWedding, "婚礼不在有效预约期内");
        return wedding;
    }
}
