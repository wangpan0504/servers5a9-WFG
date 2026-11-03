using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.OnlineEvents;

namespace DfoServer.Game.Events;

internal sealed class PcRoomTimePointService(IPcRoomTimePointStore store, PcRoomTimePointCatalog catalog,
    ItemCatalog items, ServerEventService events, GameDebugSettings debugSettings, TimeProvider? time = null)
{
    public PcRoomTimePointCatalog Catalog { get; } = catalog;
    public ServerEventService Events => events;
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public PcRoomTimePointClock StartClock() => new(events, clock);
    public PcRoomTimePointSnapshot Read(AccountOwners.Lease owner, DateTimeOffset at) =>
        store.Read(owner, PcRoomTimePointDay.At(at), Catalog);

    public PcRoomTimePointCommit Commit(AccountOwners.Lease owner, Character character,
        PcRoomTimePointClockReading reading, PcRoomTimePointClaim? claim = null,
        ImmutableArray<InventoryItem> attachments = default) => store.Update(owner, character, reading.SegmentId,
            reading.Totals, reading.At, Catalog, claim, attachments);

    // 模板和实例规划在账号锁外完成；领取事务只消费这次已冻结的期限政策和完整附件。
    public ImmutableArray<InventoryItem> Prepare(PcRoomTimePointClaim claim, DateTimeOffset now)
    {
        int itemId, count;
        if (claim.Kind == 0 && claim.Index < Catalog.Daily.Length)
        {
            var reward = Catalog.Daily[claim.Index];
            itemId = reward.ItemId; count = reward.Count;
        }
        else if (claim.Kind == 1 && claim.Index < Catalog.Period.Length)
        {
            var reward = Catalog.Period[claim.Index];
            itemId = reward.ItemId; count = reward.Count;
        }
        else throw new PcRoomTimePointClaimRejectedException("累计在线奖励档位不存在");
        bool ignoreAbsolute = debugSettings.IgnoreCashShopAbsoluteExpiration;
        bool ignoreUsable = debugSettings.IgnoreCashShopUsablePeriod;
        var template = items.Get(itemId);
        var result = ImmutableArray.CreateBuilder<InventoryItem>();
        while (count > 0)
        {
            if (result.Count == 10) throw new PcRoomTimePointClaimRejectedException("累计在线奖励超过邮件附件上限");
            int amount = Math.Min(count, template.StackLimit);
            var core = InventoryRewardGrantService.Create(template, amount, now,
                shopExpiration: ignoreAbsolute, ignoreUsablePeriod: ignoreUsable);
            if (core.IsExpired(now.ToUnixTimeSeconds()))
                throw new PcRoomTimePointClaimRejectedException("累计在线奖励已过期");
            result.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core));
            count -= amount;
        }
        return result.ToImmutable();
    }

    public DateTimeOffset NextWake(PcRoomTimePointClockReading reading, PcRoomTimePointSnapshot snapshot)
    {
        var wake = reading.At.AddSeconds(30);
        var boundary = PcRoomTimePointDay.NextBoundary(reading.At);
        if (boundary < wake) wake = boundary;
        if (reading.Enabled)
        {
            long milliseconds = (long)snapshot.Seconds * 1000;
            var next = Catalog.Daily.FirstOrDefault(row => row.RequiredMilliseconds > milliseconds);
            if (next is not null)
            {
                var threshold = reading.At.AddMilliseconds(next.RequiredMilliseconds - milliseconds);
                if (threshold < wake) wake = threshold;
            }
        }
        return wake;
    }
}
