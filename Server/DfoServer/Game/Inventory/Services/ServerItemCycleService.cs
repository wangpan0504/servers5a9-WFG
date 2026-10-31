using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class ServerItemCycleService(ServerItemUsageCatalog rules, ItemCatalog items, GoldLimitCatalog gold,
    EpicPieceCatalog pieces, TimeProvider? clock = null, MarriageDungeonDailySupply? marriageSupply = null)
{
    public ServerItemUsageCatalog Rules { get; } = rules;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public DateTimeOffset NextWake(DateTimeOffset now)
    {
        var next = FatigueDailyReset.NextReset(now, Clock.LocalTimeZone);
        foreach (var reset in Rules.Resets)
        {
            var due = DfoServer.GameWorld.Time.ServiceDay.At(reset.Until, Clock.LocalTimeZone);
            if (due > now && due < next) next = due;
        }
        return next;
    }

    public InventoryResult ApplyUsage(Character character, InventoryResult result, byte action, int itemId, int count)
    {
        if (!result.Accepted || result.Plan is not { } plan) return result;
        var now=Clock.GetUtcNow();
        if (!ServerItemUsageState.TryConsume(plan.After.ServerItems, Rules, action, itemId, count,
            now, Clock.LocalTimeZone, out var state)) return InventoryResult.Reject(19) with { RejectReason = "server-item-usage-limit" };
        if (state.Same(plan.After.ServerItems)) return result;
        long deadline = FatigueDailyReset.NextReset(now, Clock.LocalTimeZone).ToUnixTimeSeconds();
        if (plan.CharacterPrecondition?.QualificationExpiresAt is { } existing) deadline = Math.Min(deadline, existing);
        return result with { Plan = plan.WithAfter(plan.After with { ServerItems = state }, new(character, deadline, Clock, requirePosition: true)) };
    }

    public bool NeedsAccountCheck(Character character, bool onSelection) => !Rules.AccountRefills.IsEmpty &&
        (onSelection || character.Inventory.ServerItems.PendingAccountRefills ||
            character.Inventory.ServerItems.RefillDay < FatigueDailyReset.Day(Clock.GetUtcNow(), Clock.LocalTimeZone));

    public InventoryResult Maintain(Character character, IReadOnlyDictionary<int, AccountItemRefillState>? accountDays, bool onSelection = false)
    {
        var now = Clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, Clock.LocalTimeZone).DateTime;
        int day = FatigueDailyReset.Day(now, Clock.LocalTimeZone);
        var before = character.Inventory; var state = before.ServerItems;
        var marriageRefill = marriageSupply?.Eligible(character) == true
            ? new ServerItemRefill(marriageSupply.ItemId, marriageSupply.Count, DateTime.MaxValue) : null;
        if (day < state.RefillDay) return new(true, 0, new(before, before, "server_item_clock_backwards"));
        bool daily = day > state.RefillDay;
        bool pendingRefill = marriageRefill is { } wedding && !state.Refills.Any(saved => saved.ItemId == wedding.ItemId && saved.Day >= day) ||
            Rules.Refills.Any(row => row.Count > 0 && local < row.Until && character.Level >= row.MinimumLevel &&
            !state.Refills.Any(saved => saved.ItemId == row.ItemId && saved.Day >= day)) ||
            Rules.AccountRefills.Any(row => local < row.Until && character.Level >= row.MinimumLevel &&
                accountDays?.GetValueOrDefault(row.ItemId) is { Completed: false } saved && saved.Day == day && saved.CharacterId == character.Id);
        bool scheduled = Rules.Resets.Any(row => row.Until <= local &&
            DfoServer.GameWorld.Time.ServiceDay.At(row.Until, Clock.LocalTimeZone).ToUnixTimeSeconds() > state.LastSweepAt);
        if (!daily && !onSelection && !scheduled && !pendingRefill) return new(true, 0, new(before, before, "server_item_cycle_unchanged"));
        var reset = Rules.Resets.Where(row => row.Until <= local).Select(row => row.ItemId).ToHashSet();
        if (daily) reset.UnionWith(Rules.Refills.Where(row => local < row.Until && row.Count == 0 && character.Level >= row.MinimumLevel).Select(row => row.ItemId));
        var after = before with { Items = before.Items.Where(item => !reset.Contains(item.Core.ItemId)).ToImmutableArray() };
        var accounts = ImmutableArray.CreateBuilder<AccountItemRefillClaim>();
        var refillDays = state.Refills;
        void Refill(ServerItemRefill rule, bool account, bool onlyTopUp = false)
        {
            var previous = accountDays?.GetValueOrDefault(rule.ItemId);
            if (local >= rule.Until || character.Level < rule.MinimumLevel ||
                account && previous is { } saved && (saved.Day > day || saved.Day == day && (saved.Completed || saved.CharacterId != character.Id)) ||
                !account && refillDays.Any(saved => saved.ItemId == rule.ItemId && saved.Day >= day)) return;
            bool success = true;
            if (account && rule.Count == 0)
            {
                reset.Add(rule.ItemId);
                after = after with { Items = after.Items.Where(item => item.Core.ItemId != rule.ItemId).ToImmutableArray() };
            }
            else if (rule.Count > 0 && !reset.Contains(rule.ItemId))
            {
                var source = after.Items.FirstOrDefault(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == rule.ItemId);
                var template = items.Get(rule.ItemId);
                if (onlyTopUp)
                {
                    long owned = after.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == rule.ItemId)
                        .Sum(item => (long)item.Core.Count);
                    int needed = checked((int)Math.Max(0, rule.Count - owned));
                    var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = after },
                        needed == 0 ? [] : [new(rule.ItemId, needed)], items, gold, pieces, now, allowOverflowMail: false);
                    if (!grant.Accepted) success = false;
                    else after = grant.Plan!.After;
                }
                else if (source is { Core.IsStackable: true })
                {
                    if (rule.Count > template.StackLimit) success = false;
                    // 数量赋值而非累加；原UID、期限和完整核心保持不变。
                    else after = after with { Items = after.Items.Replace(source, source with { Core = source.Core with { Value = rule.Count } }) };
                }
                else
                {
                    var owned = after.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == rule.ItemId)
                        .OrderBy(item => item.Position.Index).ToArray();
                    var proposed = owned.Length > rule.Count ? after with { Items = after.Items.RemoveRange(owned.Skip(rule.Count)) } : after;
                    int needed = Math.Max(0, rule.Count - owned.Length);
                    var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = proposed }, needed == 0 ? [] : [new(rule.ItemId, needed)],
                        items, gold, pieces, now, allowOverflowMail: false);
                    if (!grant.Accepted) success = false;
                    else after = grant.Plan!.After;
                }
            }
            if (account)
            {
                var target = new AccountItemRefillState(day, character.Id, success);
                if (target != previous) accounts.Add(new(rule.ItemId, previous, target));
            }
            else if (success) refillDays = refillDays.Where(saved => saved.ItemId != rule.ItemId)
                .Append(new(rule.ItemId, day)).OrderBy(saved => saved.ItemId).ToImmutableArray();
        }
        foreach (var refill in Rules.Refills)
            if (marriageRefill?.ItemId != refill.ItemId) Refill(refill, false);
        if (marriageRefill is not null) Refill(marriageRefill, false, onlyTopUp: true);
        if (accountDays is not null) foreach (var refill in Rules.AccountRefills) Refill(refill, true);
        var mails = state.Mails;
        var deliveries = ImmutableArray.CreateBuilder<MailboxDelivery>();
        foreach (var mail in Rules.Mails)
        {
            var previous = mails.SingleOrDefault(row => row.ItemId == mail.ItemId);
            if (local >= mail.Until || character.Level < mail.MinimumLevel || mail.MaximumLevel > 0 && character.Level > mail.MaximumLevel ||
                previous is not null && day - previous.Day < mail.IntervalDays) continue;
            var template = items.Get(mail.ItemId);
            var attachments = ImmutableArray.CreateBuilder<InventoryItem>();
            bool stackable = new ItemCore(template.Id, template.Kind, 1).IsStackable;
            for (int left = mail.Count; left > 0;)
            {
                int count = stackable ? Math.Min(left, template.StackLimit) : 1;
                attachments.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0),
                    InventoryRewardGrantService.Create(template, count, now)));
                left -= count;
                if (attachments.Count > 1000) throw new InvalidDataException("单条每日邮件附件超过资源预算");
            }
            int ordinal = 0;
            foreach (var chunk in attachments.ToImmutable().Chunk(10))
                deliveries.Add(new($"server-daily-item:{character.Id}:{mail.ItemId}:{day}:{mail.SubjectCode}:{mail.MessageCode}:{ordinal++}",
                new(character.Id, "", character.Name, character.Job, character.GrowType, character.Level),
                "每日补给", $"每日补给：{template.Name}", 0, chunk.ToImmutableArray(), now.ToUnixTimeSeconds(),
                Math.Min(now.AddDays(15).ToUnixTimeSeconds(), DfoServer.GameWorld.Time.ServiceDay.At(mail.Until, Clock.LocalTimeZone).ToUnixTimeSeconds())));
            mails = mails.Where(row => row.ItemId != mail.ItemId).Append(new(mail.ItemId, day)).OrderBy(row => row.ItemId).ToImmutableArray();
        }
        // 每个补给单独保存成功日戳，满包重试不会再次补充已经成功、随后又被使用的另一种物品。
        bool pendingAccount = accountDays is null ? state.PendingAccountRefills : Rules.AccountRefills.Any(rule =>
            local < rule.Until && (accounts.FirstOrDefault(claim => claim.ItemId == rule.ItemId)?.After ?? accountDays.GetValueOrDefault(rule.ItemId)) is
                { Completed: false } saved && saved.Day == day && saved.CharacterId == character.Id);
        state = state with { RefillDay = day, Refills = refillDays, Mails = mails, PendingAccountRefills = pendingAccount,
            LastSweepAt = scheduled ? now.ToUnixTimeSeconds() : state.LastSweepAt };
        after = after with { ServerItems = state };
        long deadline = FatigueDailyReset.NextReset(now, Clock.LocalTimeZone).ToUnixTimeSeconds();
        foreach (var until in Rules.Refills.Concat(Rules.AccountRefills).Select(row => row.Until).Concat(Rules.Mails.Select(row => row.Until)))
        {
            long seconds = DfoServer.GameWorld.Time.ServiceDay.At(until, Clock.LocalTimeZone).ToUnixTimeSeconds();
            if (seconds > now.ToUnixTimeSeconds()) deadline = Math.Min(deadline, seconds);
        }
        return new(true, 0, new(before, after, "server_item_cycle", new(character, deadline, Clock, requireMarriageState: marriageRefill is not null))
        { AccountRefillClaims = accounts.ToImmutable(), DailyItemMails = deliveries.ToImmutable(), ResetItemIds = reset.ToImmutableArray() });
    }
}
