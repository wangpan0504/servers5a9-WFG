using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.MonsterMockBattle;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.MonsterMockBattle;

internal sealed record MockBattlePlan(MockBattleAccount Before, MockBattleMutation Mutation,
    InventoryPlan Inventory, ServerEventSchedule Schedule, DateTimeOffset At)
{
    public MailboxDelivery? Mail { get; init; }
}
internal sealed record MockBattleCommit(MockBattleAccount Account, Character Character);
internal sealed record MockBattleOpponent(string Account, string Name, MockBattleAccount State);

internal interface IMonsterMockBattleStore
{
    MockBattleAccount? ReadMockBattle(AccountOwners.Lease owner);
    MockBattleCommit CommitMockBattle(AccountOwners.Lease owner, Character expected, MockBattlePlan plan);
    ImmutableArray<MockBattleOpponent> FindMockBattleOpponents(string account, string? name, int stars, int limit, string? after);
}

internal sealed class MonsterMockBattleService(IMonsterMockBattleStore store, MonsterMockBattleCatalog catalog,
    MockBattleSettings? settings, ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    ServerEventService events, TimeProvider? time = null, Func<int, int>? random = null,
    DfoServer.Game.Settings.GameDebugSettings? debugSettings = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly Func<int, int> draw = random ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32;
    public MonsterMockBattleCatalog Catalog => catalog;
    public bool Configured => settings is not null;
    public DateTimeOffset Now => clock.GetUtcNow();
    public bool Enabled => Configured && events.Read(ServerEventService.NolanVisibilityId).Active;
    public InventoryResult ProtectShop(InventoryResult result)
    {
        if (!Configured || !result.Accepted || result.Plan is not { } plan) return result;
        var state = events.Read(ServerEventService.NolanVisibilityId);
        return !state.Active ? InventoryResult.Reject(17) : result with
        { Plan = plan.WithAfter(plan.After, requiredEvent: new(state.Schedule, clock)) };
    }
    public bool ContainsDungeon(uint dungeon) => catalog.Worlds.Values.Any(row => row.DungeonId == dungeon);

    public const int CoinItemId = 490013685;
    public const int RewardMaterialItemId = 490013686;
    public MockBattleAccount Read(AccountOwners.Lease owner, Character character)
    {
        var account = store.ReadMockBattle(owner) ?? MockBattleAccount.Create(Now, character.Name);
        return Wallet(account.Recover(Now), character);
    }

    static MockBattleAccount Wallet(MockBattleAccount account, Character character) => account with
    {
        Snapshot = account.Snapshot with { Parameters6 = account.Snapshot.Parameters6.SetItem(0,
            checked((uint)(character.Inventory.AccountWallet.Materials.GetValueOrDefault(CoinItemId)?.Balance ?? 0))) }
    };

    InventoryPlan CoinChange(Character character, MockBattleAccount before, MockBattleAccount after, DateTimeOffset at,
        InventorySnapshot? inventory = null)
    {
        var original = inventory ?? character.Inventory;
        long delta = (long)after.Coins - before.Coins;
        if (delta == 0) return new(character.Inventory, original, "monster_mock_battle");
        var changed = delta < 0 ? InventoryMaterialConsumptionService.Plan(original,
            [new(CoinItemId, checked((int)-delta))], pieces, at) :
            InventoryRewardGrantService.TryGrantBatch(character with { Inventory = original },
                [new(CoinItemId, checked((int)delta))], items, goldLimits, pieces, at);
        if (!changed.Accepted) throw new MockBattleRejectedException("怪物硬币不足或超过材料余额上限");
        return new(character.Inventory, changed.Plan!.After, "monster_mock_battle");
    }

    public MockBattlePlan? PrepareRefresh(AccountOwners.Lease owner, Character character)
    {
        var schedule = events.Read(ServerEventService.NolanVisibilityId);
        if (!Configured || !schedule.Active) return null;
        var at = Now;
        var before = Wallet(store.ReadMockBattle(owner) ?? MockBattleAccount.Create(at, character.Name), character);
        var after = before.NeedsSupplyInitialization ? before with
        {
            HeartAt = at.ToUnixTimeSeconds(),
            Snapshot = before.Snapshot with { Parameters6 = before.Snapshot.Parameters6.SetItem(1, MockBattleAccount.HeartLimit).SetItem(4, 0) }
        } : before.Recover(at);
        after = after with { SupplyInitialized = true };
        int day = DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(8)).DateTime.AddHours(-6)).DayNumber;
        if (before.CoinMailDay < day)
        {
            // 沿用旧日邮件日戳，已发邮件的当天不再重复直发硬币。
            after = after.WithCoins(checked(after.Coins + MockBattleAccount.DailyCoins)) with { CoinMailDay = day };
        }
        // 满额只读时会归一计时起点，留到实际扣费再保存，避免每次投影都写库。
        if (before.Revision > 0 && before.SupplyInitialized &&
            before.Hearts == after.Hearts && before.CoinMailDay == after.CoinMailDay) return null;
        return new(before, new(after, []), new(character.Inventory, CoinChange(character, before, after, at).After, "monster_mock_battle_supply")
            { RequiredEvent = new(schedule.Schedule, clock) }, schedule.Schedule, at);
    }

    MailboxDelivery RewardMail(AccountOwners.Lease owner, Character character, IEnumerable<MockBattleItemReward> rewards,
        string key, string text, DateTimeOffset at)
    {
        var attachments = ImmutableArray.CreateBuilder<InventoryItem>();
        foreach (var reward in rewards)
        {
            var template = items.Get(reward.ItemId);
            for (int remaining = reward.Count; remaining > 0;)
            {
                if (attachments.Count == 10) throw new InvalidDataException("怪物模拟战奖励超过邮件附件上限");
                int count = Math.Min(remaining, template.StackLimit);
                var core = InventoryRewardGrantService.Create(template, count, at,
                    shopExpiration: debugSettings?.IgnoreCashShopAbsoluteExpiration == true,
                    ignoreUsablePeriod: debugSettings?.IgnoreCashShopUsablePeriod == true);
                attachments.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core));
                remaining -= count;
            }
        }
        return new(key, new(character.Id, owner.Account, character.Name, character.Job, character.GrowType, character.Level),
            "诺兰", text, 0, attachments.ToImmutable(), at.ToUnixTimeSeconds(), at.AddDays(15).ToUnixTimeSeconds());
    }

    public MockBattlePlan Prepare(AccountOwners.Lease owner, Character character, uint operation, uint argument0,
        uint argument1, ReadOnlySpan<byte> payload)
    {
        var at = Now;
        var schedule = events.Read(ServerEventService.NolanVisibilityId);
        if (!schedule.Active) throw new MockBattleRejectedException("活动已关闭");
        var before = Read(owner, character);
        if (before.Revision == 0) throw new MockBattleRejectedException("活动卡册尚未初始化");
        var mutation = MockBattleRules.Apply(catalog, before with { DisplayName = character.Name },
            operation, argument0, argument1, payload, draw);
        var inventory = character.Inventory;
        if (mutation.ConsumedItem != 0)
        {
            var consume = InventoryMaterialConsumptionService.Plan(inventory, [new(mutation.ConsumedItem, 1)], pieces, at);
            if (!consume.Accepted) throw new MockBattleRejectedException("卡券不足、已锁定或过期");
            inventory = consume.Plan!.After;
        }
        MailboxDelivery? mail = null;
        if (mutation.Reward is { } reward)
        {
            if (reward.ItemId == CoinItemId)
                mutation = mutation with { After = mutation.After.WithCoins(checked(mutation.After.Coins + (uint)reward.Count)) };
            else mail = RewardMail(owner, character, [reward],
                $"monster-mock-battle-achievement:{owner.Account}:{argument0}", "阿拉德谋略战成就奖励。", at);
        }
        inventory = CoinChange(character, before, mutation.After, at, inventory).After;
        var plan = new InventoryPlan(character.Inventory, inventory, "monster_mock_battle")
        {
            RequiredEvent = new(schedule.Schedule, clock)
        };
        return new(before, mutation, plan, schedule.Schedule, at) { Mail = mail };
    }

    public MockBattleCommit Commit(AccountOwners.Lease owner, Character expected, MockBattlePlan plan) =>
        store.CommitMockBattle(owner, expected, plan);

    public ImmutableArray<MockBattleOpponent> Opponents(AccountOwners.Lease owner, string? name, int stars, string? after) =>
        store.FindMockBattleOpponents(owner.Account, name, stars, name is null ? 3 : 1, after);

    public MockBattleAdmission PrepareAdmission(AccountOwners.Lease owner, Character character, uint dungeon,
        MockBattleOpponent? opponent)
    {
        if (!Enabled) throw new MockBattleRejectedException("活动未开启");
        var at = Now;
        var world = catalog.Worlds.Values.SingleOrDefault(row => row.DungeonId == dungeon)
            ?? throw new MockBattleRejectedException("活动关卡不存在");
        if (world.Mode is 2 or 3) throw new MockBattleRejectedException("玩家对战暂未开放");
        var schedule = events.Read(ServerEventService.NolanVisibilityId).Schedule;
        var before = Read(owner, character);
        if (before.Revision == 0) throw new MockBattleRejectedException("活动卡册尚未初始化");
        var lineup = before.Snapshot.Cards.Where(row => !row.IsEmpty && row.Position >= 0).ToImmutableArray();
        if (lineup.Length is < 1 or > 5 || lineup.Any(row => !catalog.Cards.ContainsKey(row.CardId)))
            throw new MockBattleRejectedException("上阵卡片不足或来源无效");
        bool first = world.Mode == 0 && before.Snapshot.Worlds[world.Id].Completed == 0;
        if (world.Mode == 0 && world.Id > 0 && before.Snapshot.Worlds[world.Id - 1].Completed == 0)
            throw new MockBattleRejectedException("前置关卡尚未完成");
        if (world.Mode == 1)
        {
            // 原生0047B8E0将0..20共21项装入[+208]，0073E2D0对world 20直接按体力放行按钮，仅对21..26检查[+21C]==20；
            // 且0047C2B0在本地日期未命中2020绝对日历时固定回退[+1E4]=20（异次元裂缝首关18021）。
            if (world.Id != 20 && before.Snapshot.Worlds[19].Completed == 0)
                throw new MockBattleRejectedException("特殊关卡需要先通关第20关");
            var rawDate = DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(8)).DateTime.AddHours(-6));
            var date = settings?.SpecialCalendarStart is { } start
                ? catalog.SpecialDays[0].Date.AddDays(rawDate.DayNumber - start.DayNumber)
                : rawDate;
            bool scheduled = catalog.SpecialDays.Any(row => row.Date == date && row.World == world.Id);
            bool fallback = world.Id == 20 &&
                (!catalog.SpecialDays.Any(row => row.Date == date) || !catalog.SpecialDays.Any(row => row.Date == rawDate));
            if (!scheduled && !fallback)
                throw new MockBattleRejectedException("特殊关卡未到开放日期");
        }
        if ((world.Mode is 2 or 3) != (opponent is not null) || opponent?.Account == owner.Account)
            throw new MockBattleRejectedException("匹配对手与当前模式不符");
        uint cost = checked((uint)(world.Mode == 2 ? catalog.PvpParameters[0] : first ? world.FirstHeartCost : world.RepeatHeartCost));
        if (before.Hearts < cost) throw new MockBattleRejectedException("活动体力不足");
        var paid = (before with { Snapshot = before.Snapshot with
        { Parameters6 = before.Snapshot.Parameters6.SetItem(1, before.Hearts - cost) } }).Recover(at);
        return new(world, before, paid, lineup, opponent, schedule, at, catalog);
    }

    public MockBattleVictory PrepareVictory(AccountOwners.Lease owner, Character character, DungeonRunIdentity identity,
        MockBattleAdmission admission)
    {
        var before = Read(owner, character);
        if (before.Revision != admission.Before.Revision + 1)
            throw new MockBattleRejectedException("战斗期间卡册版本已改变");
        var state = before.Snapshot;
        var world = admission.World;
        bool first = world.Mode == 0 && state.Worlds[world.Id].Completed == 0;
        var rewards = world.Mode is 0 or 1 ? first ? world.FirstReward : world.RepeatReward : new MockBattleRewards([], 0);
        uint coins = checked((uint)rewards.Coins);
        var after = before;
        if (world.Mode is 0 or 1)
        {
            int index = world.Mode == 0 ? world.Id : 20;
            var progress = state.Worlds[index];
            state = state with { Worlds = state.Worlds.SetItem(index, new(checked(progress.Value + 1), 1)) };
        }
        if (world.Mode == 2 && admission.Opponent is { } opponent)
        {
            int stars = opponent.State.Snapshot.Cards.Where(row => row.Position >= 0).Sum(row => row.Stars);
            coins = checked((uint)(catalog.PvpParameters[1] * stars));
            state = state with { Parameters6 = state.Parameters6.SetItem(5, checked(state.Parameters6[5] + 1)) };
        }
        after = after with { Snapshot = state };
        if (world.Mode == 0) after = MockBattleRules.Advance(catalog, after, 2, world.DungeonId, 1);
        if (world.Mode == 1) after = MockBattleRules.Advance(catalog, after, 7, 0, 1);
        if (world.Mode == 2) after = MockBattleRules.Advance(catalog, after, 3, 0, 1);
        var mailRewards = ImmutableArray.CreateBuilder<MockBattleItemReward>();
        foreach (var reward in rewards.Items)
        {
            if (reward.ItemId == CoinItemId) coins = checked(coins + (uint)reward.Count);
            else mailRewards.Add(reward);
        }
        after = after.WithCoins(checked(after.Coins + coins));
        if (world.Mode == 2 && catalog.PvpParameters[2] > 0) mailRewards.Add(new(RewardMaterialItemId, catalog.PvpParameters[2]));
        var at = Now;
        MailboxDelivery? mail = mailRewards.Count == 0 ? null : RewardMail(owner, character, mailRewards,
            "monster-mock-battle:" + identity.InstanceId.ToString("N"), "阿拉德谋略战通关奖励。", at);
        return new(admission, before, after, mail, CoinChange(character, before, after, at));
    }
}
