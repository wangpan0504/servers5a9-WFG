using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
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
            var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
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
            var reservations = Enumerable.Range(0, 5).Select(index => new DungeonGroundReservation(20,
                checked((uint)(400 + index)), map, 100, 100, 0, index))
                .Append(new DungeonGroundReservation(21, 405, map, 100, 100, 0)).ToArray();
            var ground = new DungeonGroundState(run!, [plan], reservations, "明确合成集合来源");
            run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
            var death = authority.Death(run.Snapshot.Identity, 20);
            var pending = run.PrepareConfirmedDeath(death).Pending!;
            var now = DateTimeOffset.UtcNow;
            DungeonGroundContent[] contents = [new(InventoryRewardGrantService.Create(resources.Items.Get(1000), 3, now)),
                new(InventoryRewardGrantService.Create(resources.Items.Get(3037), 7, now)), new(null, 25),
                new(DungeonDropCreation.Create(resources.Items.Get(31440), 1, _ => 0, now,
                    amplification: new EquipmentAmplificationDropPolicy(resources.Amplifications.Rules))),
                new(InventoryRewardGrantService.Create(resources.Items.Get(10300000), 3, now))];
            That(contents[3].Item is { AmplifyType: 0x81, AmplifyValue: > 0 }, "仓储用例使用真实PVF生成的未净化装备，而不是手写标志位");
            int draws = 0;
            var loot = ground.GetOrFreezeAssets(pending, () => { draws++; return contents; });
            var reward = new DungeonExperienceReward("fixture-only", DungeonKillExperienceCatalog.ResourcePath, 1, 3, 1);
            Sql("CREATE TRIGGER collection_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'late collection failure'); END");
            Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot),
                "死亡资产事务失败回滚成长，冻结集合不提前发布");
            That(Query("SELECT count(*) FROM character_dungeon_drop_batches") == 0 &&
                Query("SELECT count(*) FROM character_dungeon_drop_objects") == 0 &&
                SkillRepositoryTests.Equal(repository.LoadCharacter(owner, reserved.Slot), reserved),
                "集合冻结没有提前入包或保留半份SQL结果");
            Sql("DROP TRIGGER collection_fail");
            var committed = repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot);
            That(committed.Character.Revision == reserved.Revision + 1 && Query("SELECT count(*) FROM character_dungeon_drop_objects") == 0 &&
                DungeonTransientReceipts.Find(owner, run.Snapshot.Identity)!.Grounds.Count == 5 &&
                ground.GetOrFreezeAssets(pending, () => { draws++; return []; }) == loot && draws == 1,
                "原事件精确重试保留实体、数量钱包、碎片及金币的五件冻结来源，不重抽");
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
                string audit = item.IsGold || CurrencyWallet.RewardKind(item.Core.ItemId) is not null ? "currency_audit_log" :
                    resources.EpicPieces.ByPiece.ContainsKey(item.Core.ItemId) ? "epic_piece_audit_log" : "inventory_audit_log";
                Sql($"CREATE TRIGGER pickup_collection_fail AFTER INSERT ON {audit} BEGIN SELECT RAISE(ABORT,'late pickup failure'); END");
                Reject<SqliteException>(() => repository.CommitDungeonPickup(owner, current, pickup, inventoryPlan.Plan!, resources.EpicPieces),
                    "逐件receipt晚期失败回滚UID、钱包或金币和revision");
                That(SkillRepositoryTests.Equal(repository.LoadCharacter(owner, current.Slot), before), "逐件失败保留完整旧资产");
                Sql("DROP TRIGGER pickup_collection_fail");
                var result = repository.CommitDungeonPickup(owner, current, pickup, inventoryPlan.Plan!, resources.EpicPieces);
                var lostResult = repository.CommitDungeonPickup(owner, current, pickup, inventoryPlan.Plan!, resources.EpicPieces);
                That(lostResult.Duplicate && lostResult.ProjectionCurrent && SkillRepositoryTests.Equal(result.Character, lostResult.Character),
                    "本key提交结果丢失仅安装实际持久结果，不重发同一数量");
                if (item.Reservation.Ordinal == 0)
                    That(result.Destination == new InventorySlot(InventoryListType.Main, 65) &&
                        lostResult.Destination == result.Destination && lostResult.ItemUid == result.ItemUid && result.ItemUid > 0 &&
                        result.Character.Inventory.Find(result.Destination) is { } stored && stored.Uid == result.ItemUid && stored.Core == item.Core &&
                        Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 0,
                        "集合消耗品实际写入背包槽65，缓存回放保持同槽、真实UID和完整核心，不写旧SQL明细");
                if (result.Currency == CurrencyKind.ClearCube)
                    That(result.Amount == 7 && result.ItemUid == 0 && result.Character.Inventory.AccountWallet.Revision == current.Inventory.AccountWallet.Revision + 1,
                        "账号材料提交返回实际余额版本，不用临时实体UID伪造入账");
                if (item.Reservation.Ordinal == 4)
                {
                    That(result.ItemUid == 0 && result.EpicPiece is { PieceId: 10300000, Amount: 3, Balance: 3 } fragment &&
                        fragment.BookRevision == before.Inventory.EpicPieces.Revision + 1 && lostResult.EpicPiece == result.EpicPiece &&
                        result.Character.Inventory.Items.SequenceEqual(before.Inventory.Items) &&
                        result.Character.Inventory.EpicPieces.Get(0) == 3,
                        "碎片钱包真实提交保留零UID，幂等重放返回相同余额版本，不创建Main实体");
                }
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
            That(Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 0 &&
                DungeonTransientReceipts.Find(owner, run.Snapshot.Identity)!.Pickups.Values.Count(value => value.Committed) == 5 &&
                current.Inventory.Gold == reserved.Inventory.Gold + 25 &&
                current.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 7 &&
                current.Inventory.Items.Single(item => item.Core.ItemId == 1000).Core.Count == 3 &&
                current.Inventory.Items.Single(item => item.Core.ItemId == contents[3].Item!.ItemId && item.Position.List == InventoryListType.Main).Core == contents[3].Item &&
                current.Inventory.EpicPieces.Get(0) == 3 && current.Revision == committed.Character.Revision + 6,
                "同死亡五个key各一次提交，实体、货币与图鉴钱包数量及未净化装备完整核心均正确");
            That(SkillRepositoryTests.Equal(new CharacterRepository(path).LoadCharacter(owner, current.Slot), current),
                "重新打开原仓储保留全部集合资产和逐件去重账本，不重建在线状态");
            // 旧掉落v18和v19均接管现有集合；真实到账资产和逐件结果须原样保留。
            foreach (int sourceVersion in new[] { 18, 19 })
            {
                using (var db = Open())
                {
                    DatabaseSchemaTests.RemoveEmotionSchema(db);
                    DatabaseSchemaTests.RemoveHotkeySchema(db);
                    DatabaseSchemaTests.RemoveTutorialSchema(db);
                    DatabaseSchemaTests.RemoveTitleSchema(db);
                    DatabaseSchemaTests.RewindExperienceCaps(db);
                    DatabaseSchemaTests.RewindAccountQuestSource(db);
                }
                DatabaseSchemaTests.RewindRaidLifecycle(path);
                Sql($"DROP TABLE IF EXISTS character_dungeon_card_options; DROP TABLE IF EXISTS character_gold_card_uses; DROP TABLE IF EXISTS account_card_memberships; DROP TABLE character_dungeon_card_rewards; PRAGMA user_version={sourceVersion}");
                var upgraded = new CharacterRepository(path).LoadCharacter(owner, current.Slot);
                That(Query("PRAGMA user_version") == SqliteMigrations.CurrentVersion && Query("SELECT count(*) FROM character_dungeon_drop_objects") == 0 &&
                    Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 0 && SkillRepositoryTests.Equal(upgraded, current),
                    $"真实已到账v{sourceVersion}集合升级保留四件完整Core、UID、金币与账号钱包，不重建或重复授予");
            }
            var highPreparation = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 21));
            That(highPreparation.Outcome == DungeonRunOutcome.Accepted && highPreparation.Pending is not null,
                "第二只怪物使用独立战斗序号及原Run身份取得真实死亡准备");
            var highPending = highPreparation.Pending!;
            var highLoot = ground.GetOrFreezeAssets(highPending, () => [contents[3]]);
            var highGrowth = repository.CommitDungeonGrowth(owner, current, run, highPending, reward, growth, highLoot);
            run.ConfirmCommittedDeath(highPending);
            That(ground.PublishCommitted(highPending, highLoot), "第二只真实怪物独立发布原装备，不借用已拾取地面key");
            Sql("UPDATE sqlite_sequence SET seq=2147483647 WHERE name='character_inventory_items'");
            var highGround = highLoot.Grounds.Single();
            var highPickup = ground.PreparePickup(run.Snapshot.Identity, highGround)!;
            var highPlan = DungeonPickupPlanning.Item(highGrowth.Character, highGround.Content, resources.Items.Get(highGround.Core.ItemId),
                resources.GoldLimits, resources.EpicPieces, now);
            That(highPlan.Accepted, "高UID装备继续使用原库存规划");
            var highResult = repository.CommitDungeonPickup(owner, highGrowth.Character, highPickup, highPlan.Plan!);
            var highReplay = repository.CommitDungeonPickup(owner, highGrowth.Character, highPickup, highPlan.Plan!);
            That(highResult.ItemUid > int.MaxValue && highReplay.Duplicate && highReplay.ItemUid == highResult.ItemUid &&
                highResult.Character.Inventory.Find(highResult.Destination)?.Core == highGround.Core &&
                Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 0,
                "怪物掉落的64位内部UID原样入库和重放，不写入客户端装备品级或改变完整Core");
            That(ground.ConfirmPickup(highPickup) && SkillRepositoryTests.Equal(new CharacterRepository(path)
                .LoadCharacter(owner, highResult.Character.Slot), highResult.Character), "高UID怪物拾取重开恢复完整资产");
            // 满级角色的击杀经验不进经验条，但必须等额进入角色熟练度。
            var seeded = SkillLearning.Initialize(0, characters.Get(0).Skills.ToArray(), learning);
            That(seeded.Success, "熟练度fixture使用真实职业技能初始化");
            var seedCharacter = new Character(0, 0, "ProfMax", 0, towns.Room, towns.InitialReturn)
            { Level = 1, Learning = seeded.State! };
            var maxPlan = CharacterGrowthPlan.Create(seedCharacter, 95, growth.GetLevelStartExperience(95), growth).Plan!;
            var maxFixture = SkillRepositoryTests.Fixture("ProfMax", towns, characters, learning) with
            {
                Level = 95, InitialLearning = maxPlan.Learning, DungeonState = CharacterDungeonState.NewOrdinary,
                ActiveMainQuests = [new(0, 3145, 1)]
            };
            var maxReserved = repository.ReserveDungeonAttempt(owner, repository.Create(owner, maxFixture), 600);
            var maxEntry = new DungeonEntryPlan(maxReserved.Id, maxReserved.Revision, 3, 3145, 0, 0, maze, map, map);
            That(SinglePlayerDungeonRun.TryCreate(maxEntry, 1, 1, new([plan], []), authority, out var maxRun, out _),
                "满级熟练度fixture使用原Run入口");
            maxRun!.CompleteLoading(maxRun.Snapshot.Identity, Guid.NewGuid());
            var maxPending = maxRun.PrepareConfirmedDeath(authority.Death(maxRun.Snapshot.Identity, 20)).Pending!;
            var maxGrowth = repository.CommitDungeonGrowth(owner, maxReserved, maxRun, maxPending, reward, growth);
            That(maxGrowth.Character.Level == 95 && maxGrowth.Character.Learning.Experience == growth.GetLevelStartExperience(95) &&
                maxGrowth.Character.DungeonState is { ProficiencyLevel: 0, ProficiencyExperience: 1 } &&
                Query("SELECT proficiency_experience FROM character_dungeon_state WHERE character_id=" + maxReserved.Id) == 1L,
                "满级击杀不涨经验条，熟练度按同额经验写入");
            That(new CharacterRepository(path).LoadCharacter(owner, maxReserved.Slot).DungeonState is
                { ProficiencyLevel: 0, ProficiencyExperience: 1 }, "重开库保留已提交的熟练度进度");
            // 明确合成两张不同副本/房间的同模板演员，验证稳定资格键；不宣称PVF自然路线会重复该怪。
            var dailyCharacter = repository.Create(owner, SkillRepositoryTests.Fixture("LukeCross", towns, characters, learning) with
                { DungeonState = CharacterDungeonState.NewOrdinary });
            int originalPieces = dailyCharacter.Inventory.EpicPieces.Get(0);
            Guid? firstDailyEvent = null;
            for (int index = 0; index < 2; index++)
            {
                var dailyReserved = repository.ReserveDungeonAttempt(owner, dailyCharacter, checked((uint)(700 + index * 10)));
                var dailyMap = new DungeonMap("boss", new(0, 0), 55900 + index, "map/luke-daily-cross-fixture.map");
                var dailyMaze = new DungeonMaze(0, 1, 1, "合成跨图日资格", [dailyMap.Position], [dailyMap.Position], [dailyMap], []);
                var dailyEntry = new DungeonEntryPlan(dailyReserved.Id, dailyReserved.Revision, checked((uint)(5101 + index)), 0, byte.MaxValue, 0,
                    dailyMaze, dailyMap, dailyMap) { IsOrdinaryLuke = true, OrdinaryLukeDay = 20261026 };
                var dailySource = source with { MonsterId = 56063, RawValues = [56063, 0, 3, 100, 100, 0, 1, 1] };
                var dailyPlan = new DungeonMonsterPlan(new(dailyMap, [dailySource], [], []), [new(0, 20, 56063, 3, dailySource)]);
                That(SinglePlayerDungeonRun.TryCreate(dailyEntry, dailyReserved.DungeonState!.LastAttempt, index + 1, new([dailyPlan], []), authority, out var dailyRun, out var dailyReason),
                    "合成跨图回归仍建立原Run与不同instance：" + dailyReason);
                dailyRun!.CompleteLoading(dailyRun.Snapshot.Identity, Guid.NewGuid());
                var dailyPending = dailyRun.PrepareConfirmedDeath(authority.Death(dailyRun.Snapshot.Identity, 20)).Pending!;
                var previous = repository.LoadOrdinaryLukeDropEvent(owner, dailyReserved.Id, 20261026, 56063);
                That((previous is null) == (index == 0), "资格查询不使用副本、房间或对象key作为去重键");
                var dailyGround = new DungeonGroundState(dailyRun, [dailyPlan],
                    [new(20, dailyReserved.DungeonState.NextObjectKey - 1, dailyMap, 100, 100, 0)], "合成跨图日资格");
                int dailyDraws = 0;
                var dailyLoot = dailyGround.GetOrFreezeAssets(dailyPending, () => { dailyDraws++; return [new(InventoryRewardGrantService.Create(resources.Items.Get(10300000), 1, now))]; },
                    ordinaryLukeDaily: new(20261026, 56063, previous is null));
                var dailyReward = new DungeonExperienceReward("fixture-only", DungeonKillExperienceCatalog.ResourcePath, 56063, 3, 1);
                var dailyCommit = repository.CommitDungeonGrowth(owner, dailyReserved, dailyRun, dailyPending, dailyReward, growth, dailyLoot);
                dailyCharacter = dailyCommit.Character;
                firstDailyEvent ??= dailyPending.Death.EventId;
                That(dailyDraws == (index == 0 ? 1 : 0) && dailyLoot.Grounds.Length == (index == 0 ? 1 : 0) &&
                    repository.LoadOrdinaryLukeDropEvent(owner, dailyReserved.Id, 20261026, 56063) == firstDailyEvent,
                    "换副本与房间后同源怪不重抽/不增发，仍保留原首击事实");
                dailyRun.ConfirmCommittedDeath(dailyPending);
                That(dailyGround.PublishCommitted(dailyPending, dailyLoot), "跨图空地面结果也在原事务提交后发布");
                if (index == 0)
                {
                    var pieceGround = dailyLoot.Grounds.Single();
                    var piecePickup = dailyGround.PreparePickup(dailyRun.Snapshot.Identity, pieceGround)!;
                    var piecePlan = DungeonPickupPlanning.Item(dailyCharacter, pieceGround.Content, resources.Items.Get(10300000),
                        resources.GoldLimits, resources.EpicPieces, now);
                    That(piecePlan.Accepted, "日资格冻结集合保留原碎片拾取规划");
                    var pieceCommit = repository.CommitDungeonPickup(owner, dailyCharacter, piecePickup, piecePlan.Plan!, resources.EpicPieces);
                    var pieceReplay = repository.CommitDungeonPickup(owner, dailyCharacter, piecePickup, piecePlan.Plan!, resources.EpicPieces);
                    That(pieceCommit.EpicPiece is { PieceId: 10300000, Amount: 1 } metadata && metadata.Balance == originalPieces + 1 &&
                        metadata.BookRevision == dailyCharacter.Inventory.EpicPieces.Revision + 1 && pieceReplay.EpicPiece == metadata &&
                        pieceReplay.Duplicate && pieceCommit.Character.Inventory.Items.SequenceEqual(dailyCharacter.Inventory.Items) &&
                        repository.LoadOrdinaryLukeDropEvent(owner, dailyCharacter.Id, 20261026, 56063) == firstDailyEvent,
                        "日首击资格与碎片book余额/revision共存，回放保留metadata且不伪造物理槽");
                    That(dailyGround.ConfirmPickup(piecePickup), "碎片实际入book后原地面拾取确认");
                    dailyCharacter = pieceCommit.Character;
                }
                That(dailyCharacter.Inventory.EpicPieces.Get(0) == originalPieces + 1,
                    "跨图重复死亡不生成第二份碎片或覆盖账号book余额");
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
        long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, ++sequence);
    }
}
