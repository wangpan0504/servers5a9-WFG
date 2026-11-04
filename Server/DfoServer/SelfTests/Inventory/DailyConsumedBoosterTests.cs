using System.Collections.Immutable;
using System.Text.Json;
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

internal static class DailyConsumedBoosterTests
{
    const int InvitationBox = 490000524, Invitation = 3330, HeartBox = 10008893;

    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var now = DailyBoosterUsage.NextReset(DateTimeOffset.UtcNow).AddDays(1).AddSeconds(-1);
        var invitation = resources.Boosters.Get(InvitationBox);
        var heart = resources.Boosters.Get(HeartBox);
        That(invitation is { Available: true, DailyConsumedLimit: 1, DailyUseLimit: 1, DailyRetainedLimit: null } &&
            invitation.Pools is [{ Draws: 1, Rewards: [{ ItemId: Invitation, Count: 100, Weight: 100000 }] }],
            "真实每日邀请函盒保留原奖池，日额度独立于源保留政策");
        (int ItemId, long Weight, int Count)[] heartRows =
        [
            (1, 3600, 1), (1, 1800, 2), (1, 1200, 3),
            (15, 1500, 1), (15, 750, 2), (15, 500, 3),
            (14, 400, 1), (14, 150, 2), (14, 100, 3)
        ];
        That(heart is { Available: true, DailyConsumedLimit: 1, DailyUseLimit: 1, DailyRetainedLimit: null } &&
            heart.Pools.Length == 2 && heart.Pools.All(pool => pool.Draws == 1) &&
            heart.Pools[0].Rewards.Select(row => (row.ItemId, row.Weight, row.Count)).SequenceEqual(heartRows) &&
            heart.Pools[1].Rewards is [{ ItemId: HeartBox, Count: 1, Weight: 1000 }],
            "真实爱心盒九行原权重及独立必返自身池全部保留，不改为留源");
        var retained = resources.Boosters.Get(490704515);
        That(retained is { Available: true, DailyRetainedLimit: 1, DailyUseLimit: 1, DailyConsumedLimit: null },
            "旧515仍按原每日保留规则解析，不因耗源日盒新增而改变");

        var service = Service(resources);
        var current = Role(towns, resources, InvitationBox, 2, now);
        for (int day = 0; day < 2; day++)
        {
            var dayTime = now.AddDays(day);
            string before = JsonSerializer.Serialize(current);
            var opened = service.Open(current, new(65, 1), out var receipt, dayTime);
            That(opened.Accepted && opened.Plan is not null && receipt is { Rewards: [{ ItemId: Invitation, Count: 100 }] },
                "邀请函盒跨日每次只开一源并完整发100张邀请函：" + day);
            var after = opened.Plan!.After;
            That(Total(after, InvitationBox) == 1 - day && Total(after, Invitation) == 100 * (day + 1) &&
                after.DailyBoosterUses is [{ ItemId: InvitationBox, Used: 1 } uses] &&
                uses.Day == ShopPurchaseCount.Today(dayTime) &&
                after.Find(Bag(66)) == current.Inventory.Find(Bag(66)) &&
                JsonSerializer.Serialize(current) == before,
                "日额度随服务日更新，源2依次变1和0，原输入与哨兵不变");
            current = current with { Inventory = after };
            RejectOpen(service, current, 65, dayTime, "同日重放不能再扣源或发邀请函");
            if (day == 0)
            {
                var moved = current with { Inventory = after with { Items = after.Items.Select(item =>
                    item.Core.ItemId == InvitationBox ? item with { Uid = 9999, Position = Bag(4) } : item).ToImmutableArray() } };
                RejectOpen(service, moved, 4, dayTime, "换源UID和快捷栏位置不能恢复同模板日额度");
            }
        }
        for (byte job = 0; job < 16; job++)
            That(service.Open(Role(towns, resources, InvitationBox, 1, now) with { Job = job }, new(65, 1), out _, now).Accepted,
                "真实全职业声明允许合法职业开每日耗源盒：" + job);
        RejectOpen(service, Role(towns, resources, InvitationBox, 2, now), 65, now,
            "一日额度1不能批量消耗两盒", count: 2);

        long start = 0;
        foreach (var row in heartRows)
        {
            foreach (long roll in new[] { start, start + row.Weight - 1 })
            {
                var role = Role(towns, resources, HeartBox, 1, now);
                string before = JsonSerializer.Serialize(role);
                var heartService = Service(resources, new FixedRoll(roll));
                var result = heartService.Open(role, new(65, 1), out var receipt, now);
                That(result.Accepted && result.Plan is not null && receipt is not null,
                    $"爱心盒原奖行边界可完整规划：{row.ItemId}/{row.Count}/{roll}");
                var after = result.Plan!.After;
                That(receipt!.Rewards.OrderBy(reward => reward.ItemId).SequenceEqual(
                        new BoosterGrantedItem[] { new(row.ItemId, row.Count), new(HeartBox, 1) }.OrderBy(reward => reward.ItemId)) &&
                    Total(after, HeartBox) == 1 && after.DailyBoosterUses is [{ ItemId: HeartBox, Used: 1 }] &&
                    after.ReviveCoins == role.Inventory.ReviveCoins + (row.ItemId == 1 ? row.Count : 0) &&
                    (row.ItemId == 1 || Total(after, row.ItemId) == row.Count) &&
                    after.Gold == role.Inventory.Gold && after.WinPoints == role.Inventory.WinPoints &&
                    after.Find(Bag(66)) == role.Inventory.Find(Bag(66)) && JsonSerializer.Serialize(role) == before,
                    "九行各自边界发原数量且独立返一盒，源数量净0而不是增加一盒");
                var returned = after.Items.Single(item => item.Core.ItemId == HeartBox);
                RejectOpen(heartService, role with { Inventory = after }, returned.Position.Index, now,
                    "新返爱心盒仍共享原模板日账，不能同日循环发奖");
                if (roll == 0)
                    That(heartService.Open(role with { Inventory = after }, new(returned.Position.Index, 1), out _, now.AddDays(1)).Accepted,
                        "跨日后返还爱心盒可按新日额度重新开启");
            }
            start += row.Weight;
        }
        Capacity(towns, resources, now);
        Deadlines(towns, resources, now);
        Persistence(towns, resources, now);
        Console.WriteLine($"PASS {Count - checks} 每日耗源普通礼盒资源、领域与事务断言");
    }

    static void Capacity(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        var service = Service(resources);
        var invitation = Role(towns, resources, InvitationBox, 2, now);
        var target = resources.Items.Get(Invitation);
        var full = invitation with { Inventory = Filled(invitation.Inventory, target, InvitationBox, 2) };
        string before = JsonSerializer.Serialize(full);
        var rejected = service.Open(full, new(65, 1), out var receipt, now);
        That(!rejected.Accepted && rejected.Error == 4 && rejected.Plan is null && receipt is null &&
            JsonSerializer.Serialize(full) == before && full.Inventory.DailyBoosterUses.IsEmpty,
            "奖励真实分页及快捷栏全满时整笔拒绝，不扣源、日额度或截断奖励");

        var heart = Role(towns, resources, HeartBox, 1, now);
        var filler = resources.Items.Get(1106);
        That(filler.Kind == ItemKind.Consumable, "满消耗品页夹具使用真实普通消耗品");
        var packed = heart with { Inventory = Filled(heart.Inventory, filler, HeartBox, 1) };
        var walletBranch = Service(resources, new FixedRoll(0)).Open(packed, new(65, 1), out var walletReceipt, now);
        That(walletBranch.Accepted && walletBranch.Plan is not null && walletReceipt is not null &&
            walletBranch.Plan.After.Find(Bag(65)) is { Core.ItemId: HeartBox, Core.Count: 1 } &&
            walletBranch.Plan.After.ReviveCoins == packed.Inventory.ReviveCoins + 1 &&
            walletBranch.Plan.After.Items.Length == packed.Inventory.Items.Length &&
            walletBranch.Plan.After.DailyBoosterUses is [{ ItemId: HeartBox, Used: 1 }],
            "耗尽源先释放真实格，满页仍可在该格返一盒并完整记入钱包奖励与日额度");
        var twoSlots = Service(resources, new FixedRoll(6600)).Open(packed, new(65, 1), out var denied, now);
        That(!twoSlots.Accepted && twoSlots.Error == 4 && twoSlots.Plan is null && denied is null &&
            packed.Inventory.DailyBoosterUses.IsEmpty,
            "同一满页只有一个释放格，实物品调加自返盒需要两格时完整拒绝");

        InventorySnapshot Filled(InventorySnapshot original, ItemTemplate item, int sourceId, int sourceCount)
        {
            var range = ItemSlotBoundService.Range(item.Kind, original);
            var filled = Enumerable.Range(range.Start, range.End - range.Start + 1)
                .Concat(Enumerable.Range(ItemSlotBoundService.QuickSlotStart,
                    ItemSlotBoundService.QuickSlotEnd - ItemSlotBoundService.QuickSlotStart + 1))
                .Where(slot => slot != 65).Select(slot => new InventoryItem(slot + 100, Bag((short)slot),
                    InventoryRewardGrantService.Create(item, item.StackLimit, utcNow: now))).ToImmutableArray();
            return original with { Items = filled.Add(new(1, Bag(65), Core(resources, sourceId, sourceCount, now))) };
        }
    }

    static void Deadlines(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        var service = Service(resources);
        foreach (int id in new[] { InvitationBox, HeartBox })
        {
            var role = Role(towns, resources, id, 1, now);
            var opened = service.Open(role, new(65, 1), out _, now);
            That(opened.Accepted && opened.Plan?.CharacterPrecondition is { } condition &&
                condition.Matches(role, now.ToUnixTimeSeconds()) &&
                !condition.Matches(role, now.AddSeconds(1).ToUnixTimeSeconds()) &&
                !condition.Matches(role with { Level = 2 }, now.ToUnixTimeSeconds()),
                "耗源日盒也携带跨日截止与完整角色资格前提：" + id);
            var earlier = now.AddMinutes(-1);
            int expires = checked((int)earlier.AddSeconds(30).ToUnixTimeSeconds());
            var shortRole = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.SetItem(0,
                role.Inventory.Items[0] with { Core = role.Inventory.Items[0].Core with { ExpireTime = expires } }) } };
            var shortened = service.Open(shortRole, new(65, 1), out _, earlier);
            That(shortened.Accepted && shortened.Plan?.CharacterPrecondition is { } shortCondition &&
                shortCondition.Matches(shortRole, expires - 1) && !shortCondition.Matches(shortRole, expires),
                "源期限早于服务日边界时不得被日额度续期：" + id);
            RejectOpen(service, shortRole, 65, earlier.AddSeconds(30), "恰到源期仍拒绝开盒");
        }
    }

    static void Persistence(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "daily-consumed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            foreach (int id in new[] { InvitationBox, HeartBox })
            {
                using var owner = owners.Acquire("daily-consumed-" + id, () => { });
                var seed = Role(towns, resources, id, id == InvitationBox ? 2 : 1, now).Inventory;
                repository.Create(owner, new(id == InvitationBox ? "DailyInvite" : "DailyHeart", towns.Room, towns.InitialReturn)
                { Inventory = seed with { Items = seed.Items.Select(item => item with { Uid = 0 }).ToImmutableArray() } });
                var clock = new DailyBoosterTestClock(now);
                var session = new GameSession(repository, towns, owner, timeProvider: clock);
                session.Select(0); session.MarkReady();
                var before = session.Selected!;
                var service = Service(resources, new FixedRoll(0), clock);
                string original = Snapshot(owner);
                var crossing = service.Open(before, new(65, 1), out _);
                That(crossing.Accepted && crossing.Plan is not null, "真实耗源日盒先成功规划再验证提交跨日：" + id);
                clock.Advance(TimeSpan.FromSeconds(1));
                Reject<InventoryQualificationExpiredException>(() => repository.CommitInventory(owner, before, crossing.Plan!),
                    "跨六点旧日计划不能在新日写入源、奖励或次数");
                That(Snapshot(owner) == original, "跨日拒绝保持完整角色、审计与UID序列");

                var pending = service.Open(before, new(65, 1), out _);
                That(pending.Accepted && pending.Plan is not null, "新日按当前时钟重新规划每日耗源盒");
                int auditItem = id == InvitationBox ? Invitation : HeartBox;
                Query("CREATE TRIGGER fail_daily_consumed BEFORE INSERT ON inventory_audit_log " +
                    "WHEN NEW.action_name='booster_open' AND NEW.item_id_after=" + auditItem +
                    " BEGIN SELECT RAISE(ABORT,'daily consumed late audit'); END");
                Reject<SqliteException>(() => session.MutateInventory(_ => pending),
                    "奖励及角色更新后的末段库存审计故障必须传播");
                That(Snapshot(owner) == original && ReferenceEquals(session.Selected, before),
                    "晚审计故障完整回滚源、钱包、奖励、次数、revision、UID分配及在线角色");
                Query("DROP TRIGGER fail_daily_consumed");
                That(session.MutateInventory(_ => pending).Accepted, "同一个未提交计划在去除SQL故障后可完整重试");
                var saved = session.Selected!;
                var reopenedRepository = new CharacterRepository(path);
                var recovered = reopenedRepository.List(owner).Single();
                That(saved.Revision == before.Revision + 1 && JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(recovered) &&
                    Total(recovered.Inventory, id) == 1 &&
                    recovered.Inventory.DailyBoosterUses is [{ Used: 1 } usage] && usage.ItemId == id &&
                    (id == InvitationBox ? Total(recovered.Inventory, Invitation) == 100 :
                        recovered.Inventory.ReviveCoins == before.Inventory.ReviveCoins + 1),
                    "重开仓储恢复耗一源或自返净0、真实奖励、日次数及唯一新revision：" + id);
                string committed = Snapshot(owner);
                Reject<InvalidOperationException>(() => repository.CommitInventory(owner, before, pending.Plan!),
                    "已提交前的旧revision不能再次消费或发奖");
                var returned = recovered.Inventory.Items.Single(item => item.Core.ItemId == id);
                RejectOpen(service, recovered, returned.Position.Index, clock.GetUtcNow(), "重开库不能恢复已消费的日额度");
                That(Snapshot(owner) == committed, "旧revision与重开重复请求均不写审计或资产");
                clock.Advance(TimeSpan.FromDays(1));
                var nextDay = service.Open(saved, new(returned.Position.Index, 1), out _);
                That(nextDay.Accepted && nextDay.Plan is not null, "另一服务日的剩余源可再次规划");
                using var replacement = owners.Acquire(owner.Account, () => { });
                Reject<InvalidOperationException>(() => repository.CommitInventory(owner, saved, nextDay.Plan!),
                    "旧owner generation不能提交新日的耗源及计数计划");
                That(Snapshot(replacement) == committed, "接管owner后所有已提交资产及日账保持");
            }

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot(AccountOwners.Lease lease) => JsonSerializer.Serialize(repository.List(lease)) + ":" +
                Query("SELECT count(*) FROM inventory_audit_log") + ":" + Query("SELECT count(*) FROM currency_audit_log") + ":" +
                Query("SELECT group_concat(name || '=' || seq, ',') FROM (SELECT name,seq FROM sqlite_sequence ORDER BY name)");
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
    }

    static void RejectOpen(InventoryBoosterService service, Character role, short slot, DateTimeOffset now, string message, int count = 1)
    {
        string before = JsonSerializer.Serialize(role);
        var rejected = service.Open(role, new(slot, count), out var receipt, now);
        That(!rejected.Accepted && rejected.Error == 19 && rejected.Plan is null && receipt is null &&
            JsonSerializer.Serialize(role) == before, message);
    }

    static InventoryBoosterService Service(ItemResources resources, Random? random = null, TimeProvider? clock = null) =>
        new(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces, random, clock: clock);
    static Character Role(TownCatalog towns, ItemResources resources, int id, int count, DateTimeOffset now) =>
        new(1, 0, "DailyConsume", 0, towns.Room, towns.InitialReturn)
        {
            Level = 1, Job = 15,
            Inventory = new([new(1, Bag(65), Core(resources, id, count, now)), new(2, Bag(66), Core(resources, 1106, 3, now))], Gold: 12345)
            { ReviveCoins = 7, WinPoints = 9 }
        };
    static ItemCore Core(ItemResources resources, int id, int count, DateTimeOffset now) =>
        InventoryRewardGrantService.Create(resources.Items.Get(id), count, utcNow: now);
    static int Total(InventorySnapshot inventory, int id) => inventory.Items.Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count);
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);

    sealed class FixedRoll(long value) : Random
    {
        public override long NextInt64(long maxValue) => maxValue switch
        {
            10000 when value is >= 0 and < 10000 => value,
            1000 or 100000 => 0,
            _ => throw new InvalidOperationException("日盒边界抽样值超出真实奖池权重")
        };
    }
}
