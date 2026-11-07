using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Game.Tutorials;
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
        EquipmentGoldBonusTests.RunResources(resources.Items);
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
            var title = new InventoryItem(0, new(InventoryListType.Equipment, 13),
                InventoryRewardGrantService.Create(resources.Items.Get(100331240)) with { ExpireTime = 0 });
            fixture = fixture with { Inventory = fixture.Inventory with
                { Items = fixture.Inventory.Items.Where(item => item.Position != title.Position).Append(title).ToImmutableArray() } };
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
            var plan = new DungeonMonsterPlan(new(map, [source, source, source], [], []),
                [new(0, 20, 1, 3, source), new(1, 21, 1, 3, source), new(2, 22, 1, 3, source)]);
            var authority = new FixtureAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out _),
                "集合仓储fixture使用原Run与可信事件入口");
            var reservations = Enumerable.Range(0, 5).Select(index => new DungeonGroundReservation(20,
                checked((uint)(400 + index)), map, 100, 100, 0, index))
                .Append(new DungeonGroundReservation(21, 405, map, 100, 100, 0))
                .Append(new DungeonGroundReservation(22, 406, map, 100, 100, 0)).ToArray();
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
            That(committed.Character.Revision == reserved.Revision + 1 && Query("SELECT count(*) FROM character_dungeon_drop_objects") == 5 &&
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
                        Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 1,
                        "集合消耗品实际写入背包槽65，持久回放保持同槽、真实UID和完整核心及SQL回执");
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
            That(Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 5 &&
                DungeonTransientReceipts.Find(owner, run.Snapshot.Identity)!.Pickups.Values.Count(value => value.Committed) == 5 &&
                current.Inventory.Gold == reserved.Inventory.Gold + 27 &&
                current.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 7 &&
                current.Inventory.Items.Single(item => item.Core.ItemId == 1000).Core.Count == 3 &&
                current.Inventory.Items.Single(item => item.Core.ItemId == contents[3].Item!.ItemId && item.Position.List == InventoryListType.Main).Core == contents[3].Item &&
                current.Inventory.EpicPieces.Get(0) == 3 && current.Revision == committed.Character.Revision + 6,
                "同死亡五个key各一次提交，金币含称号额外2且原地面仍为25，实体、钱包和完整核心均正确");
            That(SkillRepositoryTests.Equal(new CharacterRepository(path).LoadCharacter(owner, current.Slot), current),
                "重新打开原仓储保留全部集合资产和逐件去重账本，不重建在线状态");
            var wornTitle = current.Inventory.Find(title.Position)!;
            var unequipped = current.Inventory with
                { Items = current.Inventory.Items.Replace(wornTitle, wornTitle with { Position = new(InventoryListType.Main, 64) }) };
            current = InventoryService.Commit(repository, owner, current,
                new(true, 0, new(current.Inventory, unequipped, "gold-bonus-unequip", new(current))));
            var goldGround = loot.Grounds.Single(item => item.IsGold);
            var plainGold = DungeonPickupPlanning.Gold(current, goldGround.Content, resources.GoldLimits);
            var oldGold = new CharacterRepository(path).CommitDungeonPickup(owner, current,
                new(run.Snapshot.Identity, goldGround), plainGold.Plan!, resources.EpicPieces, resources.Items);
            That(plainGold.Plan!.After.Gold == current.Inventory.Gold + 25 && oldGold.Duplicate &&
                !oldGold.ProjectionCurrent && oldGold.Amount == 27 && oldGold.Character.Inventory.Gold == current.Inventory.Gold,
                "脱下称号后新拾取回到原金额；旧回执仍重放原27金币，不按新穿戴重算或再次入账");
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
                That(Query("PRAGMA user_version") == SqliteMigrations.CurrentVersion && Query("SELECT count(*) FROM character_dungeon_drop_objects") == 5 &&
                    Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 5 && SkillRepositoryTests.Equal(upgraded, current),
                    $"真实已到账v{sourceVersion}集合升级保留五件完整Core、UID、金币、账号钱包与图鉴，不重建或重复授予");
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
                Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 6,
                "怪物掉落的64位内部UID原样入库和重放，不写入客户端装备品级或改变完整Core");
            That(ground.ConfirmPickup(highPickup) && SkillRepositoryTests.Equal(new CharacterRepository(path)
                .LoadCharacter(owner, highResult.Character.Slot), highResult.Character), "高UID怪物拾取重开恢复完整资产");
            // 第三只正常collection来源使用真实716，先证明合法45规划，再进入原仓储提交。
            var commonTemplate = resources.Items.Get(490013716);
            That(commonTemplate.AccountCommonItem, "716真实Catalog声明公共物品，不替换模板或猜容器");
            var commonCore = InventoryRewardGrantService.Create(commonTemplate, 3, now) with
            { Marker16 = 33, Extension97 = 0x12345678, Extension114 = new(1, 333) };
            var commonPending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 22)).Pending!;
            var commonLoot = ground.GetOrFreezeAssets(commonPending, () => [new(commonCore)]);
            var commonGrowth = repository.CommitDungeonGrowth(owner, highResult.Character, run, commonPending, reward, growth, commonLoot);
            run.ConfirmCommittedDeath(commonPending);
            That(ground.PublishCommitted(commonPending, commonLoot), "716正常冻结集合在死亡事务后发布来源");
            var commonGround = commonLoot.Grounds.Single();
            var commonPickup = ground.PreparePickup(run.Snapshot.Identity, commonGround)!;
            var commonPlan = DungeonPickupPlanning.Item(commonGrowth.Character, commonGround.Content, commonTemplate,
                resources.GoldLimits, resources.EpicPieces, now);
            var commonSlot = new InventorySlot(InventoryListType.AccountCommon, 0);
            That(commonGround.UsesCollectionLedger && commonPickup is not null && commonPlan.Accepted &&
                commonPlan.Plan is { } legalCommon && legalCommon.ChangedSlots.SequenceEqual(new[] { commonSlot }) &&
                legalCommon.After.Find(commonSlot)?.Core == commonCore,
                "716真实来源与发奖前置成立，合法计划明确目的45/0并保完整Core");
            var commonExpected = commonGrowth.Character;
            var mainRange = ItemSlotBoundService.Range(commonTemplate.Kind, commonExpected.Inventory);
            var wrongSlot = Enumerable.Range(mainRange.Start, mainRange.End - mainRange.Start + 1)
                .Select(index => new InventorySlot(InventoryListType.Main, checked((short)index)))
                .First(slot => commonExpected.Inventory.Find(slot) is null);
            var plannedCommon = commonPlan.Plan!.After.Find(commonSlot)!;
            var wrongAfter = commonPlan.Plan.After with
            { Items = commonPlan.Plan.After.Items.Replace(plannedCommon, plannedCommon with { Position = wrongSlot }) };
            wrongAfter.Validate(allocating: true);
            var wrongContainer = new InventoryPlan(commonPlan.Plan.Before, wrongAfter, commonPlan.Plan.Reason,
                commonPlan.Plan.CharacterPrecondition);
            That(wrongContainer.ChangedSlots.SequenceEqual(new[] { wrongSlot }) &&
                wrongAfter.Find(wrongSlot)?.Core == commonCore &&
                commonExpected.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main)
                    .All(item => wrongAfter.Find(item.Position) == item),
                "负例仅把同一完整716奖励挪到合法Main空材料槽，保单槽增量和全部原Main实例");
            long beforeWrongReceipts = Query("SELECT count(*) FROM character_dungeon_drop_pickups");
            Reject<InvalidDataException>(() => repository.CommitDungeonPickup(owner, commonExpected, commonPickup!,
                wrongContainer, resources.EpicPieces, resources.Items), "真实common模板禁止借合法Main槽提交同一冻结物品");
            That(SkillRepositoryTests.Equal(repository.LoadCharacter(owner, commonExpected.Slot), commonExpected) &&
                Query("SELECT count(*) FROM character_dungeon_drop_pickups") == beforeWrongReceipts,
                "错误目的容器拒绝不改库存或拾取回执，原合法45计划仍可继续");
            // 修复前此处在PickupSlot按Material猜Main时抛异常；不是无资源、无合法计划或伪造来源的RED。
            var commonResult = repository.CommitDungeonPickup(owner, commonGrowth.Character, commonPickup!, commonPlan.Plan!,
                resources.EpicPieces, resources.Items);
            var commonReplay = repository.CommitDungeonPickup(owner, commonGrowth.Character, commonPickup!, commonPlan.Plan!,
                resources.EpicPieces, resources.Items);
            That(commonResult.Destination == commonSlot && commonResult.ItemUid > 0 &&
                commonResult.Character.Inventory.Find(commonSlot) is { } commonItem && commonItem.Uid == commonResult.ItemUid &&
                commonItem.Core == commonCore && commonReplay.Duplicate && commonReplay.ProjectionCurrent &&
                commonReplay.Destination == commonSlot && commonReplay.ItemUid == commonResult.ItemUid &&
                SkillRepositoryTests.Equal(commonResult.Character, commonReplay.Character),
                "716集合提交真实45同UID及完整Core，原key重放不增发且不伪造Main槽");
            That(Query("SELECT count(*) FROM character_dungeon_drop_pickups WHERE destination_list=45 AND destination_slot=0") == 1 &&
                Query($"SELECT count(*) FROM character_inventory_items WHERE item_uid={commonResult.ItemUid} AND character_id IS NULL") == 1 &&
                ground.ConfirmPickup(commonPickup!) && ground.FindAvailable(run.Snapshot.Identity, 406) is null &&
                new CharacterRepository(path).LoadCharacter(owner, commonResult.Character.Slot).Inventory.Find(commonSlot)?.Core == commonCore,
                "716实际目的容器持久化，提交后消除原地面源，仓储重开不重复发物");
            var directCharacter = commonResult.Character;
            var bagTitle = directCharacter.Inventory.Items.Single(item => item.Core.ItemId == title.Core.ItemId);
            directCharacter = InventoryService.Commit(repository, owner, directCharacter,
                new(true, 0, new(directCharacter.Inventory, directCharacter.Inventory with
                { Items = directCharacter.Inventory.Items.Replace(bagTitle, bagTitle with { Position = title.Position }) },
                "gold-bonus-reequip", new(directCharacter))));
            var directReserved = repository.ReserveDungeonAttempt(owner, directCharacter, 700);
            var directEntry = new DungeonEntryPlan(directReserved.Id, directReserved.Revision, 3, 3145, 0, 0, maze, map, map);
            That(SinglePlayerDungeonRun.TryCreate(directEntry, directReserved.DungeonState!.LastAttempt, 1,
                new([plan], []), authority, out var directRun, out _),
                "金币自动拾取使用新的真实副本实例，不继承旧拾取key");
            var directGround = new DungeonGroundState(directRun!, [plan],
                [new(20, 450, map, 100, 100, 0, 0), new(20, 451, map, 100, 100, 0, 1)], "金币自动拾取合成来源");
            directRun!.CompleteLoading(directRun.Snapshot.Identity, Guid.NewGuid());
            var directPending = directRun.PrepareConfirmedDeath(authority.Death(directRun.Snapshot.Identity, 20)).Pending!;
            var directLoot = directGround.GetOrFreezeAssets(directPending, () => [new(null, 101), new(null, 19)]);
            var directPlan = DungeonDirectPickupPlan.Create(directReserved, directPending, directLoot, directReserved.Inventory,
                resources.Items, resources.EpicPieces, true, DateTimeOffset.UtcNow, TimeProvider.System, resources.GoldLimits);
            That(directPlan.Entries.Select(item => item.Amount).SequenceEqual(new[] { 111, 20 }) &&
                directPlan.After.Gold == directReserved.Inventory.Gold + 131 &&
                directLoot.Grounds.Select(item => item.GoldAmount).SequenceEqual(new[] { 101, 19 }),
                "自动拾取逐个地面金币应用称号10%，不合并取整或修改冻结来源");
            long beforeDirectReceipts = Query("SELECT count(*) FROM character_dungeon_drop_pickups");
            Sql("CREATE TRIGGER gold_direct_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'gold direct rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, directReserved, directRun, directPending,
                reward, growth, directLoot, directPickup: directPlan), "金币加成和自动拾取与死亡事务共同回滚");
            That(Query("SELECT count(*) FROM character_dungeon_drop_pickups") == beforeDirectReceipts &&
                SkillRepositoryTests.Equal(repository.LoadCharacter(owner, directReserved.Slot), directReserved),
                "自动拾取失败不入金币、经验或拾取回执");
            Sql("DROP TRIGGER gold_direct_fail");
            var directCommit = repository.CommitDungeonGrowth(owner, directReserved, directRun, directPending, reward, growth,
                directLoot, directPickup: directPlan);
            DungeonTransientReceipts.Forget(owner, directRun.Snapshot.Identity);
            var directReplay = new CharacterRepository(path).CommitDungeonGrowth(owner, directReserved, directRun, directPending,
                reward, growth, directLoot, directPickup: directPlan);
            That(directCommit.Character.Inventory.Gold == directReserved.Inventory.Gold + 131 && directReplay.Duplicate &&
                directReplay.Character.Inventory.Gold == directCommit.Character.Inventory.Gold &&
                directCommit.DirectPickup.GroundKeys.SequenceEqual(new uint[] { 450, 451 }) &&
                directReplay.DirectPickup.GroundKeys.SequenceEqual(directCommit.DirectPickup.GroundKeys) &&
                Query("SELECT sum(amount) FROM character_dungeon_drop_pickups WHERE ground_key IN(450,451)") == 131,
                "自动拾取保存实际111与20金币，缓存丢失和仓储重开只回放原结果，不再加成");
            directRun.ConfirmCommittedDeath(directPending);
            That(directGround.PublishCommitted(directPending, directLoot, directCommit.DirectPickup.GroundKeys) &&
                directGround.FindAvailable(directRun.Snapshot.Identity, 450) is null,
                "自动拾取提交后移除原地面来源，不能再次手动领取");
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
            TestFiles.DeleteDirectory(root, recursive: true);
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

    public static void RunDurableReceipts(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
    {
        RunDurableDeathReplay(towns, pvf, characters, resources);
        RunDurablePickupReplay(towns, pvf, characters, resources);
        RunDurableEmptyReplay(towns, pvf, characters, resources);
        RunDurableLateRollback(towns, pvf, characters, resources);
        RunDurableTutorial(towns, pvf, characters, resources);
        RunDurableSchemaUpgrade();
        Run(towns, pvf, characters, resources);
    }

    public static void RunDurableSchemaUpgrade()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-durable-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        bool completed = false;
        try
        {
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("durable-schema", () => { });
            var role = repository.Create(owner, new NewCharacter("DurableDB", new(38, 0, 1677, 222), null)
                { DungeonState = CharacterDungeonState.NewOrdinary });
            string[] batchColumns = ["freeze_format", "combat_sequence", "committed_epoch", "map_snapshot", "uses_collection"];
            string[] pickupColumns = ["receipt_format", "destination_list", "epic_piece", "account_material"];
            using (var db = Open())
            using (var tx = db.BeginTransaction())
            {
                using var command = db.CreateCommand(); command.Transaction = tx;
                command.CommandText = "SELECT sql FROM sqlite_schema WHERE name='character_dungeon_drop_pickups'";
                string currentSql = (string)command.ExecuteScalar()!;
                string published = DungeonDropCollectionSchema.PickupDefinition;
                int currentStart = currentSql.IndexOf("CHECK((account_material", StringComparison.Ordinal);
                int currentEnd = currentSql.IndexOf("PRIMARY KEY(", currentStart, StringComparison.Ordinal);
                int publishedStart = published.IndexOf("CHECK((destination_slot", StringComparison.Ordinal);
                int publishedEnd = published.IndexOf("PRIMARY KEY(", publishedStart, StringComparison.Ordinal);
                SqliteMigrations.ReplaceTableConstraint(db, tx, "character_dungeon_drop_pickups",
                    currentSql[currentStart..currentEnd].TrimEnd(), published[publishedStart..publishedEnd].TrimEnd());
                tx.Commit();
            }
            foreach (string column in batchColumns) Sql("ALTER TABLE character_dungeon_drop_batches DROP COLUMN " + column);
            foreach (string column in pickupColumns) Sql("ALTER TABLE character_dungeon_drop_pickups DROP COLUMN " + column);
            Sql($$"""
                INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,
                  map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,
                  before_level,after_level,before_experience,after_experience,revision)
                  VALUES({{role.Id}},'durable-schema','legacy-event','legacy-instance',0,1,1,76126,20,1,1,1,'legacy','legacy',1,3,1,1,0,1,1);
                INSERT INTO character_dungeon_drop_batches(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,
                  map_id,monster_key,rule_id,resource_source,object_count)
                  VALUES({{role.Id}},'durable-schema','legacy-event','legacy-instance',0,1,1,76126,20,'legacy','原冻结',1);
                INSERT INTO character_dungeon_drop_objects VALUES({{role.Id}},'legacy-event','legacy-instance',0,400,'{}',0,100,100,0);
                INSERT INTO character_dungeon_drop_pickups(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,
                  destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision)
                  VALUES({{role.Id}},'durable-schema','legacy-event',400,'legacy-instance',1,1,3,2147483649,'{}',NULL,1,NULL,NULL,1);
                CREATE INDEX durable_legacy_index ON character_dungeon_drop_objects(core);
                CREATE TRIGGER durable_legacy_guard BEFORE INSERT ON character_dungeon_drop_objects
                  WHEN NEW.gold_amount=99 BEGIN SELECT RAISE(ABORT,'legacy guard'); END;
                PRAGMA user_version=159;
                """);
            // 第二张表的未知同名列使最终结构核验失败，前一张表的全部ALTER亦须回滚。
            Sql("ALTER TABLE character_dungeon_drop_pickups ADD COLUMN destination_list TEXT");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "v160拒绝未知同名列，不以列名存在替代结构合同");
            That(Query("PRAGMA user_version") == 159 &&
                Query("SELECT count(*) FROM pragma_table_info('character_dungeon_drop_batches') WHERE name='freeze_format'") == 0 &&
                Query("SELECT count(*) FROM pragma_table_info('character_dungeon_drop_pickups') WHERE name='receipt_format'") == 0 &&
                Query("SELECT item_uid FROM character_dungeon_drop_pickups") == 2147483649 &&
                SkillRepositoryTests.Equal(repository.LoadCharacter(owner, role.Slot), role),
                "v160晚期失败回滚前面新增列和版本，真实旧UID与角色资产保留");
            Sql("ALTER TABLE character_dungeon_drop_pickups DROP COLUMN destination_list");
            var upgraded = new CharacterRepository(path);
            That(Query("PRAGMA user_version") == SqliteMigrations.CurrentVersion &&
                Query("SELECT freeze_format FROM character_dungeon_drop_batches") == 0 &&
                Query("SELECT receipt_format FROM character_dungeon_drop_pickups") == 0 &&
                Query("SELECT item_uid FROM character_dungeon_drop_pickups") == 2147483649 &&
                Query("SELECT count(*) FROM character_dungeon_drop_batches WHERE resource_source='原冻结' AND map_snapshot IS NULL") == 1 &&
                Query("SELECT count(*) FROM character_dungeon_drop_objects WHERE core='{}' AND ground_key=400") == 1 &&
                Query("SELECT count(*) FROM sqlite_schema WHERE name IN ('durable_legacy_index','durable_legacy_guard')") == 2 &&
                Query("SELECT count(*) FROM pragma_foreign_key_check") == 0 &&
                SkillRepositoryTests.Equal(upgraded.LoadCharacter(owner, role.Slot), role),
                "真实v159升级保留旧行、完整64位UID、索引触发器及角色，不补发或提升历史格式");
            var reopened = new CharacterRepository(path);
            That(Query("SELECT count(*) FROM character_dungeon_drop_batches") == 1 &&
                Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 1 &&
                SkillRepositoryTests.Equal(reopened.LoadCharacter(owner, role.Slot), role),
                "v160重开不重复迁移、不扩充旧回执或资产");
            completed = true;
        }
        finally
        {
            if (completed) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.WriteLine("PRESERVED_DURABLE_FIXTURE_ROOT " + root);
        }
        Console.WriteLine($"PASS {Count - checks} 持久冻结v159升级保留与DDL回滚断言");

        long Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        void Sql(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open(); return db;
        }
    }

    public static void RunDurableTutorial(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-durable-tutorial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        bool completed = false;
        try
        {
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("durable-tutorial", () => { });
            var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
            var fixture = SkillRepositoryTests.Fixture("DurableTutor", towns, characters, learning) with
                { DungeonState = CharacterDungeonState.NewOrdinary, Tutorial = new(3) };
            var reserved = repository.ReserveDungeonAttempt(owner, repository.Create(owner, fixture), 500);
            var map = new DungeonMap("boss", new(0, 0), 76126, "map/durable-tutorial-fixture.map");
            var maze = new DungeonMaze(0, 1, 1, "合成训练冻结仓储", [map.Position], [map.Position], [map], []);
            var entry = new DungeonEntryPlan(reserved.Id, reserved.Revision, 3, 0, byte.MaxValue, 0, maze, map, map)
                { IsTutorial = true };
            var source = new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
                "[fixed]", "[normal]", "monster/durable-tutorial-fixture.mob");
            var monsters = new DungeonMonsterPlan(new(map, [source, source], [], []),
                [new(0, 20, 1, 3, source), new(1, 21, 1, 3, source)]);
            var authority = new FixtureAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([monsters], []), authority, out var run, out _),
                "训练确定掉落沿原Quest0、训练模式与可信死亡入口");
            var ground = new DungeonGroundState(run!, [monsters],
                [new(20, 400, map, 100, 100, 0), new(21, 401, map, 100, 100, 0)], "明确合成训练来源");
            That(run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                "训练死亡在原Run完成加载后准备");
            var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20)).Pending!;
            var now = DateTimeOffset.UtcNow;
            var core = InventoryRewardGrantService.Create(resources.Items.Get(1000), 1, now);
            int draws = 0;
            var loot = ground.GetOrFreeze(pending, () => { draws++; return core; });
            var original = loot.Ground!;
            var reward = new DungeonExperienceReward("fixture-only", DungeonKillExperienceCatalog.ResourcePath, 1, 3, 1);
            var progress = CharacterGrowthPlan.AddExperience(reserved, reward.Amount, growth).Plan!;
            var plan = new TutorialCheckpointPlan(reserved, pending.Death.EventId, pending.Death.Identity,
                map.Id, "death", pending.Death.MonsterKey, reward.Amount,
                new(reserved.Inventory, reserved.Inventory, "tutorial_death"),
                reserved.Tutorial! with { Completed = pending.Result.ClearFact is not null }, reserved.Position, progress)
                { Death = pending, DeathReward = reward, Loot = loot };
            Reject<InvalidDataException>(() => repository.CommitTutorialCheckpoint(owner, plan with
                { Loot = new(loot.Death, loot.CommittedIdentity, loot.Map, original with { ResourceIdentity = "changed-source" }) }),
                "训练单件冻结不能把对象来源与父死亡来源保存成不同值");
            Reject<InvalidDataException>(() => repository.CommitTutorialCheckpoint(owner, plan with
                { Loot = new(loot.Death, loot.CommittedIdentity, loot.Map, original with { UsesCollectionLedger = true }) }),
                "训练单件对象不能混用集合身份合同");
            Sql("CREATE TRIGGER durable_tutorial_fail BEFORE INSERT ON character_tutorial_receipts BEGIN SELECT RAISE(ABORT,'late tutorial receipt failure'); END");
            Reject<SqliteException>(() => repository.CommitTutorialCheckpoint(owner, plan),
                "训练最终receipt失败须回滚已写成长、状态及确定冻结");
            That(SkillRepositoryTests.Equal(repository.LoadCharacter(owner, reserved.Slot), reserved) &&
                Query("SELECT count(*) FROM character_tutorial_receipts") == 0 &&
                Query("SELECT count(*) FROM character_dungeon_growth_receipts") == 0 &&
                Query("SELECT count(*) FROM character_dungeon_drop_batches") == 0 &&
                Query("SELECT count(*) FROM character_dungeon_drop_objects") == 0 &&
                DungeonTransientReceipts.FindLoot(owner, reserved.Id, pending.Death.EventId) is null &&
                ground.FindAvailable(run.Snapshot.Identity, 400) is null,
                "训练晚期回滚不留下资产、revision、持久头或提前发布的缓存");
            Sql("DROP TRIGGER durable_tutorial_fail");
            var committed = repository.CommitTutorialCheckpoint(owner, plan);
            That(!committed.Duplicate && committed.Character.Revision == reserved.Revision + 1 &&
                committed.Character.Tutorial == plan.After &&
                Query("SELECT count(*) FROM character_tutorial_receipts") == 1 &&
                Query("SELECT count(*) FROM character_dungeon_growth_receipts") == 1 &&
                Query("SELECT count(*) FROM character_dungeon_drop_batches WHERE freeze_format=1 AND uses_collection=0") == 1 &&
                Query("SELECT count(*) FROM character_dungeon_drop_objects") == 1,
                "同一训练准备重试只提交一次成长、状态和单件完整冻结");
            DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
            var reopened = new CharacterRepository(path);
            var replay = reopened.CommitTutorialCheckpoint(owner, plan);
            That(replay.Duplicate && SkillRepositoryTests.Equal(replay.Character, committed.Character) && draws == 1 &&
                ReferenceEquals(ground.GetOrFreeze(pending, () => { draws++; return null; }), loot),
                "训练清cache重开仓储仍按SQL重放确定冻结，不重奖或重抽");
            Reject<InvalidDataException>(() => reopened.CommitTutorialCheckpoint(owner, plan with { Loot = null }),
                "训练重放不能省略原确定冻结");
            Reject<InvalidDataException>(() => reopened.CommitTutorialCheckpoint(owner, plan with
                { Loot = new(loot.Death, loot.CommittedIdentity, loot.Map, original with { Content = new(core with { Value = 2 }) }) }),
                "训练重放不能改变原单件Core或数量");
            That(run.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Accepted && ground.PublishCommitted(pending, loot),
                "训练事务重放后沿原Run确认并发布原单件");
            var pickup = ground.PreparePickup(run.Snapshot.Identity, original)!;
            var pickupPlan = DungeonPickupPlanning.Item(replay.Character, original.Content, resources.Items.Get(core.ItemId),
                resources.GoldLimits, resources.EpicPieces, now);
            That(pickupPlan.Accepted, "训练单件沿原正式拾取规划");
            DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
            var collected = reopened.CommitDungeonPickup(owner, replay.Character, pickup, pickupPlan.Plan!);
            DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
            var replayedPickup = new CharacterRepository(path).CommitDungeonPickup(owner, replay.Character, pickup, pickupPlan.Plan!);
            That(replayedPickup.Duplicate && replayedPickup.ProjectionCurrent && replayedPickup.ItemUid == collected.ItemUid &&
                SkillRepositoryTests.Equal(replayedPickup.Character, collected.Character) &&
                collected.Character.Inventory.Find(collected.Destination) is { } actual && actual.Core == core && actual.Uid == collected.ItemUid &&
                Query("SELECT count(*) FROM character_dungeon_drop_pickups WHERE receipt_format=1") == 1 &&
                ground.ConfirmPickup(pickup) && draws == 1,
                "训练来源与单件实际拾取都从持久事实重放，UID及完整Core只到账一次");
            completed = true;
        }
        finally
        {
            if (completed) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.WriteLine("PRESERVED_DURABLE_FIXTURE_ROOT " + root);
        }
        Console.WriteLine($"PASS {Count - checks} 训练确定冻结回滚与持久重放SQLite断言");

        long Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        void Sql(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open(); return db;
        }
    }

    public static void RunDurableDeathReplay(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
        => RunDurableReceiptCase(towns, pvf, characters, resources, 0);

    public static void RunDurablePickupReplay(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
        => RunDurableReceiptCase(towns, pvf, characters, resources, 1);

    public static void RunDurableEmptyReplay(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
        => RunDurableReceiptCase(towns, pvf, characters, resources, 2);

    public static void RunDurableLateRollback(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
        => RunDurableReceiptCase(towns, pvf, characters, resources, 3);

    static void RunDurableReceiptCase(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources, int scenario)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-durable-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        bool completed = false;
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("durable-drop-fixture", () => { });
            var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
            var fixture = SkillRepositoryTests.Fixture("DurableDrop", towns, characters, learning) with
            {
                DungeonState = CharacterDungeonState.NewOrdinary,
                ActiveMainQuests = [new(0, 3145, 1)]
            };
            var reserved = repository.ReserveDungeonAttempt(owner, repository.Create(owner, fixture), 500);
            var map = new DungeonMap("boss", new(0, 0), 76126, "map/durable-collection-fixture.map")
                { CandidateIds = [76126], CandidateWeights = [1] };
            var maze = new DungeonMaze(0, 1, 1, "合成持久集合仓储", [map.Position], [map.Position], [map], []);
            var entry = new DungeonEntryPlan(reserved.Id, reserved.Revision, 3, 3145, 0, 0, maze, map, map);
            var source = new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
                "[fixed]", "[normal]", "monster/durable-collection-fixture.mob");
            var monsters = new DungeonMonsterPlan(new(map, [source, source], [], []),
                [new(0, 20, 1, 3, source), new(1, 21, 1, 3, source)]);
            var authority = new FixtureAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([monsters], []), authority, out var run, out _),
                "持久集合沿既有Run与可信死亡入口，不建立第二死亡入口");
            var reservations = Enumerable.Range(0, 6).Select(index => new DungeonGroundReservation(20,
                checked((uint)(400 + index)), map, 100, 100, 0, index))
                .Append(new DungeonGroundReservation(21, 406, map, 100, 100, 0)).ToArray();
            var ground = new DungeonGroundState(run!, [monsters], reservations, "明确合成持久集合来源");
            That(run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                "持久集合使用完成加载的合法Run");
            var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20)).Pending!;
            var now = DateTimeOffset.UtcNow;
            DungeonGroundContent[] contents = scenario == 2 ? [] :
            [
                new(InventoryRewardGrantService.Create(resources.Items.Get(1000), 3, now)),
                new(InventoryRewardGrantService.Create(resources.Items.Get(3037), 7, now)),
                new(null, 25),
                new(DungeonDropCreation.Create(resources.Items.Get(31440), 1, _ => 0, now,
                    amplification: new EquipmentAmplificationDropPolicy(resources.Amplifications.Rules))),
                new(InventoryRewardGrantService.Create(resources.Items.Get(10300000), 3, now)),
                new(InventoryRewardGrantService.Create(resources.Items.Get(10308807), 3, now))
            ];
            int draws = 0;
            var loot = ground.GetOrFreezeAssets(pending, () => { draws++; return contents; }, "fixture/durable-death-v1");
            var reward = new DungeonExperienceReward("fixture-only", DungeonKillExperienceCatalog.ResourcePath, 1, 3, 1);

            if (scenario == 3)
            {
                Sql("CREATE TRIGGER durable_object_fail AFTER INSERT ON character_dungeon_drop_objects WHEN NEW.ordinal=5 BEGIN SELECT RAISE(ABORT,'late durable object failure'); END");
                Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot),
                    "最后一件冻结对象SQL失败须回滚同笔成长和前面全部冻结对象");
                That(Query("SELECT count(*) FROM character_dungeon_growth_receipts") == 0 &&
                    Query("SELECT count(*) FROM character_dungeon_drop_batches") == 0 &&
                    Query("SELECT count(*) FROM character_dungeon_drop_objects") == 0 &&
                    SkillRepositoryTests.Equal(repository.LoadCharacter(owner, reserved.Slot), reserved) &&
                    ground.FindAvailable(run.Snapshot.Identity, 400) is null,
                    "晚期冻结失败没有半笔成长、半个集合、提前入包或提前发布");
                Sql("DROP TRIGGER durable_object_fail");
            }

            var committed = repository.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot);
            That(committed.Character.Revision == reserved.Revision + 1 &&
                committed.Character.Inventory.Gold == reserved.Inventory.Gold &&
                committed.Character.Inventory.Items.SequenceEqual(reserved.Inventory.Items),
                "死亡提交仅形成原冻结资产，拾取前不提前发放实体或金币");

            if (scenario is 0 or 2)
            {
                DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
                var reopened = new CharacterRepository(path);
                DungeonGrowthCommit? replay = null;
                try { replay = reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot); }
                catch (InvalidDataException error) when (error.Message == "本实例的临时冻结掉落缺失，不能恢复旧副本") { }
                That(replay is not null, "清缓存并重开仓储后必须从原死亡事务重放完整冻结，不依赖瞬态掉落缓存");
                That(replay!.Duplicate && SkillRepositoryTests.Equal(replay.Character, committed.Character) && draws == 1,
                    "清掉原临时缓存后，同合法pending从SQL重放完整死亡结果且不重复经验或抽样");
                Sql("UPDATE character_dungeon_drop_batches SET freeze_format=0");
                DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
                Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth, loot),
                    "历史格式冻结头不能因同event或同数值key被升级成当前恢复资格");
                That(SkillRepositoryTests.Equal(reopened.LoadCharacter(owner, reserved.Slot), committed.Character) &&
                    Query("SELECT count(*) FROM character_dungeon_drop_batches WHERE freeze_format=0") == 1,
                    "拒绝历史格式保留原历史行及实际角色资产");
                Sql("UPDATE character_dungeon_drop_batches SET freeze_format=1");
                That(ReferenceEquals(ground.GetOrFreezeAssets(pending, () => { draws++; return []; }, "fixture/durable-death-v1"), loot) && draws == 1,
                    "原Run保留同一冻结结果，结果重放不调用另一份生成器");
                Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth),
                    "已持久冻结的结果不能在死亡重放时省略，空集合也不是未规划");
                Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth,
                    new(loot.Death, loot.CommittedIdentity with { Epoch = loot.CommittedIdentity.Epoch + 1 }, loot.Map,
                        loot.Grounds, loot.ResourceIdentity)), "死亡重放核对冻结结果的提交后epoch");
                Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth,
                    new(loot.Death, loot.CommittedIdentity, loot.Map, loot.Grounds, "changed-source")),
                    "死亡重放核对完整来源身份");
                Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth,
                    new(loot.Death, loot.CommittedIdentity, loot.Map with { CandidateWeights = [2] }, loot.Grounds, loot.ResourceIdentity)),
                    "持久地图身份不只比较map_id，也保留候选值与权重");
                if (scenario == 0)
                {
                    var first = loot.Grounds[0];
                    Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth,
                        new(loot.Death, loot.CommittedIdentity, loot.Map, loot.Grounds.RemoveAt(3), loot.ResourceIdentity)),
                        "持久死亡重放不能截掉金币或装备尾项");
                    Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth,
                        new(loot.Death, loot.CommittedIdentity, loot.Map,
                            loot.Grounds.SetItem(0, first with { Content = new(first.Core with { Value = 4 }) }), loot.ResourceIdentity)),
                        "持久死亡重放不能改变原三件数量或完整Core");
                    Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth,
                        new(loot.Death, loot.CommittedIdentity, loot.Map,
                            loot.Grounds.SetItem(0, first with { DeathIdentity = first.DeathIdentity with { TaskAttempt = 2 } }), loot.ResourceIdentity)),
                        "持久死亡重放不能借用同数值key的另一attempt");
                    Reject<InvalidDataException>(() => reopened.CommitDungeonGrowth(owner, reserved, run, pending, reward, growth,
                        new(loot.Death, loot.CommittedIdentity, loot.Map,
                            loot.Grounds.SetItem(0, first with { Reservation = first.Reservation with { X = 101 } }), loot.ResourceIdentity)),
                        "持久死亡重放保留原地面坐标和预留身份");
                }
                That(run.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Accepted && ground.PublishCommitted(pending, loot),
                    "SQL死亡重放完成后仍沿原Run确认并发布原对象");
                That(ground.Snapshot.Items.Length == (scenario == 2 ? 0 : 6) &&
                    Query("SELECT count(*) FROM character_dungeon_drop_batches") == 1 &&
                    Query("SELECT count(*) FROM character_dungeon_drop_objects") == (scenario == 2 ? 0 : 6),
                    "空集合有持久死亡头且零对象；命中保留六个独立对象");
            }
            else
            {
                That(run.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Accepted && ground.PublishCommitted(pending, loot),
                    "完整冻结提交后才开放逐件拾取");
                Sql("INSERT INTO sqlite_sequence(name,seq) SELECT 'character_inventory_items',2147483647 " +
                    "WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='character_inventory_items')");
                Sql("UPDATE sqlite_sequence SET seq=2147483647 WHERE name='character_inventory_items'");
                That(committed.Character.Inventory.Items.Length == 0 &&
                    Query("SELECT count(*) FROM character_inventory_items") == 0 &&
                    Query("SELECT count(*) FROM sqlite_sequence WHERE name='character_inventory_items'") == 1 &&
                    Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'") == int.MaxValue,
                    "空库存拾取夹具明确建立唯一的64位UID序列前置");
                Character current = committed.Character;
                foreach (var item in loot.Grounds)
                {
                    var pickup = ground.PreparePickup(run.Snapshot.Identity, item)!;
                    var plan = item.IsGold ? DungeonPickupPlanning.Gold(current, item.Content, resources.GoldLimits) :
                        DungeonPickupPlanning.Item(current, item.Content, resources.Items.Get(item.Core.ItemId), resources.GoldLimits, resources.EpicPieces, now);
                    That(plan.Accepted, "冻结道具、账号材料、金币与装备均沿真实拾取规划");
                    if (scenario == 3 && item.Reservation.Ordinal is 0 or 4 or 5)
                    {
                        Sql("CREATE TRIGGER durable_pickup_fail AFTER INSERT ON character_dungeon_drop_pickups BEGIN SELECT RAISE(ABORT,'late durable pickup failure'); END");
                        Reject<SqliteException>(() => repository.CommitDungeonPickup(owner, current, pickup, plan.Plan!, resources.EpicPieces, resources.Items),
                            "实际拾取receipt晚期失败须回滚已写库存、UID和revision");
                        That(SkillRepositoryTests.Equal(repository.LoadCharacter(owner, current.Slot), current) &&
                            Query("SELECT count(*) FROM character_dungeon_drop_pickups") == item.Reservation.Ordinal &&
                            repository.LoadCharacter(owner, current.Slot).Inventory.EpicPieces.Same(current.Inventory.EpicPieces),
                            "拾取晚期失败没有库存、UID、账号图鉴或去重半笔");
                        Sql("DROP TRIGGER durable_pickup_fail");
                        DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
                    }
                    var before = current;
                    var result = repository.CommitDungeonPickup(owner, before, pickup, plan.Plan!, resources.EpicPieces, resources.Items);
                    DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
                    var reopened = new CharacterRepository(path);
                    var replay = reopened.CommitDungeonPickup(owner, before, pickup, plan.Plan!, resources.EpicPieces, resources.Items);
                    That(replay.Duplicate && replay.ProjectionCurrent && replay.Destination == result.Destination &&
                        replay.ItemUid == result.ItemUid && replay.Currency == result.Currency && replay.Amount == result.Amount &&
                        replay.EpicPiece == result.EpicPiece && replay.AccountMaterial == result.AccountMaterial &&
                        SkillRepositoryTests.Equal(replay.Character, result.Character),
                        "拾取成功后清cache并重开Repository只回放实际UID、槽、数量和当前资产");
                    if (item.Reservation.Ordinal == 4)
                    {
                        That(result.ItemUid == 0 && result.Currency is null &&
                            result.EpicPiece is { PieceId: 10300000, Amount: 3, Balance: 3 } piece &&
                            piece.BookRevision == before.Inventory.EpicPieces.Revision + 1 &&
                            replay.Character.Inventory.Items.SequenceEqual(before.Inventory.Items) &&
                            replay.Character.Inventory.EpicPieces.Get(piece.Index) == 3,
                            "碎片真实图鉴余额及bookRevision持久重放，不伪造实体UID或货币");
                        Sql("UPDATE character_dungeon_drop_pickups SET epic_piece=json_set(epic_piece,'$.Index',99) WHERE ground_key=" + item.Reservation.GroundKey);
                        DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
                        Reject<InvalidDataException>(() => reopened.CommitDungeonPickup(owner, before, pickup, plan.Plan!, resources.EpicPieces, resources.Items),
                            "碎片JSON索引必须与原冻结物品及实际资源目录一致，不能借另一图鉴条目重放");
                        Sql("UPDATE character_dungeon_drop_pickups SET epic_piece=json_set(epic_piece,'$.Index',0) WHERE ground_key=" + item.Reservation.GroundKey);
                        var saved = reopened.Save(owner, replay.Character, replay.Character.Position, replay.Character.Return);
                        DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
                        var late = new CharacterRepository(path).CommitDungeonPickup(owner, before, pickup, plan.Plan!, resources.EpicPieces, resources.Items);
                        That(late.Duplicate && !late.ProjectionCurrent && late.EpicPiece == result.EpicPiece &&
                            SkillRepositoryTests.Equal(late.Character, saved),
                            "碎片回执晚到保留当前角色revision，只关闭过期投影而不重复加图鉴");
                        result = result with { Character = saved };
                    }
                    if (item.Reservation.Ordinal is 0 or 3)
                        That(result.ItemUid > int.MaxValue && result.Character.Inventory.Find(result.Destination) is { } actual &&
                            actual.Uid == result.ItemUid && actual.Core == item.Core,
                            "持久拾取receipt保留64位UID及原完整Core，三件消耗品不变一件");
                    if (item.Reservation.Ordinal == 5)
                    {
                        That(result.ItemUid == 0 && result.Currency is null && result.EpicPiece is null &&
                            result.AccountMaterial is { ItemId: 10308807, Amount: 3, Balance: 3 } material &&
                            material.WalletRevision == before.Inventory.AccountWallet.Revision + 1 &&
                            replay.Character.Inventory.Items.SequenceEqual(before.Inventory.Items) &&
                            replay.Character.Inventory.AccountWallet.Material(material.ItemId) is { Balance: 3, RaidEarned: 0 },
                            "账号材料持久回执携带实际钱包版本，不建实体、不占周额度、清缓存不重复到账");
                        Sql("UPDATE character_dungeon_drop_pickups SET account_material=json_set(account_material,'$.ItemId',10305912) WHERE ground_key=" + item.Reservation.GroundKey);
                        Reject<InvalidDataException>(() => reopened.CommitDungeonPickup(owner, before, pickup, plan.Plan!, resources.EpicPieces, resources.Items),
                            "持久账号材料JSON不能借另一材料编号重放");
                        Sql("UPDATE character_dungeon_drop_pickups SET account_material=json_set(account_material,'$.ItemId',10308807) WHERE ground_key=" + item.Reservation.GroundKey);
                        var saved = reopened.Save(owner, replay.Character, replay.Character.Position, replay.Character.Return);
                        var late = reopened.CommitDungeonPickup(owner, before, pickup, plan.Plan!, resources.EpicPieces, resources.Items);
                        That(late.Duplicate && !late.ProjectionCurrent && late.AccountMaterial == result.AccountMaterial &&
                            SkillRepositoryTests.Equal(late.Character, saved), "账号材料迟到收据保留当前角色，旧版本不重发钱包结果");
                        result = result with { Character = saved };
                    }
                    if (item.Reservation.Ordinal == 0)
                    {
                        Sql("UPDATE character_dungeon_drop_pickups SET receipt_format=0");
                        DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
                        Reject<InvalidDataException>(() => reopened.CommitDungeonPickup(owner, before, pickup, plan.Plan!, resources.EpicPieces, resources.Items),
                            "历史格式拾取回执不能充当当前格式的去重成功");
                        That(SkillRepositoryTests.Equal(reopened.LoadCharacter(owner, before.Slot), result.Character) &&
                            Query("SELECT count(*) FROM character_dungeon_drop_pickups WHERE receipt_format=0") == 1,
                            "拒绝历史拾取保留真实UID、数量与历史回执");
                        Sql("UPDATE character_dungeon_drop_pickups SET receipt_format=1");
                    }
                    var changedContent = item.IsGold ? new DungeonGroundContent(null, 26) :
                        new DungeonGroundContent(item.Core with { Value = item.Core.Value + 1 });
                    Reject<InvalidDataException>(() => reopened.CommitDungeonPickup(owner, before,
                        new(pickup.Identity, item with { Content = changedContent }), plan.Plan!, resources.EpicPieces, resources.Items),
                        "已领取key重放仍复验原完整内容，不能以receipt绕过来源校验");
                    Reject<InvalidDataException>(() => reopened.CommitDungeonPickup(owner, before,
                        new(pickup.Identity with { InstanceId = Guid.NewGuid() }, item), plan.Plan!, resources.EpicPieces, resources.Items),
                        "同数值key不能借另一实例回放原拾取");
                    DungeonTransientReceipts.Forget(owner, run.Snapshot.Identity);
                    var twice = new CharacterRepository(path).CommitDungeonPickup(owner, before, pickup, plan.Plan!, resources.EpicPieces, resources.Items);
                    That(twice.Duplicate && twice.ItemUid == result.ItemUid &&
                        SkillRepositoryTests.Equal(twice.Character, result.Character) && ground.ConfirmPickup(pickup),
                        "反复失去临时receipt也只消费一次原地面对象");
                    current = twice.Character;
                }
                That(Query("SELECT count(*) FROM character_dungeon_drop_pickups") == 6 &&
                    current.Revision == committed.Character.Revision + 8 &&
                    current.Inventory.EpicPieces.Get(0) == 3 &&
                    current.Inventory.AccountWallet.Material(10308807) is { Balance: 3, RaidEarned: 0 } &&
                    current.Inventory.Gold == reserved.Inventory.Gold + 25 &&
                    current.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == reserved.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) + 7 &&
                    current.Inventory.Items.Single(item => item.Core.ItemId == 1000).Core.Count == 3 &&
                    current.Inventory.Items.Single(item => item.Core.ItemId == 31440 && item.Position.List == InventoryListType.Main).Core == contents[3].Item &&
                    draws == 1, "六件资产及六个持久拾取各一次，重复结果不增加数量、经验或抽样");
            }
            completed = true;
        }
        finally
        {
            if (completed) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.WriteLine("PRESERVED_DURABLE_FIXTURE_ROOT " + root);
        }
        Console.WriteLine($"PASS {Count - checks} 持久冻结与拾取真实SQLite断言 case={scenario}（原Run仍在，非跨OS恢复）");

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
