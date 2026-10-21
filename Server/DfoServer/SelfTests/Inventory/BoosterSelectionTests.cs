using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class BoosterSelectionTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException) { failed = true; }
            That(failed, name);
        }
        var catalog = itemResources.Items;
        LegacyExpiryFixture.Restore(catalog, (10002064, "2013-09-24 06:00:00"));
        var service = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces);
        var now = DateTimeOffset.UtcNow;
        RunRealTemplateSelection(towns, itemResources, now);
        BoosterNativeSelectionTests.RunResources(towns, characters, itemResources, now);
        RunDisjointInstanceSelection(towns, itemResources, now);
        var creatureCategory = Parse("0 0\n[creature]\n400990444 1 400990445 1\n[/creature]").Selection!.Categories.Single();
        That(creatureCategory.PendingRule is null && creatureCategory.Rewards.All(row =>
                row.NativeCreatureSelectionRow && row.RequiredKind is null),
            "选择段保留原生来源和完整候选，由真实模板决定产物类型");
        foreach (var rewardId in new[] { 400990444, 400990445 })
        {
            var petService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces,
                debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true });
            var source = new InventoryItem(1000, Bag(70), InventoryRewardGrantService.Create(catalog.Get(490705390), shopExpiration: true));
            Character petCharacter = new(1, 0, "PetBox", 0, towns.Room, towns.InitialReturn) { Inventory = new([source]) };
            var petResult = petService.Open(petCharacter, Select(70, [rewardId]), out var petReceipt, now);
            That(petResult.Accepted && petResult.Plan!.After.Items.Single() is { Core.Kind: ItemKind.Creature, Creature: not null, Uid: 0 } pet &&
                pet.Core.ItemId == rewardId && petReceipt!.Rewards.Single().Count == 1 && petCharacter.Inventory.Items.Single() == source,
                "真实宠物礼盒两项选择分别生成完整宠物详情，不提前消耗原背包");
            That(!petService.Open(petCharacter, Select(70, [400990444, 400990445]), out _).Accepted,
                "宠物礼盒仍拒绝超额选择");
            var range = ItemSlotBoundService.Range(ItemKind.Creature, petCharacter.Inventory);
            var pets = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                InventoryRewardGrantService.CreateItem(catalog.Get(rewardId), new(InventoryListType.Pet, (short)slot),
                    InventoryRewardGrantService.Create(catalog.Get(rewardId)))).ToArray();
            var fullPetCharacter = petCharacter with { Inventory = new([source, .. pets]) };
            That(!petService.Open(fullPetCharacter, Select(70, [rewardId]), out _, now).Accepted &&
                fullPetCharacter.Inventory.Find(Bag(70)) == source, "宠物页满时拒绝且保留礼包，不能借用Main空格");
            var lastSlotCharacter = petCharacter with { Inventory = new([source, .. pets[..^1]]) };
            var lastSlot = petService.Open(lastSlotCharacter, Select(70, [rewardId]), out _, now);
            That(lastSlot.Accepted && lastSlot.Plan!.After.Items.Length == pets.Length &&
                lastSlot.Plan.After.Find(new(InventoryListType.Pet, range.End)) is { Creature: not null },
                "宠物页仅剩一个槽时完整发放，边界槽不能误拒绝");
        }
        var current = new Character(1, 0, "Selection", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        var grouped = itemResources.Boosters.Get(8403).Selection!;
        That(grouped.SelectionCount == 1 && grouped.Categories.Length == 28 &&
            grouped.Categories.Single(row => row.Category == 0 && row.Subcategory == 1).Rewards[0] == new BoosterChoice(100332, 1),
            "two PVF category coordinates remain distinct, not flattened or inferred from character job");
        var parsed = Parse("0 0\n[stackable]\n1106 7 1112 9\n[/stackable]\n[etc]\n1106 3\n[/etc]", 0);
        That(parsed.Selection!.Categories[0].Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1112, 9), new(1106, 3) }),
            "selection rewards are item/count pairs and repeated resource rows remain intact");
        var hints = Parse("0 0\n[default select]\n2 1106 999999999\n[recommend]\n1 1112\n" +
            "[stackable]\n1106 7 1112 9\n[/stackable]\n[default select]\n1 1106\n[recommend]\n0", 2).Selection!.Categories[0];
        That(hints.PendingRule is null && hints.DefaultItemIds.SequenceEqual(new[] { 1106, 999999999, 1106 }) &&
            hints.RecommendedItemIds.SequenceEqual(new[] { 1112 }) &&
            hints.Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1112, 9) }),
            "unclosed hint fields preserve IDs without adding rewards, replacing quantities or requiring current candidates");
        That(Parse("0 0\n[default select]\n1 1106\n[etc]\n1106 7 1106 3\n[/etc]", 0)
            .Selection!.Categories[0].Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1106, 3) }),
            "full-group hint does not append another reward to repeated resource rows");
        foreach (string bad in new[] { "0 0\n[etc]\n1106\n[/etc]", "0 0\n[etc]\n1106 0\n[/etc]",
            "0 0\n[etc]\n-1 2\n[/etc]", "0 0\n[equipment]\n16400 1\n[/equipment]\n[avatar]\n1 1 1 1\n[/avatar]",
            "0 0\n[etc]\n1106 2\n[/etc]\n99", "0 0\n[default select]\n2 1106\n[etc]\n1106 2\n[/etc]",
            "0 0\n[recommend]\n1 1106 1112\n[etc]\n1106 2\n[/etc]", "0 0\n[default select]\n-1\n[etc]\n1106 2\n[/etc]",
            "0 0\n[recommend]\n1 x\n[etc]\n1106 2\n[/etc]", "0 0\n[default select]\n1 1106\n[/default select]",
            "0 0\n[stackable]\n1106 2\n[etc]\n1112 1\n[/etc]", "0 0\n[etc]\n1106 2\n[/stackable]",
            "0 0\n[default select]\n1 1106\n[booster equipment grade]\n6\n[equipment]\n16400 1\n[/equipment]" })
            That(Parse(bad).Selection!.Categories[0].PendingRule is not null, "unknown or malformed category never grants a truncated subset");
        That(!Parse("256 0\n[etc]\n1106 1\n[/etc]").Available && !Parse("0 0", -1).Available,
            "invalid category key and selection count reject definition");
        That(!BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[/booster select category]\n[booster select category]\n0 0\n[/booster select category]").Available,
            "duplicate category cannot replace an existing reward group");
        byte[] body = [3, 0, 1, 0, 0, 0, 2, 1, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 1106);
        var request = BoosterRequests.Read(body);
        That(request.Slot == 3 && request.Count == 1 && request.Selection is { Category: 2, Subcategory: 1 } &&
            request.Selection.ItemIds.SequenceEqual(new[] { 1106 }), "native 6-byte prefix, two category bytes, i32 ids and two zero option counts");
        foreach (int width in new[] { 7, 8, 9, 11, 12, 13, 15 })
            Reject(() => BoosterRequests.Read(new byte[width]), "truncated selection width " + width);
        body[^1] = 1;
        That(!BoosterRequests.IsItemSelection(body), "extra detail options are not parsed as ordinary item selection");
        Reject(() => BoosterRequests.Read(body), "nonzero detail tail is not discarded");

        current = current with { Inventory = new([new(1, Bag(3), Core(10000743, 2) with { SortLockFlag = 1 }),
            new(2, Bag(65), Core(10000871, 5) with { SortLockFlag = 1 })], Gold: 12345) };
        var before = current.Inventory;
        var result = service.Open(current, Select(3, [10000871], count: 2), out var receipt, now);
        That(result.Accepted && current.Inventory == before && result.Plan!.After.Find(Bag(3)) is null &&
            result.Plan.After.Find(Bag(65)) is { Uid: 2, Core.Value: 7, Core.SortLockFlag: 1 },
            "quickbar and sort lock permit use, identical reward merges without changing the existing UID");
        That(receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(10000871, 2) }) && result.Plan!.After.Gold == 12345,
            "selected reward count comes from PVF times opened count, without extra price or material costs");
        foreach (var bad in new[] { Select(3, []), Select(3, [1106]), Select(3, [10000871, 10000871]),
            Select(3, [10000871, 10000872]), Select(3, [10000871], 1), Select(3, [10000871], 0, 1),
            Select(3, [10000871], count: 0), Select(3, [10000871], count: 3), new BoosterOpenRequest(3, 1) })
            That(!service.Open(current, bad, out _).Accepted && current.Inventory == before, "invalid selection keeps all source items and assets");
        foreach (var core in new[] { Core(10000743) with { EquipmentLockId = 1 },
            Core(10000743) with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) }, Core(7542) })
            That(!service.Open(current with { Inventory = new([new(1, Bag(3), core)]) }, Select(3, [10000871]), out _, now).Accepted,
                "safety lock, expiry and cross-kind request reject before consumption");

        var filled = Enumerable.Range(65, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(1108, 1000))).ToImmutableArray();
        filled = filled.SetItem(0, new(65, Bag(65), Core(10000743)));
        current = current with { Inventory = new(filled) };
        result = service.Open(current, Select(65, [10000871]), out _);
        That(result.Accepted && result.Plan!.After.Find(Bag(65)) is { Uid: 0, Core.ItemId: 10000871 } && result.Plan.After.Items.Length == 56,
            "full page uses the exhausted source slot rather than requiring an extra empty slot");
        current = current with { Inventory = new(filled.SetItem(0, filled[0] with { Core = Core(10000743, 2) })) };
        That(!service.Open(current, Select(65, [10000871]), out _).Accepted && current.Inventory.Find(Bag(65))!.Core.Count == 2,
            "capacity failure retains source when its slot cannot be freed");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012332))]) };
        result = service.Open(current, Select(3, [109000071]), out _);
        That(result.Accepted && result.Plan!.After.Items.Single().Core == Core(109000071) && current.Level == 1 && current.Job == 15,
            "equipment keeps full seal, durability, quality and expiry without an output wear-level or job restriction");
        current = current with { Inventory = new([new(1, Bag(3), Core(8403))]) };
        That(service.Open(current, Select(3, [100332], 0, 1), out _).Accepted &&
            !service.Open(current, Select(3, [100332], 0, 0), out _).Accepted &&
            !service.Open(current, Select(3, [100332], 3, 2), out _).Accepted, "correct second category succeeds; cross-category and empty groups reject");

        current = current with { Inventory = new([new(1, Bag(3), Core(10153913))]) };
        result = service.Open(current, Select(3, [100050655, 100100567, 100150535]), out receipt);
        That(result.Accepted && receipt!.Rewards.Length == 3 && result.Plan!.After.Items.Length == 3, "multiple selections grant every chosen equipment");
        That(service.Open(current, Select(3, [100050655]), out _).Accepted, "selection count is the resource maximum, not an added minimum");
        That(!service.Open(current, Select(3, [100050655, 100100567, 100150535, 100200510]), out _).Accepted,
            "selection cannot exceed the resource quota");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012510))]) };
        That(service.Open(current, Select(3, [10012190]), out _).Accepted &&
            !service.Open(current, Select(3, [100350302]), out _).Accepted,
            "an unchosen missing template does not block a valid independent choice");
        result = service.Open(current, Select(3, [63220]), out _);
        That(result.Accepted && result.Plan!.After.Items.Single() is { Core.ItemId: 63220, Core.Kind: ItemKind.Creature, Creature: not null },
            "a selected supported creature is granted with its independent detail and complete core");
        int[] all = [100300855, 100312548, 100322438, 100344654, 100352921];
        current = current with { Inventory = new([new(1, Bag(3), Core(10161354))]) };
        result = service.Open(current, Select(3, all.Reverse().ToArray()), out receipt);
        That(result.Accepted && receipt!.Rewards.Select(row => row.ItemId).SequenceEqual(all) && result.Plan!.After.Items.Length == 5,
            "selection count zero grants all rewards in the selected group, independent of submitted order");
        That(!service.Open(current, Select(3, all[..4]), out _).Accepted && !service.Open(current, Select(3, [.. all, all[0]]), out _).Accepted,
            "full-group echo cannot omit or duplicate rewards");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012104))]) };
        result = service.Open(current, Select(3, [101000007]), out receipt);
        That(result.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(101000007, 1) }) &&
            result.Plan!.After.Items.Single().Core == Core(101000007),
            "stale default ID outside the candidate list neither blocks a valid choice nor creates an extra item");
        That(!service.Open(current, Select(3, [10000219]), out _).Accepted &&
            !service.Open(current, Select(3, [101000007, 10000219]), out _).Accepted,
            "default hint never authorizes an item outside the real reward list or an extra selection");
        current = current with { Inventory = new([new(1, Bag(3), Core(10002056))]) };
        That(!service.Open(current, Select(3, [3290, 3311]), out _).Accepted, "觉醒之息源盒最低75级在完整合法选择时仍生效");
        current = current with { Level = 75 };
        result = service.Open(current, Select(3, [3290, 3311]), out receipt);
        That(result.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(3290, 88), new(3311, 88) }) &&
            result.Plan!.After.Items.Length == 2 && result.Plan.After.Items.All(item => item.Core.Count == 88),
            "multiple defaults use actual row quantities, allow fewer than the selection maximum and do not auto-grant unchosen expired defaults");
        That(!service.Open(current, Select(3, [10002064]), out _).Accepted && current.Inventory.Items.Length == 1,
            "a default that is actually selected still needs a usable complete reward template");
        current = current with { Level = 16, Inventory = new([new(1, Bag(3), Core(2749783))]) };
        var historical = new DateTimeOffset(2013, 3, 1, 0, 0, 0, TimeSpan.Zero);
        result = service.Open(current, Select(3, [2749770]), out receipt, historical);
        That(result.Accepted && receipt!.Rewards.Single() == new BoosterGrantedItem(2749770, 5) &&
            result.Plan!.After.Items.Single().Core.Count == 5,
            "default list leading count and repeated ID do not replace or add to the configured five-item reward");

        current = current with { Level = 75, Inventory = new(filled.SetItem(0, new(65, Bag(65), Core(10002056)))) };
        var materialPage = Enumerable.Range(121, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(3290, int.MaxValue)));
        current = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.AddRange(materialPage) } };
        before = current.Inventory;
        That(!service.Open(current, Select(65, [3311]), out _).Accepted && current.Inventory == before,
            "default selection still checks real output capacity and keeps the source and every existing instance on failure");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("selection-test", () => { });
            repo.Create(owner, new("SelectOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), Core(10161354)), new(0, Bag(4), Core(10000743, 2))])
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var saved = session.Selected!;
            string snapshot = Snapshot();
            Query("CREATE TRIGGER fail_selection BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=10 BEGIN SELECT RAISE(ABORT,'selection rollback'); END");
            Reject(() => session.MutateInventory(role => service.Open(role, Select(3, all), out _)), "later reward insert failure propagates");
            That(session.Selected == saved && Snapshot() == snapshot, "source, already-written rewards, revisions, audit and UID sequence roll back together");
            Query("DROP TRIGGER fail_selection");
            var stalePlan = service.Open(saved, Select(4, [10000871]), out _).Plan!;
            That(session.MutateInventory(role => service.Open(role, Select(3, all), out _)).Accepted, "complete all-group plan commits once");
            Reject(() => repo.CommitInventory(owner, saved, stalePlan), "stale revision cannot commit a selection computed before another asset change");
            That(session.MutateInventory(role => service.Open(role, Select(4, [10000871], count: 2), out _)).Accepted, "batch selection commits through the shared transaction");
            snapshot = Snapshot();
            That(!session.MutateInventory(role => service.Open(role, Select(3, all), out _)).Accepted && Snapshot() == snapshot,
                "exhausted request replay creates no extra reward or audit");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                "repository reopen retains complete equipment and consumable rewards");
            That(Query("SELECT COUNT(DISTINCT item_uid) FROM character_inventory_items") is 6L, "every final physical instance has a unique persisted UID");
            repo.Create(owner, new("RookieSet", towns.Room, towns.InitialReturn)
            {
                Job = 15, Inventory = new([new(0, Bag(3), Core(490704519))])
            });
            var rookieSession = new GameSession(repo, towns, owner);
            rookieSession.Select(1);
            rookieSession.MarkReady();
            int[] rookieSet = [400100084, 400150076, 400200076, 400250082, 400050092];
            var rookieSaved = rookieSession.Selected;
            snapshot = Snapshot();
            Query("CREATE TRIGGER fail_rookie_set BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=10 BEGIN SELECT RAISE(ABORT,'rookie set rollback'); END");
            Reject(() => rookieSession.MutateInventory(role => service.Open(role, Select(3, rookieSet), out _, now)),
                "新秀整套奖励后续写入失败传播");
            That(rookieSession.Selected == rookieSaved && Snapshot() == snapshot,
                "限定实例分解兼容仍保留源、已写产物、审计与UID序列的整组回滚");
            Query("DROP TRIGGER fail_rookie_set");
            That(rookieSession.MutateInventory(role => service.Open(role, Select(3, rookieSet), out _, now)).Accepted,
                "新秀五件套沿真实Session事务完整提交");
            var rookieReopened = new CharacterRepository(path).List(owner).Single(role => role.Name == "RookieSet");
            That(JsonSerializer.Serialize(rookieReopened) == JsonSerializer.Serialize(rookieSession.Selected) &&
                rookieReopened.Inventory.Items.Length == 5 && rookieReopened.Inventory.Items.All(item => item.Uid > 0 &&
                    item.Core.ExpireTime == now.ToUnixTimeSeconds() + 30 * 86400) &&
                rookieReopened.Inventory.Items.Select(item => item.Uid).Distinct().Count() == 5,
                "重开库保留新秀整套独立UID、完整核心及30天期限");
            snapshot = Snapshot();
            That(!rookieSession.MutateInventory(role => service.Open(role, Select(3, rookieSet), out _, now)).Accepted && Snapshot() == snapshot,
                "新秀整套成功后的耗尽重放不再发奖或记审计");
            repo.Create(owner, new("NativeAura", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(70), InventoryRewardGrantService.Create(catalog.Get(10007996), 2, now, shopExpiration: true))])
            });
            var auraSaved = repo.List(owner).Single(role => role.Name == "NativeAura");
            var nativeService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits,
                itemResources.EpicPieces, debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true }, avatars: itemResources.AvatarPackages);
            var auraPlan = nativeService.Open(auraSaved, Select(70, [101590027]), out _, now).Plan!;
            snapshot = Snapshot();
            Query("CREATE TRIGGER fail_native_aura BEFORE INSERT ON inventory_audit_log WHEN NEW.item_kind_after=8 BEGIN SELECT RAISE(ABORT,'native aura rollback'); END");
            Reject(() => repo.CommitInventory(owner, auraSaved, auraPlan), "光环详情及槽位已写入后，末段审计故障仍传播失败");
            That(Snapshot() == snapshot && Query("SELECT count(*) FROM inventory_avatar_details") is 0L,
                "光环末段故障完整回滚源、核心、槽、详情、UID、revision及审计");
            Query("DROP TRIGGER fail_native_aura");
            var auraCommitted = repo.CommitInventory(owner, auraSaved, auraPlan);
            That(JsonSerializer.Serialize(repo.List(owner).Single(role => role.Name == "NativeAura")) == JsonSerializer.Serialize(auraCommitted) &&
                auraCommitted.Inventory.Find(Bag(70)) is { Core.Count: 1 } &&
                auraCommitted.Inventory.Items.Single(item => item.Core.Kind == ItemKind.Avatar) is { Uid: > 0, Avatar: not null },
                "确定回滚后原合法光环计划可完整提交一次，保存重读保留详情与剩余源");
            snapshot = Snapshot();
            Reject(() => repo.CommitInventory(owner, auraSaved, auraPlan), "已提交的旧光环计划不可再次提交");
            That(Snapshot() == snapshot, "光环旧revision拒绝不修改详情、UID或资产");
            var nextAuraPlan = nativeService.Open(auraCommitted, Select(70, [101590028]), out _, now).Plan!;
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(role => service.Open(role, Select(4, [10000871]), out _)), "old generation cannot open or replay a selection");
            Reject(() => repo.CommitInventory(owner, auraCommitted, nextAuraPlan), "旧owner不能提交带光环详情的新合法计划");
            That(Query("PRAGMA integrity_check") is "ok", "selection database integrity");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} booster selection assertions");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count, now);
    }

    static void RunRealTemplateSelection(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        int[] sourceIds = [10007539, 10007726, 10007992, 10007993, 10007996, 490700181,
            490700184, 490700980, 490701127, 490701129, 490702047];
        var catalog = resources.Items;
        var service = new InventoryBoosterService(catalog, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true }, avatars: resources.AvatarPackages);
        var failures = new List<string>();
        int rows = 0, titles = 0, auras = 0;
        foreach (int sourceId in sourceIds)
        {
            var definition = resources.Boosters.Get(sourceId);
            That(definition.Available && definition.Selection is { SelectionCount: 1, Categories.Length: 1 },
                "真实称号和光环盒保持单分类单选 " + sourceId);
            foreach (var category in definition.Selection!.Categories)
                foreach (var choice in category.Rewards)
                {
                    rows++;
                    var template = catalog.Get(choice.ItemId);
                    if (template.Kind == ItemKind.Equipment) titles++;
                    if (template.Kind == ItemKind.Avatar) auras++;
                    var source = new InventoryItem(1000, Bag(70),
                        InventoryRewardGrantService.Create(catalog.Get(sourceId), 2, shopExpiration: true));
                    Character role = new(1, 0, "RealSelection", 0, towns.Room, towns.InitialReturn)
                    {
                        Inventory = new([source])
                    };
                    var opened = service.Open(role, Select(70, [choice.ItemId], category.Category, category.Subcategory),
                        out var receipt, now);
                    if (!opened.Accepted)
                    {
                        failures.Add(sourceId + ":" + choice.ItemId + ":" + opened.Error + ":" + opened.RejectReason);
                        continue;
                    }
                    var reward = opened.Plan!.After.Items.Single(item => item.Core.ItemId == choice.ItemId);
                    That(receipt!.Rewards.Single() == new BoosterGrantedItem(choice.ItemId, choice.Count) &&
                        reward.Core.ItemId == choice.ItemId && reward.Core.Count == choice.Count &&
                        reward.Core.Kind == template.Kind && role.Inventory.Items.Single() == source &&
                        opened.Plan.After.Find(source.Position) is { Core.Count: 1 },
                        "真实选择按模板完整发奖且规划不提前扣源 " + sourceId + ":" + choice.ItemId);
                    var expected = InventoryRewardGrantService.CreateItem(template, reward.Position,
                        InventoryRewardGrantService.Create(template, choice.Count, now, shopExpiration: true));
                    That(reward == expected, "真实候选完整核心、封装、耐久、品质、期限及详情沿统一创建规则 " + choice.ItemId);
                    That(template.Kind switch
                    {
                        ItemKind.Equipment => reward.Position.List == InventoryListType.Main && reward.Avatar is null && reward.Creature is null,
                        ItemKind.Avatar => reward.Position.List == InventoryListType.Avatar && reward.Avatar is not null &&
                            reward.Creature is null && reward.Core.Durability == 0,
                        _ => false
                    }, "真实称号与光环进入模板所属容器并带完整详情 " + sourceId + ":" + choice.ItemId);
                }
        }
        That(rows == 33 && titles == 21 && auras == 12, "正式Catalog中11盒仍声明33个真实称号与光环候选");
        That(failures.Count == 0, "真实选择发奖失败：" + string.Join(", ", failures));
        RunRealTemplateRejections(towns, resources, now);
    }

    static void RunRealTemplateRejections(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        const int sourceId = 10007539;
        var catalog = resources.Items;
        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var isolated = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(isolated)!;
        var service = new InventoryBoosterService(catalog, isolated, resources.GoldLimits, resources.EpicPieces,
            debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true },
            contracts: resources.Contracts, avatars: resources.AvatarPackages);
        var source = new InventoryItem(1000, Bag(70), InventoryRewardGrantService.Create(catalog.Get(sourceId), shopExpiration: true));
        Character role = new(1, 0, "RealReject", 0, towns.Room, towns.InitialReturn) { Inventory = new([source]) };
        InventoryResult Open(int id, string body, int selectionCount = 1)
        {
            definitions[sourceId] = BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n" +
                "[booster selection num]\n" + selectionCount + "\n[booster select category]\n0 0\n" + body + "\n[/booster select category]");
            int[] ids = selectionCount == 0 ? definitions[sourceId].Selection!.Categories[0].Rewards.Select(row => row.ItemId).ToArray() : [id];
            return service.Open(role, Select(70, ids), out _, now);
        }
        int optionAvatar = resources.AvatarPackages.Get(490705387).Categories.SelectMany(category => category.Rows)
            .Select(row => row.ItemId).First(id => resources.AvatarPackages.GetAvatar(id).RequiresAbilityChoice);
        That(catalog.Get(optionAvatar).Kind == ItemKind.Avatar &&
            resources.AvatarPackages.GetAvatar(optionAvatar).RequiresAbilityChoice,
            "额外属性装扮反例使用正式资源声明");
        foreach (int unsafeId in new[] { optionAvatar, 1, resources.EpicPieces.ByPiece.Keys.First(),
            resources.Boosters.DirectActivationItems.First(), int.MaxValue })
        {
            var result = Open(unsafeId, "[creature]\n" + unsafeId + " 1\n[/creature]");
            That(!result.Accepted && role.Inventory.Items.Single() == source,
                "选择段不能把需额外选项、特殊owner或失效模板当默认实体发奖 " + unsafeId);
        }
        That(!Open(400330019, "[equipment]\n400330019 1\n[/equipment]\n[creature]\n400330019 1\n[/creature]").Accepted,
            "同ID不同选择来源不能靠第一行覆盖分组语义");
        const string repeatedTitle = "[creature]\n400330019 1 400330019 1\n[/creature]";
        var repeatedChoice = Open(400330019, repeatedTitle);
        var repeatedAll = Open(400330019, repeatedTitle, 0);
        That(repeatedChoice.Accepted && repeatedChoice.Plan!.After.Items.Length == 1 &&
            repeatedAll.Accepted && repeatedAll.Plan!.After.Items.Length == 2,
            "相同来源等价行在选择模式只发一行，全领模式保留每行独立实例");
        That(!Open(1, "[etc]\n1 1\n[/etc]\n[creature]\n1 1\n[/creature]", 0).Accepted,
            "全领逐来源保留门槛，钱包行不能通过同ID普通行绕过creature来源实体限制");
        That(Open(63220, "[creature]\n63220 1\n[/creature]").Accepted,
            "真实宠物模板仍按原生选择来源生成宠物实体");
        const int auraId = 101590027;
        Open(auraId, "[creature]\n" + auraId + " 1\n[/creature]");
        foreach (string? avatarText in new string?[] { null, "[import script]\n`equipment/base.equ`",
            "[avatar type select]\n0 0 0 6000 0 0 0\n[/avatar type select]",
            "[avatar select ability]\n0 `[HP MAX]` `+` 100\n[/avatar select ability]",
            "[avatar select ability]\n1 `[HP MAX]` `+` 100\n[/avatar select ability]" })
        {
            var avatarCatalog = new AvatarPackageCatalog(_ => avatarText ?? "",
                avatarText is null ? new Dictionary<int, string>() : new() { [auraId] = "aura.equ" }, new Dictionary<int, string>());
            var guarded = new InventoryBoosterService(catalog, isolated, resources.GoldLimits, resources.EpicPieces,
                debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true }, avatars: avatarCatalog);
            var rejected = guarded.Open(role, Select(70, [auraId]), out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && role.Inventory.Items.Single() == source,
                "缺失、挂起、期限类型表及显式能力选择（含0）不能猜默认光环实例");
        }
        var aura = catalog.Get(auraId);
        var range = ItemSlotBoundService.Range(ItemKind.Avatar, role.Inventory);
        var full = role with { Inventory = new([source, .. Enumerable.Range(range.Start, range.End - range.Start + 1)
            .Select(slot => InventoryRewardGrantService.CreateItem(aura, new(InventoryListType.Avatar, (short)slot),
                InventoryRewardGrantService.Create(aura, utcNow: now, shopExpiration: true)))]) };
        That(!service.Open(full, Select(70, [auraId]), out _, now).Accepted && full.Inventory.Find(source.Position) == source,
            "光环页满不能借用Main源槽或丢弃奖励，整次规划保留源");
        definitions[sourceId] = BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n" +
            "[creature]\n1 400330019 1 1\n[/creature]\n[/booster info]");
        That(!service.Open(role, new(70, 1), out _, now).Accepted,
            "随机宠物池继续拒绝真实称号模板，不受选择来源修复影响");
    }

    static void RunDisjointInstanceSelection(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        var catalog = resources.Items;
        var service = new InventoryBoosterService(catalog, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        Character Role(int sourceId) => new(1, 0, "RookieBox", 0, towns.Room, towns.InitialReturn)
        {
            Job = 15, Inventory = new([new(1, Bag(3), InventoryRewardGrantService.Create(catalog.Get(sourceId), utcNow: now))], Gold: 12345)
        };
        var first = Role(490704518);
        That(service.Open(first, Select(3, [401030066]), out _, now).Accepted,
            "模板已禁分解的新秀武器盒能沿真实开箱入口发放，不能因空实例分解声明整盒拒绝");
        for (byte job = 0; job < 16; job++)
            foreach (byte level in new byte[] { 1, 95 })
                That(service.Open(first with { Job = job, Level = level }, Select(3, [401030066]), out _, now).Accepted,
                    "各职业及领取等级都可选择异职业武器，不将穿戴资格套用于领盒 " + job + ":" + level);
        var unique = new HashSet<int>();
        foreach (var (id, categories, lines, groupSize) in new[]
        {
            (490704518, 16, 76, 1), (490704519, 35, 175, 5), (490704520, 7, 21, 3), (490704521, 7, 14, 2)
        })
        {
            var definition = resources.Boosters.Get(id);
            That(definition.Available && definition.Selection!.Categories.Length == categories &&
                definition.Selection.Categories.Sum(group => group.Rewards.Length) == lines,
                "新秀四盒保留全部分类及声明行 " + id);
            foreach (var category in definition.Selection!.Categories)
            {
                var selections = id == 490704518 ? category.Rewards.Select(row => new[] { row.ItemId }) :
                    new[] { category.Rewards.Select(row => row.ItemId).ToArray() };
                foreach (int[] ids in selections)
                {
                    var role = Role(id);
                    var opened = service.Open(role, Select(3, ids, category.Category, category.Subcategory), out var receipt, now);
                    That(opened.Accepted && receipt!.Rewards.Length == groupSize && opened.Plan!.After.Items.Length == groupSize &&
                        opened.Plan.After.Gold == 12345 && opened.Plan.After.Find(Bag(3)) is null && role.Inventory.Items.Length == 1,
                        "各分类完整发奖且只规划一次源消费，不额外限制领取职业/等级 " + id);
                    foreach (var reward in opened.Plan!.After.Items)
                    {
                        unique.Add(reward.Core.ItemId);
                        var template = catalog.Get(reward.Core.ItemId);
                        That(ids.Contains(reward.Core.ItemId) && template is { Kind: ItemKind.Equipment,
                            DisjointForbidden: true, PersonalCargoForbidden: true, Expiration.Days: 30 } &&
                            reward.Core == InventoryRewardGrantService.Create(template, utcNow: now) with
                            { Attr = (byte)(id == 490704518 ? 10 : 0), GenuineUpgrade = (byte)(id == 490704518 ? 5 : 0) },
                            "实际装备完整核心、30天期限和模板限制保留，武器强化10锻造5 " + reward.Core.ItemId);
                    }
                }
            }
        }
        That(unique.Count == 247, "四盒全部286声明行覆盖247个独立装备模板");
        foreach (var request in new[] { Select(3, [401030066, 401030066]), Select(3, [400100084]),
            Select(3, [401030066], 255), Select(3, []) })
            That(!service.Open(first, request, out _, now).Accepted && first.Inventory.Items.Single().Core.ItemId == 490704518,
                "新兼容盒仍拒绝重复、外来、错误分类和空选择，保留源");
        foreach (var source in new[] { first.Inventory.Items.Single().Core with { EquipmentLockId = 1 },
            first.Inventory.Items.Single().Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } })
            That(!service.Open(first with { Inventory = new([new(1, Bag(3), source)]) }, Select(3, [401030066]), out _, now).Accepted,
                "兼容分解声明不绕过源安全锁和过期限制");
        var range = ItemSlotBoundService.Range(ItemKind.Equipment, first.Inventory);
        var full = first with { Inventory = first.Inventory with { Items = first.Inventory.Items.AddRange(
            Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot => new InventoryItem(slot, Bag((short)slot),
                InventoryRewardGrantService.Create(catalog.Get(16400), utcNow: now)))) } };
        That(!service.Open(full, Select(3, [401030066]), out _, now).Accepted && full.Inventory.Items.Length == range.End - range.Start + 2,
            "装备页满时整盒拒绝，不能丢奖励或改投其他容器");

        // 每个负例用私有目录，不能改共享资源缓存；仍调用真实Open而非只检查Parse。
        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        var isolated = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(isolated)!;
        var guarded = new InventoryBoosterService(catalog, isolated, resources.GoldLimits, resources.EpicPieces);
        const string marker = "[set disjoint instance type]\n";
        string Text(string body, string declaration = marker) => declaration + "[stackable type]\n`[booster selection]` 0\n" +
            "[booster selection num]\n0\n[booster select category]\n0 0\n" + body + "\n[/booster select category]\n";
        const string safe = "[equipment]\n401030066 1\n[/equipment]";
        definitions[10000743] = BoosterCatalog.Parse(Text(safe));
        var syntheticRole = Role(10000743);
        That(guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
            "支持范围由声明及实际模板驱动，不按四个新秀盒ID放行");
        definitions[10000743] = BoosterCatalog.Parse(Text(safe,
            "[outer]\n[inner]\n[/outer]\n" + marker + "[/inner]\n"));
        string beforeCrossedDeclaration = JsonSerializer.Serialize(syntheticRole);
        var crossed = guarded.Open(syntheticRole, Select(3, [401030066]), out var crossedReceipt, now);
        That(!crossed.Accepted && crossed.Plan is null && crossedReceipt is null &&
            JsonSerializer.Serialize(syntheticRole) == beforeCrossedDeclaration,
            "交叉闭合内层中的实例分解声明不是根级，必须整包拒绝并保留源与全部资产");
        foreach (string nested in new[]
        {
            "[outer]\n[outer]\n[/outer]\n" + marker + "[/outer]\n",
            "[outer]\n[outer]\n" + marker + "[/outer]\n[/outer]\n",
            "[outer]\n[inner]\n" + marker + "[/inner]\n[/outer]\n",
            "[outer]\n" + marker + "[/outer]\n"
        })
        {
            definitions[10000743] = BoosterCatalog.Parse(Text(safe, nested));
            var nestedResult = guarded.Open(syntheticRole, Select(3, [401030066]), out var nestedReceipt, now);
            That(!nestedResult.Accepted && nestedResult.Plan is null && nestedReceipt is null &&
                JsonSerializer.Serialize(syntheticRole) == beforeCrossedDeclaration,
                "同名或异名嵌套外层仍开放时，实例分解声明不是根级，拒绝且资产不变");
        }
        definitions[10000743] = BoosterCatalog.Parse(Text(safe,
            "[outer]\n[/outer]\n" + marker + "[outer]\n[/outer]\n"));
        That(guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
            "两个独立且完整闭合的同名区间之间仍是根级，不因保守配对扩大拒绝范围");
        foreach (string declaration in new[] { marker + marker, marker + "1\n", marker + "`value`\n", marker + "[/set disjoint instance type]\n",
            marker + marker + "[/set disjoint instance type]\n", "[/set disjoint instance type]\n",
            "[set disjoint instance type\n", "[set disjoint instance type extra]\n", "[ set disjoint instance type]\n" })
        {
            definitions[10000743] = BoosterCatalog.Parse(Text(safe, declaration));
            That(!guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
                "重复、有值、嵌套或闭合冲突的实例分解声明保持拒绝");
        }
        definitions[10000743] = BoosterCatalog.Parse(Text(safe + "\n" + marker, ""));
        That(!guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted,
            "分类内嵌实例分解声明不能按根级空声明放行");
        That(!catalog.Get(16400).DisjointForbidden, "负例普通装备确实允许分解");
        foreach (int unsafeId in new[] { 16400, 1106, 1, resources.EpicPieces.ByPiece.Keys.First(), resources.Boosters.DirectActivationItems.First() })
        {
            definitions[10000743] = BoosterCatalog.Parse(Text(safe + "\n[etc]\n" + unsafeId + " 1\n[/etc]"));
            var rejected = guarded.Open(syntheticRole, Select(3, [401030066, unsafeId]), out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && syntheticRole.Inventory.Gold == 12345 &&
                syntheticRole.Inventory.Items.Single().Core.ItemId == 10000743,
                "禁分解实例声明的实际所选奖励必须全为已禁分解装备，快捷钱包/碎片/契约也不能绕过 " + unsafeId);
        }
        definitions[10000743] = BoosterCatalog.Parse(Text(safe + "\n[etc]\n1106 1\n[/etc]")
            .Replace("[booster selection num]\n0", "[booster selection num]\n1", StringComparison.Ordinal));
        That(guarded.Open(syntheticRole, Select(3, [401030066]), out _, now).Accepted &&
            !guarded.Open(syntheticRole, Select(3, [1106]), out _, now).Accepted,
            "限定兼容按实际选择预检，不因同分类内未选中的不安全候选额外拒绝");
        definitions[10000743] = BoosterCatalog.Parse(marker + "[stackable type]\n`[booster]` 0\n" +
            "[booster info]\n[equipment]\n1 401030066 1 1\n[/equipment]\n[/booster info]");
        That(!guarded.Open(syntheticRole, new(3, 1), out _, now).Accepted,
            "随机奖池不进入选择盒的限定兼容范围");
    }

    static BoosterDefinition Parse(string category, int count = 1) => BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n" +
        "[booster selection num]\n" + count + "\n[booster select category]\n" + category + "\n[/booster select category]");
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static BoosterOpenRequest Select(short slot, int[] ids, byte category = 0, byte subcategory = 0, int count = 1) =>
        new(slot, count, new(category, subcategory, ids));
}
