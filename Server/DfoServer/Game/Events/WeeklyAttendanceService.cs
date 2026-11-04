using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.OnlineEvents;

namespace DfoServer.Game.Events;

internal sealed record WeeklyAttendancePlan(WeeklyAttendanceSeason Season, DateTimeOffset At, bool Claim, bool Bonus,
    ImmutableArray<ImmutableArray<InventoryItem>> Attachments);

internal sealed class WeeklyAttendanceService(IWeeklyAttendanceStore store, WeeklyAttendanceCatalog catalog,
    ItemCatalog items, ServerEventService events, GameDebugSettings settings, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    WeeklyAttendanceSeason? frozenSeason = store.ReadWeeklyAttendanceSeason();
    public DateTimeOffset Now => clock.GetUtcNow();
    public bool Enabled => events.Read(ServerEventService.WeeklyAttendanceId).Active &&
        (Volatile.Read(ref frozenSeason) is not { } season || season.Week(Now) >= 0);

    public WeeklyAttendancePlan? Prepare(AccountOwners.Lease owner, Character character, bool claim = false, bool bonus = false)
    {
        if (!Enabled) return null;
        var now = Now;
        var season = store.ActivateWeeklyAttendance(owner, character, now, catalog);
        Volatile.Write(ref frozenSeason, season);
        var state = store.ReadWeeklyAttendance(owner, season, catalog);
        var rewards = state.Claim(catalog, season, now, claim, bonus);
        var attachments = rewards.Select(Prepare).ToImmutableArray();
        return new(season, now, claim, bonus, attachments);

        ImmutableArray<InventoryItem> Prepare(WeeklyAttendanceReward reward)
        {
            var template = items.Get(reward.ItemId);
            var result = ImmutableArray.CreateBuilder<InventoryItem>();
            for (int remaining = reward.Count; remaining > 0;)
            {
                if (result.Count == 10) throw new WeeklyAttendanceRejectedException("周签到奖励超过邮件附件上限");
                int amount = Math.Min(remaining, template.StackLimit);
                var core = InventoryRewardGrantService.Create(template, amount, now,
                    shopExpiration: settings.IgnoreCashShopAbsoluteExpiration, ignoreUsablePeriod: settings.IgnoreCashShopUsablePeriod);
                if (core.IsExpired(now.ToUnixTimeSeconds())) throw new WeeklyAttendanceRejectedException("周签到奖励已过期");
                result.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core));
                remaining -= amount;
            }
            return result.ToImmutable();
        }
    }
    public WeeklyAttendanceCommit Commit(AccountOwners.Lease owner, Character character, WeeklyAttendancePlan plan) =>
        store.CommitWeeklyAttendance(owner, character, plan.Season, plan.At, catalog, plan.Claim, plan.Bonus, plan.Attachments);
}
