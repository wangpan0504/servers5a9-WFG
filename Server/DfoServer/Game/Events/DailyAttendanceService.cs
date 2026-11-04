using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.OnlineEvents;

namespace DfoServer.Game.Events;

internal sealed record DailyAttendanceSnapshot(int Day, int Clears, int Total, byte Claimed);
internal sealed record DailyAttendancePlan(DailyAttendanceSnapshot Before, DailyAttendanceCatalog Catalog,
    long ActivityRevision, DateTimeOffset At, DailyAttendanceReward? Reward, ImmutableArray<InventoryItem> Attachments);
internal sealed record DailyAttendanceCommit(DailyAttendanceSnapshot Snapshot, uint MailId = 0);

internal interface IDailyAttendanceStore
{
    DailyAttendanceSnapshot ReadDailyAttendance(AccountOwners.Lease owner, int day);
    DailyAttendanceCommit ClaimDailyAttendance(AccountOwners.Lease owner, Character expected, DailyAttendancePlan plan);
}

internal sealed class DailyAttendanceService(IDailyAttendanceStore store, DailyAttendanceCatalog catalog,
    ItemCatalog items, ServerEventService events, GameDebugSettings debugSettings, TimeProvider? time = null)
{
    public DailyAttendanceCatalog Catalog => catalog;
    public ServerEventService Events => events;
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public static bool IsRecommended(DungeonEntryPlan entry, DungeonDefinition definition, int level) =>
        !entry.IsPractice && !entry.IsTutorial && !entry.IsClassExperience && !entry.IsTrainingRoom &&
        !entry.IsQuestReplay && entry.LostLand is null && entry.DungeonId != 5000 &&
        DungeonLuckyStarPolicy.IsRecommendedClear(definition, entry.QuestId, entry.RouteQuestId, level);
    public DailyAttendanceSnapshot Read(AccountOwners.Lease owner) =>
        store.ReadDailyAttendance(owner, PcRoomTimePointDay.At(clock.GetUtcNow()));

    public DailyAttendancePlan? Prepare(AccountOwners.Lease owner, bool claim)
    {
        var at = clock.GetUtcNow();
        var state = events.Read(ServerEventService.DailyAttendanceId);
        if (!state.Active) return null;
        var before = store.ReadDailyAttendance(owner, PcRoomTimePointDay.At(at));
        DailyAttendanceReward? reward = claim
            ? catalog.Accumulated.FirstOrDefault(row => before.Total >= row.RequiredDays && (before.Claimed & (1 << row.Index)) == 0)
            : before.Total < catalog.Daily.Length && before.Clears == DailyAttendanceCatalog.ClearTarget - 1
                ? catalog.Daily[before.Total] : null;
        if (claim && reward is null || !claim && (before.Total >= catalog.Daily.Length || before.Clears >= DailyAttendanceCatalog.ClearTarget))
            return null;
        var attachments = ImmutableArray.CreateBuilder<InventoryItem>();
        if (reward is not null)
        {
            var template = items.Get(reward.ItemId);
            int remaining = reward.Count;
            while (remaining > 0)
            {
                if (attachments.Count == 10) throw new InvalidDataException("日签到奖励超过邮件附件上限");
                int count = Math.Min(remaining, template.StackLimit);
                var core = InventoryRewardGrantService.Create(template, count, at,
                    shopExpiration: debugSettings.IgnoreCashShopAbsoluteExpiration,
                    ignoreUsablePeriod: debugSettings.IgnoreCashShopUsablePeriod);
                if (core.IsExpired(at.ToUnixTimeSeconds())) throw new InvalidDataException("日签到奖励已过期");
                attachments.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core));
                remaining -= count;
            }
        }
        return new(before, catalog, state.Schedule.Revision, at, reward, attachments.ToImmutable());
    }

    public DailyAttendanceCommit Claim(AccountOwners.Lease owner, Character character, DailyAttendancePlan plan) =>
        store.ClaimDailyAttendance(owner, character, plan);
}
