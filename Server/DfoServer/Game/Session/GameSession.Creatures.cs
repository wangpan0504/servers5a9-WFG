using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record CreatureFeedUse(ConsumableUseRequest Request, long SelectionEpoch, long Timestamp, int CoolTime);
    CreatureFeedUse? creatureFeedUse;
    sealed class CreatureClock(ushort characterId, long epoch, long uid, CreatureDetail detail, long timestamp, long frequency)
    {
        public Guid CycleId { get; } = Guid.NewGuid();
        public ushort CharacterId { get; } = characterId;
        public long Epoch { get; } = epoch;
        public long Uid { get; } = uid;
        public int BaseAmount { get; set; } = detail.Satiety * 36000 + detail.SatietyFraction;
        public long Timestamp { get; set; } = timestamp;
        public long DueTimestamp { get; set; } = checked(timestamp + 60 * frequency);
        public long Units { get; set; }
        public bool Fighting { get; set; }
        public int FoodRate { get; set; }
    }
    CreatureClock? creatureClock;

    public CreatureClockTicket? PrepareCreatureTick(CreatureService service)
    {
        var selected = owner.Execute(() => Ready ? Selected : null);
        var item = selected is null ? null : service.Equipped(selected, service.Clock.GetUtcNow().ToUnixTimeSeconds());
        bool itemGrowth = item is not null && service.Catalog.Get(item.Core.ItemId)?.UsesExperienceItems == true;
        int foodRate = selected is null ? 0 : service.FoodConsumptionRate(selected, service.Clock.GetUtcNow().ToUnixTimeSeconds());
        return owner.Execute(() =>
        {
            if (!Ready || Selected is null || !ReferenceEquals(Selected, selected)) return null;
            if (itemGrowth || item?.Creature is not { Mode: 0 } detail)
            {
                creatureClock = null;
                return (CreatureClockTicket?)null;
            }
            long now = service.Clock.GetTimestamp(), frequency = service.Clock.TimestampFrequency;
            if (creatureClock is not { } clock || clock.CharacterId != Selected.Id || clock.Epoch != SelectionEpoch || clock.Uid != item.Uid)
                creatureClock = clock = new(Selected.Id, SelectionEpoch, item.Uid, detail, now, frequency);
            int amount = detail.Satiety * 36000 + detail.SatietyFraction;
            if (amount != clock.BaseAmount)
            {
                clock.BaseAmount = amount;
                clock.Units = 0;
                clock.Timestamp = now;
            }
            long seconds = Math.Max(0, (long)service.Clock.GetElapsedTime(clock.Timestamp, now).TotalSeconds);
            if (seconds > 0)
            {
                long elapsed = Math.Min(seconds, 36000);
                clock.Units = Math.Clamp(clock.BaseAmount + clock.Units + (clock.Fighting ? -elapsed * 6 * (100L + clock.FoodRate) : elapsed * 100),
                    0, 3600000) - clock.BaseAmount;
                clock.Timestamp = seconds > 36000 ? now : checked(clock.Timestamp + seconds * frequency);
            }
            // 首次进图加载沿用城镇休息，过房加载沿用战斗；不在冻结的加载阶段写入角色资产。
            if (Phase != SessionPhase.Loading) clock.Fighting = Phase == SessionPhase.Dungeon && formalDungeon?.Entry.HasFreeCombatConsumption != true;
            clock.FoodRate = foodRate;
            bool revive = Phase == SessionPhase.Town && clock.BaseAmount + clock.Units < 36000;
            return new CreatureClockTicket(clock.CycleId, clock.CharacterId, clock.Epoch, clock.Uid, clock.DueTimestamp, clock.Units, revive);
        });
    }

    public InventoryResult SettleCreatureTick(CreatureClockTicket expected, CreatureService service, bool force = false)
    {
        var next = PrepareCreatureTick(service);
        if (next is null || next.CycleId != expected.CycleId || !force && !next.Revive && service.Clock.GetTimestamp() < next.DueTimestamp)
            return InventoryResult.Reject(123);
        bool Current() => creatureClock?.CycleId == next.CycleId && SelectionEpoch == next.SelectionEpoch &&
            Selected?.Id == next.CharacterId && Selected.Inventory.Find(CreatureRules.EquippedSlot)?.Uid == next.Uid;
        var result = PlanAndCommitInventory(character => service.ShiftSatiety(character, next.Units, next.Revive,
            service.Clock.GetUtcNow().ToUnixTimeSeconds()), 123, requireTown: false, allowSettlement: false, additionalGuard: Current);
        if (result.Accepted) owner.Execute(() =>
        {
            if (Current() && creatureClock is { } clock && Selected!.Inventory.Find(CreatureRules.EquippedSlot)?.Creature is { } detail)
            {
                clock.BaseAmount = detail.Satiety * 36000 + detail.SatietyFraction;
                clock.Units = 0;
                clock.DueTimestamp = checked(service.Clock.GetTimestamp() + 60 * service.Clock.TimestampFrequency);
            }
            return true;
        });
        return result;
    }

    public ConsumableUseResult FeedCreature(ConsumableUseRequest request, CreatureService service, bool activityBlocked = false)
    {
        var captured = owner.Execute(() => (Guard: UseGuard(), Character: CanUseConsumable() ? Selected : null));
        ConsumableUseResult Reject(string reason) => new(ConsumableUseOutcome.Rejected, reason, Guard: captured.Guard);
        if (RecentCreatureFeed(request, service)) return new(ConsumableUseOutcome.CloseConnection, "creature-ambiguous-repeat");
        if (activityBlocked || captured.Character is not { } character ||
            !service.RecognizesFeed(character, request.ItemId, service.Clock.GetUtcNow().ToUnixTimeSeconds()))
            return Reject("invalid-state-or-feed");
        var feed = service.Catalog.Feed(request.ItemId);
        int coolTime = feed?.CoolTime ?? service.Catalog.GrowthFood(request.ItemId)?.CoolTime ??
            service.Catalog.ExperienceItem(request.ItemId)?.CoolTime ?? 0;
        var planned = service.Feed(character, request, service.Clock.GetUtcNow().ToUnixTimeSeconds());
        return owner.Execute(() =>
        {
            if (!CanPublishConsumable(captured.Guard) || !CanUseConsumable())
                return new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "creature-state-changed");
            if (creatureFeedUse is { } previous && previous.SelectionEpoch == SelectionEpoch &&
                service.Clock.GetElapsedTime(previous.Timestamp) < TimeSpan.FromMilliseconds(previous.CoolTime))
                return previous.Request == request ? new ConsumableUseResult(ConsumableUseOutcome.CloseConnection, "creature-ambiguous-repeat") :
                    Reject("creature-feed-cooldown");
            if (!planned.Accepted) return Reject("creature-feed-source");
            var source = character.Inventory.Find(request.Position)!;
            if (source.IsExpired(service.Clock.GetUtcNow().ToUnixTimeSeconds())) return Reject("creature-feed-expired");
            var committed = CommitInventory(character, planned);
            creatureFeedUse = new(request, SelectionEpoch, service.Clock.GetTimestamp(), coolTime);
            return new ConsumableUseResult(ConsumableUseOutcome.Committed, "creature-fed", committed, UseGuard()) { SourceUid = source.Uid };
        });
    }

    public bool RecentCreatureFeed(ConsumableUseRequest request, CreatureService service) => owner.Execute(() =>
        creatureFeedUse is { } previous && previous.SelectionEpoch == SelectionEpoch && previous.Request == request &&
        service.Clock.GetElapsedTime(previous.Timestamp) < TimeSpan.FromMilliseconds(previous.CoolTime));
}
