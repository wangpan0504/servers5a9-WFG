using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonDropCollectionRepositoryTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-drop-collection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("collection-fixture", () => { });
            var learning = SkillLearningCatalog.Load(pvf);
            var growth = CharacterGrowthCatalog.Load(pvf);
            var fixture = SkillRepositoryTests.Fixture("DropCollect", towns, characters, learning) with
            {
                DungeonState = CharacterDungeonState.NewOrdinary,
                ActiveMainQuests = [new(0, 3145, 1)]
            };
            var created = repository.Create(owner, fixture);
            var reserved = repository.ReserveDungeonAttempt(owner, created, 500);
            That(Query("PRAGMA user_version") == SqliteMigrations.CurrentVersion &&
                Query("SELECT count(*) FROM character_dungeon_drop_objects") == 0,
                "集合事务沿生产初始化的正式空账本，不由测试手动建表");
            var map = new DungeonMap("boss", new(0, 0), 76126, "map/collection-fixture.map");
            var maze = new DungeonMaze(0, 1, 1, "合成集合仓储", [map.Position], [map.Position], [map], []);
            var entry = new DungeonEntryPlan(reserved.Id, reserved.Revision, 3, 3145, 0, 0, maze, map, map);
            var source = new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
                "[fixed]", "[normal]", "monster/collection-fixture.mob");
            var plan = new DungeonMonsterPlan(new(map, [source, source], [], []), [new(0, 20, 1, 3, source), new(1, 21, 1, 3, source)]);
            var authority = new FixtureAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out _),
                "集合仓储fixture使用原Run与可信事件入口");
            var reservations = Enumerable.Range(0, 4).Select(index => new DungeonGroundReservation(20,
                checked((uint)(400 + index)), map, 100, 100, 0, index)).ToArray();
            var ground = new DungeonGroundState(run!, [plan], reservations, "明确合成集合来源");
            run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
            var death = authority.Death(run.Snapshot.Identity, 20);
            var pending = run.PrepareConfirmedDeath(death).Pending!;
            var now = DateTimeOffset.UtcNow;
            DungeonGroundContent[] contents = [new(InventoryRewardGrantService.Create(resources.Items.Get(1000), 3, now)),
                new(InventoryRewardGrantService.Create(resources.Items.Get(3037), 7, now)), new(null, 25),
                new(DungeonDropCreation.Create(resources.Items.Get(101010912), 1, _ => 0, now))];
            int draws = 0;
            var loot = ground.GetOrFreezeAssets(pending, () => { draws++; return contents; });
            var reward = new DungeonExperienceReward("fixture-only", DungeonKillExperienceCatalog.ResourcePath, 1, 3, 1);
            Sql("CREATE TRIGGER collection_fail AFTER INSERT ON character_dungeon_drop_objects WHEN NEW.ordinal=2 BEGIN SELECT RAISE(ABORT,'late collection failure'); END");
            Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot),
                "集合第三件写入失败回滚成长、父账本和前两件资产");
            That(Query("SELECT count(*) FROM character_dungeon_drop_batches") == 0 &&
                Query("SELECT count(*) FROM character_dungeon_drop_objects") == 0 &&
                SkillRepositoryTests.Equal(repository.LoadCharacter(owner, reserved.Slot), reserved),
                "集合冻结没有提前入包或保留半份SQL结果");
            Sql("DROP TRIGGER collection_fail");
            var committed = repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot);
            That(committed.Character.Revision == reserved.Revision + 1 && Query("SELECT count(*) FROM character_dungeon_drop_objects") == 4 &&
                ground.GetOrFreezeAssets(pending, () => { draws++; return []; }) == loot && draws == 1,
                "原事件精确重试保留四件数量、完整Core和金币，不重抽");
            var replay = repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot);
            That(replay.Duplicate && replay.Character.Revision == committed.Character.Revision,
                "集合死亡提交结果丢失后只读恢复，不重复增加经验或对象");
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth),
                "集合重放不能省略全部冻结结果");
            var truncated = new FrozenDungeonGroundDeath(loot.Death, loot.CommittedIdentity, loot.Map,
                loot.Grounds.RemoveAt(3), loot.ResourceIdentity);
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, truncated),
                "集合精确重放不能只提交首几件或丢失装备");
            run.ConfirmCommittedDeath(pending);
            That(ground.PublishCommitted(pending, loot), "完整集合提交后才发布各地面对象");
            Character current = committed.Character;
            foreach (var item in loot.Grounds)
            {
                var pickup = ground.PreparePickup(run.Snapshot.Identity, item)!;
                var inventoryPlan = item.IsGold
                    ? DungeonPickupPlanning.Gold(current, item.Content, resources.GoldLimits)
                    : DungeonPickupPlanning.Item(current, item.Content, resources.Items.Get(item.Core.ItemId), resources.GoldLimits, resources.EpicPieces, now);
                That(inventoryPlan.Accepted, "真实资源对应本件冻结资产可完整规划");
                if (item.IsGold)
                {
                    var malformed = new InventoryPlan(current.Inventory,
                        inventoryPlan.Plan!.After with { Gold = inventoryPlan.Plan.After.Gold + 1 }, "tampered-gold");
                    Reject<InvalidDataException>(() => repository.CommitDungeonPickup(owner, current, pickup, malformed),
                        "金币拾取规划不能修改原冻结增量");
                }
                var before = current;
                Sql("CREATE TRIGGER pickup_collection_fail AFTER INSERT ON character_dungeon_drop_pickups BEGIN SELECT RAISE(ABORT,'late pickup failure'); END");
                Reject<SqliteException>(() => repository.CommitDungeonPickup(owner, current, pickup, inventoryPlan.Plan!),
                    "逐件receipt晚期失败回滚UID、钱包或金币和revision");
                That(SkillRepositoryTests.Equal(repository.LoadCharacter(owner, current.Slot), before), "逐件失败保留完整旧资产");
                Sql("DROP TRIGGER pickup_collection_fail");
                var result = repository.CommitDungeonPickup(owner, current, pickup, inventoryPlan.Plan!);
                var lostResult = repository.CommitDungeonPickup(owner, current, pickup, inventoryPlan.Plan!);
                That(lostResult.Duplicate && lostResult.ProjectionCurrent && SkillRepositoryTests.Equal(result.Character, lostResult.Character),
                    "本key提交结果丢失仅安装实际持久结果，不重发同一数量");
                if (result.Currency == CurrencyKind.ClearCube)
                    That(result.Amount == 7 && result.ItemUid == 0 && result.Character.Inventory.AccountWallet.Revision == current.Inventory.AccountWallet.Revision + 1,
                        "账号材料提交返回实际余额版本，不用临时实体UID伪造入账");
                if (item.Reservation.Ordinal == 3)
                {
                    var saved = repository.Save(owner, result.Character, result.Character.Position, result.Character.Return);
                    var late = repository.CommitDungeonPickup(owner, current, pickup, inventoryPlan.Plan!);
                    That(late.Duplicate && !late.ProjectionCurrent && SkillRepositoryTests.Equal(saved, late.Character),
                        "提交后已有新revision时仅恢复当前资产，禁止用旧receipt投影覆盖新状态");
                    result = result with { Character = saved };
                }
                That(ground.ConfirmPickup(pickup), "逐件提交后确认原准备，其他物品仍可拾取");
                current = result.Character;
            }
            That(Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 4 && current.Inventory.Gold == reserved.Inventory.Gold + 25 &&
                current.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 7 &&
                current.Inventory.Items.Single(item => item.Core.ItemId == 1000).Core.Count == 3 &&
                current.Inventory.Items.Single(item => item.Core.ItemId == 101010912 && item.Position.List == InventoryListType.Main).Core.Value == 0 &&
                current.Revision == committed.Character.Revision + 5,
                "同死亡四个key各一次提交，道具、账号材料、金币和零品级装备均正确");
            That(SkillRepositoryTests.Equal(new CharacterRepository(path).LoadCharacter(owner, current.Slot), current),
                "重新打开原仓储保留全部集合资产和逐件去重账本，不重建在线状态");
            // 旧掉落v18和v19均接管现有集合；真实到账资产和逐件结果须原样保留。
            foreach (int sourceVersion in new[] { 18, 19 })
            {
                using (var db = Open()) DatabaseSchemaTests.RewindExperienceCaps(db);
                Sql($"DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE character_dungeon_card_rewards; PRAGMA user_version={sourceVersion}");
                var upgraded = new CharacterRepository(path).LoadCharacter(owner, current.Slot);
                That(Query("PRAGMA user_version") == SqliteMigrations.CurrentVersion && Query("SELECT count(*) FROM character_dungeon_drop_objects") == 4 &&
                    Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 4 && SkillRepositoryTests.Equal(upgraded, current),
                    $"真实已到账v{sourceVersion}集合升级保留四件完整Core、UID、金币与账号钱包，不重建或重复授予");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"PASS {Count - checks} 完整集合真实SQLite冻结、逐件拾取、回滚和结果恢复断言（正式结构，非实机）");

        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            return db;
        }
        long Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        void Sql(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

    }

    sealed class FixtureAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
}
