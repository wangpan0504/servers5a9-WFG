using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonPassiveDropTests
{
    public static void RunResources(TcpTestResources resources)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-passive-drops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("passive-drop-fixture", () => { });
        try
        {
            var fixture = SkillRepositoryTests.Fixture("PassiveLoot", resources.Towns, resources.Characters, resources.Learning) with
            { DungeonState = CharacterDungeonState.NewOrdinary, ActiveMainQuests = [] };
            var created = repository.Create(owner, fixture);
            var filler = InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(90032), 1, Now);
            var filled = created.Inventory with { Items = created.Inventory.Items.AddRange(Enumerable.Range(49, 48)
                .Select(slot => new InventoryItem(0, new(InventoryListType.GuildMedal, (short)slot), filler))) };
            created = InventoryService.Commit(repository, owner, created,
                new(true, 0, new(created.Inventory, filled, "pickup-slot-fixture", new(created))));
            var f = Fixture(created.Id, created.Revision + 1, checked((ushort)created.DungeonState!.NextObjectKey));
            DungeonGroundContent[] contents = [new(InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1000), 3, Now)),
                new(InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(3037), 7, Now)),
                new(null, 25), new(DungeonDropCreation.Create(resources.ItemResources.Items.Get(101010912), 1, _ => 0, Now)),
                new(InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(90000), 2, Now)),
                new(InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(90001), 3, Now))];
            var origin = new DungeonPassiveDropOrigin(1, 0, 0, 0);
            var eventId = Guid.NewGuid();
            var grounds = contents.Select((content, index) => new DungeonGroundObject(eventId, f.Run.Snapshot.Identity,
                new(f.Plan.SpecialObjects[1].ObjectKey, checked((uint)(400 + index)), f.Entry.StartMap, 400, 250, 0, index), content)
            { PassiveOrigin = origin, UsesCollectionLedger = true, ResourceIdentity = "合成特殊物品事务来源" }).ToArray();
            var state = new DungeonGroundState(f.Run, [f.Plan], [], passiveGrounds: grounds);
            var current = repository.ReserveDungeonAttempt(owner, created, 500);
            state.PublishPassiveEntry();
            f.Run.CompleteLoading(f.Run.Snapshot.Identity, Guid.NewGuid());
            That(Scalar("SELECT count(*) FROM character_dungeon_passive_pickups") == 0 &&
                Scalar("SELECT count(*) FROM character_dungeon_growth_receipts") == 0 && current.DungeonState!.NextObjectKey == 500,
                "准入只保存attempt与key高水位，未拾取的生成物品不写表或伪造死亡经验");
            int picked = 0;
            foreach (var ground in grounds)
            {
                if (ground.Content.Item?.Kind == ItemKind.Equipment)
                    Sql("UPDATE sqlite_sequence SET seq=2147483647 WHERE name='character_inventory_items'");
                var available = state.FindAvailable(f.Run.Snapshot.Identity, ground.Reservation.GroundKey)!;
                var prepared = state.PreparePickup(f.Run.Snapshot.Identity, available)!;
                var plan = ground.IsGold ? DungeonPickupPlanning.Gold(current, ground.Content, resources.ItemResources.GoldLimits) :
                    DungeonPickupPlanning.Item(current, ground.Content, resources.ItemResources.Items.Get(ground.Core.ItemId),
                        resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces, Now);
                That(plan.Accepted, "原冻结内容复用正式拾取规划，装备种子与完整数量不重建");
                var old = current;
                string audit = ground.IsGold || CurrencyWallet.RewardKind(ground.Core.ItemId) is not null ? "currency_audit_log" : "inventory_audit_log";
                Sql($"CREATE TRIGGER passive_pickup_fail AFTER INSERT ON {audit} BEGIN SELECT RAISE(ABORT,'late passive pickup failure'); END");
                Reject<SqliteException>(() => repository.CommitDungeonPickup(owner, current, prepared, plan.Plan!),
                    "拾取回执末步失败完整回滚库存、UID、钱包与金币");
                That(JsonSerializer.Serialize(repository.LoadCharacter(owner, current.Slot)) == JsonSerializer.Serialize(current) &&
                    state.HasPendingPickup && Scalar("SELECT count(*) FROM character_dungeon_passive_pickups") == 0 &&
                    (DungeonTransientReceipts.Find(owner, f.Run.Snapshot.Identity)?.Pickups.Values.Count(value => value.Committed) ?? 0) == picked,
                    "SQL失败不留下拾取行，保留同一地面准备，不假报成功或自动重抽");
                Sql("DROP TRIGGER passive_pickup_fail");
                var committed = repository.CommitDungeonPickup(owner, current, prepared, plan.Plan!);
                var replay = repository.CommitDungeonPickup(owner, current, prepared, plan.Plan!);
                That(Scalar("SELECT count(*) FROM character_dungeon_passive_pickups") == 0 &&
                    DungeonTransientReceipts.Find(owner, f.Run.Snapshot.Identity)!.Pickups.Values.Count(value => value.Committed) == ++picked,
                    "成功拾取只确认当前实例缓存，重复不增加资产或旧SQL明细");
                That(!committed.Duplicate && replay.Duplicate && replay.ProjectionCurrent &&
                    replay.ItemUid == committed.ItemUid && replay.Character.Revision == committed.Character.Revision &&
                    replay.Character.Revision == old.Revision + 1, "结果丢失只读原回执恢复，每个地面key仅授予一次资产");
                if (ground.Content.Item?.Kind == ItemKind.GuardianGem)
                {
                    var destination = ground.Core.ItemId == 90000
                        ? new InventorySlot(InventoryListType.GuildMedal, 97) : new(InventoryListType.Main, 3);
                    That(committed.Destination == destination && replay.Destination == destination &&
                        committed.Character.Inventory.Find(destination) is { } actual && actual.Uid == committed.ItemUid && actual.Core == ground.Core,
                        "专用页和满页回退快捷栏使用实际列表、槽号及真实UID，原回执恢复不能强当Main");
                }
                if (ground.Content.Item?.Kind == ItemKind.Equipment)
                    That(committed.ItemUid > int.MaxValue && replay.ItemUid == committed.ItemUid,
                        "普通装备的内部64位UID完整保存和恢复，不套用客户端地面key或宠物键限制");
                That(state.ConfirmPickup(prepared) && state.FindAvailable(f.Run.Snapshot.Identity, ground.Reservation.GroundKey) is null,
                    "提交后消费原地面对象，重复请求不复活");
                if (!ground.IsGold)
                {
                    var changed = new DungeonGroundPickupPreparation(prepared.Identity,
                        ground with { Content = new(ground.Core with { Attr = 7 }) });
                    Reject<InvalidDataException>(() => repository.CommitDungeonPickup(owner, old, changed, plan.Plan!),
                        "即使已有拾取回执也不能更换冻结核心");
                }
                current = committed.Character;
            }
            That(Scalar("SELECT count(*) FROM character_dungeon_passive_pickups") == 0 &&
                DungeonTransientReceipts.Find(owner, f.Run.Snapshot.Identity)!.Pickups.Values.Count(value => value.Committed) == contents.Length &&
                Scalar("SELECT count(*) FROM character_dungeon_drop_batches") == 0 &&
                Scalar("SELECT count(*) FROM character_dungeon_growth_receipts") == 0,
                "特殊来源拾取共享资产事务，但不污染怪物集合或击杀来源");
            var reopened = new CharacterRepository(path).LoadCharacter(owner, current.Slot);
            That(JsonSerializer.Serialize(reopened) == JsonSerializer.Serialize(current), "仓储重建恢复完整已到账资产、UID和高水位");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestFiles.DeleteDirectory(root, recursive: true);
        }
        Console.WriteLine($"PASS {Count - before} 特殊物品准入与拾取真实SQLite回滚、幂等和多类别资产断言");

        void Sql(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        long Scalar(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }
}
