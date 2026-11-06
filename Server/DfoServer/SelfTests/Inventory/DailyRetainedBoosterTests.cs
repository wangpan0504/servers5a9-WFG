using System.Collections.Immutable;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class DailyRetainedBoosterTests
{
    const int Source = 490704515, Reward = 490704522;
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        // 选择未来的06:00边界前一秒，避免真实选角把15天源期限的固定历史夹具当作已过期物品清理。
        var now = DailyBoosterUsage.NextReset(DateTimeOffset.UtcNow).AddDays(1).AddSeconds(-1);
        var sourceTemplate = resources.Items.Get(Source);
        var source = new InventoryItem(1, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(sourceTemplate, utcNow: now));
        var character = new Character(1, 0, "DailyRule", 0, towns.Room, towns.InitialReturn) { Inventory = new([source]) };
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        var createdReward = InventoryRewardGrantService.Create(resources.Items.Get(Reward), 10, now);
        That(createdReward.ExpireTime == DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds(),
            "真实522每日奖励不永久化，统一Create写入下一服务日截止");
        DailyItemExpirationTests.RunResources(towns, resources);
        WeeklyItemExpirationTests.RunResources(towns, resources);
        var first = service.Open(character, new(65, 1), out var receipt, now);
        That(first.Accepted && first.Plan!.After.Find(source.Position) == source && receipt!.Rewards is [{ ItemId: Reward, Count: 10 }] &&
            first.Plan.After.Items.Where(item => item.Core.ItemId == Reward).Sum(item => item.Core.Count) == 10 &&
            first.Plan.After.Items.Where(item => item.Core.ItemId == Reward).All(item => item.Core.ExpireTime == createdReward.ExpireTime),
            "每日首次完整给十份奖励且源UID、槽、Core、期限及数量完全不变");
        var used = character with { Inventory = first.Plan!.After };
        That(service.Open(used, new(65, 1), out var denied, now) is { Accepted: false, Error: 19, Plan: null } && denied is null,
            "同模板当日第二次拒绝且没有任何奖励规划");
        var moved = used with { Inventory = used.Inventory with { Items = used.Inventory.Items.Select(item => item == source ? item with
            { Uid = 123, Position = new(InventoryListType.Main, 4) } : item).ToImmutableArray() } };
        That(!service.Open(moved, new(4, 1), out _, now).Accepted && !service.Open(character, new(65, 2), out _, now).Accepted,
            "换槽换实例不绕模板次数，批量不能超过源数量和日额度");
        var full = character with { Inventory = new(Enumerable.Range(65, 56).Select(slot => source with
            { Uid = slot, Position = new(InventoryListType.Main, (short)slot) }).ToImmutableArray()) };
        That(service.Open(full, new(65, 1), out _, now) is { Accepted: false, Plan: null } && full.Inventory.DailyBoosterUses.IsEmpty,
            "满包不能借源格，发奖失败不增加次数");
        foreach (var template in new[] { sourceTemplate with { UseForbidden = true }, sourceTemplate with { MinimumLevel = 2 },
            sourceTemplate with { MaximumLevel = 0 }, sourceTemplate with { RequiredSkill = 123 } })
        {
            try
            {
                resources.Items.PrefillTemplates([new(Source, template)]);
                That(!service.Open(character, new(65, 1), out _, now).Accepted, "完整源资格拒绝禁用、最小/最大等级及技能不符");
            }
            finally { resources.Items.PrefillTemplates([new(Source, sourceTemplate)]); }
        }
        foreach (var core in new[] { source.Core with { EquipmentLockId = 1 }, source.Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } })
            That(!service.Open(character with { Inventory = new([source with { Core = core }]) }, new(65, 1), out _, now).Accepted,
                "源锁定或恰到期拒绝且无规划");
        That(first.Plan.CharacterPrecondition is { } condition && condition.Matches(character, now.ToUnixTimeSeconds()) &&
            !condition.Matches(character, now.AddSeconds(1).ToUnixTimeSeconds()) &&
            !condition.Matches(character with { Level = 2 }, now.ToUnixTimeSeconds()), "跨日截止严格复验且角色资格快照变化拒绝");
        var expiring = character with { Inventory = new([source with { Core = source.Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } }]) };
        That(!service.Open(expiring, new(65, 1), out _, now).Accepted, "源期限不能被每日政策放宽");
        foreach (var location in new[] { towns.Room, towns.InitialReturn, new Location(0, 0, 0, 0) })
            That(service.Open(character with { Position = location }, new(65, 1), out _, now).Accepted,
                "515未声明地点限制，不把合法场景任意收窄为Town");
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resources.Boosters)!;
        var actualDefinition = resources.Boosters.Get(Source);
        try
        {
            resources.Items.PrefillTemplates([new(Source, sourceTemplate with { StackLimit = 255 })]);
            definitions[Source] = actualDefinition with { DailyRetainedLimit = 2 };
            var batchCharacter = character with { Inventory = new([source with { Core = source.Core with { Value = 2 } }]) };
            var batch = service.Open(batchCharacter, new(65, 2), out var batchReceipt, now);
            That(batch.Accepted && batch.Plan!.After.Find(source.Position)!.Core.Count == 2 &&
                batch.Plan.After.DailyBoosterUses is [{ Used: 2 }] && batchReceipt!.Rewards is [{ ItemId: Reward, Count: 20 }],
                "合法批量次数与完整奖励乘数一致，不能只记一次却发多次");
            definitions[Source] = new([new(1, [new(1, 100, 1)])]) { DailyRetainedLimit = 1 };
            var walletFull = character with { Inventory = character.Inventory with { ReviveCoins = int.MaxValue } };
            That(service.Open(walletFull, new(65, 1), out _, now) is { Accepted: false, Plan: null } && walletFull.Inventory.DailyBoosterUses.IsEmpty,
                "钱包上限失败保留源与次数，不截断奖励");
        }
        finally
        {
            definitions[Source] = actualDefinition;
            resources.Items.PrefillTemplates([new(Source, sourceTemplate)]);
        }
        var earlier = now.AddMinutes(-1);
        var shortSource = source with { Core = source.Core with { ExpireTime = (int)earlier.AddSeconds(30).ToUnixTimeSeconds() } };
        var shortCharacter = character with { Inventory = new([shortSource]) };
        var shortPlan = service.Open(shortCharacter, new(65, 1), out _, earlier).Plan!;
        That(shortPlan.CharacterPrecondition!.Matches(shortCharacter, earlier.AddSeconds(29).ToUnixTimeSeconds()) &&
            !shortPlan.CharacterPrecondition.Matches(shortCharacter, earlier.AddSeconds(30).ToUnixTimeSeconds()),
            "源期限早于跨日时取真正最早截止，不延长源有效期");
        Persistence(towns, resources, now);
        Console.WriteLine($"PASS {Count - checks} daily retained domain and persistence assertions");
    }

    static void Persistence(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "daily-retained-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("daily-retained", () => { });
            repository.Create(owner, new("DailyKeep", towns.Room, towns.InitialReturn) { Inventory = new([new(0,
                new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.Items.Get(Source), utcNow: now))]) });
            // 建源、选角清理及开箱资格统一使用本轮虚拟时间，不能让真实日期先删除历史源。
            var clock = new DailyBoosterTestClock(now);
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            var before = session.Selected!;
            That(before.Inventory.Find(new(InventoryListType.Main, 65)) is { Core.ItemId: Source },
                "同一虚拟时钟选角后保留真实每日源实例");
            string Snapshot() => JsonSerializer.Serialize(repository.List(owner));
            void Sql(string sql) { using var db = new SqliteConnection("Data Source=" + path); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces, clock: clock);
            string snapshot = Snapshot();
            var midnight = service.Open(before, new(65, 1), out _);
            That(midnight.Accepted && midnight.Plan is not null,
                $"跨日持久化前必须成功规划完整每日奖励，实际{midnight.Error}/{midnight.RejectReason}");
            var midnightPlan = midnight.Plan!;
            clock.Advance(TimeSpan.FromSeconds(1));
            bool expired = false;
            try { repository.CommitInventory(owner, before, midnightPlan); }
            catch (InvalidOperationException) { expired = true; }
            That(expired && Snapshot() == snapshot, "规划到真实提交跨六点边界拒绝，读取可前进时钟而非冻结now");
            Sql("CREATE TRIGGER fail_daily_reward BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=66 BEGIN SELECT RAISE(ABORT,'daily rollback'); END");
            bool rejected = false;
            try { session.MutateInventory(role => service.Open(role, new(65, 1), out _)); }
            catch (SqliteException) { rejected = true; }
            That(rejected && Snapshot() == snapshot && session.Selected == before, "后续奖励SQL失败回滚次数、源、奖励、revision与在线快照");
            Sql("DROP TRIGGER fail_daily_reward");
            That(session.MutateInventory(role => service.Open(role, new(65, 1), out _)).Accepted, "每日源保留与奖励及次数同事务提交");
            var saved = session.Selected!;
            var recovered = new CharacterRepository(path).List(owner).Single();
            That(saved.Revision == before.Revision + 1 && recovered.Inventory.Find(new(InventoryListType.Main, 65)) == before.Inventory.Find(new(InventoryListType.Main, 65)) &&
                recovered.Inventory.DailyBoosterUses is [{ ItemId: Source, Used: 1 }] && JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(recovered),
                "重开恢复完整奖励与次数，同一revision保留原源实例");
            bool stale = false;
            try { repository.CommitInventory(owner, before, new(before.Inventory, saved.Inventory, "daily_stale")); }
            catch (InvalidOperationException) { stale = true; }
            That(stale, "旧revision不能重放每日计划");
            var ledgerOnly = saved.Inventory.DailyBoosterUses.Add(new(Source + 1, ShopPurchaseCount.Today(clock.GetUtcNow()), 1));
            var countChanged = repository.CommitInventory(owner, saved,
                new(saved.Inventory, saved.Inventory with { DailyBoosterUses = ledgerOnly }, "daily_ledger_only"));
            That(countChanged.Revision == saved.Revision + 1 && countChanged.Inventory.Items.SequenceEqual(saved.Inventory.Items),
                "只改次数也持久化并递增revision，原资产完整保留");
            var semanticBefore = countChanged.Inventory with { DailyBoosterUses = ledgerOnly.Reverse().ToImmutableArray() };
            var unchanged = repository.CommitInventory(owner, countChanged, new(semanticBefore, semanticBefore, "daily_semantic"));
            That(unchanged.Revision == countChanged.Revision, "反序数组按账本语义复验，不产生虚假变化");
            Sql("UPDATE characters SET daily_booster_usage='[{\"ItemId\":490704515,\"Day\":1,\"Used\":0}]'");
            bool malformed = false;
            try { repository.List(owner); }
            catch (InvalidDataException) { malformed = true; }
            That(malformed, "有效JSON中的坏账本拒绝读取，不清零恢复额度");
            Sql("UPDATE characters SET daily_booster_usage='[]'");
            bool ledgerStale = false;
            try { repository.CommitInventory(owner, countChanged, new(countChanged.Inventory, countChanged.Inventory with { Gold = countChanged.Inventory.Gold + 1 }, "daily_stale_ledger")); }
            catch (InvalidOperationException) { ledgerStale = true; }
            That(ledgerStale, "即使revision未变，账本被外部修改也拒绝旧计划");
            using var replacement = owners.Acquire("daily-retained", () => { });
            bool generation = false;
            try { repository.CommitInventory(owner, countChanged, new(countChanged.Inventory, countChanged.Inventory, "daily_stale_owner")); }
            catch (InvalidOperationException) { generation = true; }
            That(generation, "旧owner generation不能再次写次数或奖励");
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
    }
}
