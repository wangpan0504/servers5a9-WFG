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
    public static void RunCore()
    {
        int checks = Count;
        const string empty = "[default select]\n0\n[booster equipment upgrade]\n10\n[stackable]\n[/stackable]";
        const string reward = "[etc]\n1106 1\n[/etc]";
        string Text(string first, string second, string dimensions = "[booster category num]\n2 2 2\n", int count = 1,
            string type = "[stackable type]\n`[booster selection]` 0\n", string extra = "") =>
            type + dimensions + "[booster selection num]\n" + count + "\n" +
            "[booster select category]\n0 0\n" + first + "\n[/booster select category]\n" +
            "[booster select category]\n0 0\n" + second + "\n[/booster select category]\n" +
            "[booster select category]\n1 0\n" + reward + "\n[/booster select category]\n" + extra;
        var definition = BoosterCatalog.Parse(Text(empty, empty.Replace("\n", "\r\n  ")));
        That(definition.Available && definition.Selection is { Categories.Length: 3 } selection &&
            selection.Categories[0].Rewards.IsEmpty && selection.Categories[1].Rewards.IsEmpty &&
            selection.Categories[2].Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 1) }),
            "等价空重复保留两个原块及其它奖励分类，块间空白不改变token");
        That(BoosterCatalog.Parse(Text("", "", "[booster category num]\n1 2\n")).Available,
            "单维分类可保留等价空重复");
        var repeatedRewards = BoosterCatalog.Parse(Text(reward, reward));
        That(repeatedRewards.Available && repeatedRewards.Selection!.Categories.Length == 3 &&
            repeatedRewards.Selection.Categories.All(category => category.Rewards.Length == 1),
            "等价非空重复保留原块，每个分类只保留一份奖励声明");
        foreach (string text in new[]
        {
            Text(reward, reward.Replace("1106", "1112")), Text(reward, reward.Replace("1106 1", "1106 01")), Text(empty, ""), Text(empty, empty.Replace("10", "11")),
            Text(empty, empty.Replace("0\n[booster equipment", "1 1106\n[booster equipment")),
            Text("[unknown]\n1", "[unknown]\n1"), Text("[stackable]", "[stackable]"),
            Text(empty, empty, extra: "[/booster select category]"),
            "[outer]\n" + Text(empty, empty) + "\n[/outer]",
            Text(empty, empty).Replace("[booster selection num]\n1\n", "[booster selection num]\n1\n[wrapper]\n") + "\n[/wrapper]",
            Text(empty, empty, dimensions: "[booster category num]\n2 256 256\n").Replace("1 0\n[etc]", "128 0\n[etc]"),
            Text(empty, empty, dimensions: "[booster category num]\n2 256 256\n").Replace("0 0\n", "0 128\n"),
            Text(empty, empty, dimensions: "[outer]\n[booster category num]\n2 2 2\n[/outer]\n"),
            Text(empty, empty).Replace("[booster selection num]\n1\n", "[outer]\n[booster selection num]\n1\n[/outer]\n"),
            Text(empty, empty, count: 0), Text(empty, empty, dimensions: ""),
            Text(empty, empty, dimensions: "[booster category num]\n0\n"),
            Text(empty, empty, dimensions: "[booster category num]\n2 1 1\n"),
            Text(empty, empty, dimensions: "[booster category num]\n2 2 0\n"),
            Text(empty, empty, dimensions: "[booster category num]\n3 2 2 2\n"),
            Text(empty, empty, dimensions: "[booster category num]\n2 2 x\n"),
            Text(empty, empty, dimensions: "[booster category num]\n2 2 2\n[booster category num]\n2 2 2\n"),
            Text(empty, empty, type: "[stackable type]\n`[booster selection]` 0\n[stackable type]\n`[booster selection]` 0\n"),
            Text(empty, empty, extra: "[booster select category]\n1 1\n[unknown]\n[/booster select category]"),
            Text(empty, empty).Replace("1 0\n[etc]", "2 0\n[etc]")
        })
            That(!BoosterCatalog.Parse(text).Available, "冲突、未知、残缺或根资格不完整的重复分类仍整盒拒绝");
        Console.WriteLine($"PASS {Count - checks} equivalent duplicate selection parser assertions");
    }

    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        RunEquivalentDuplicateResources(towns, itemResources);
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
        RunDisjointInstanceSelection(towns, itemResources, now);
        var creatureCategory = Parse("0 0\n[creature]\n400990444 1 400990445 1\n[/creature]").Selection!.Categories.Single();
        That(creatureCategory.PendingRule is null && creatureCategory.Rewards.All(row => row.RequiredKind == ItemKind.Creature),
            "宠物自选段保留完整候选并约束实际产物类型");
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
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(role => service.Open(role, Select(4, [10000871]), out _)), "old generation cannot open or replay a selection");
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

    static void RunEquivalentDuplicateResources(TownCatalog towns, ItemResources resources)
    {
        var failures = new List<string>();
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, categories, empty, rows) in new[]
        {
            (2749832, 36, 8, 93), (10007852, 56, 15, 658),
            (10008885, 56, 14, 675), (490700910, 61, 17, 718), (2749978, 9, 0, 70)
        })
        {
            var definition = resources.Boosters.Get(id);
            if (!definition.Available || definition.Selection is not { } selection ||
                selection.Categories.Length != categories || selection.Categories.Any(group => group.PendingRule is not null) ||
                selection.Categories.Count(group => group.Rewards.IsEmpty) != empty ||
                selection.Categories.Sum(group => group.Rewards.Length) != rows)
                failures.Add(id + ":" + (definition.PendingRule ?? "分类或奖励声明被删改"));
            if (definition.Selection is not { } parsed)
                continue;
            var source = new InventoryItem(1, Bag(65), InventoryRewardGrantService.Create(resources.Items.Get(id), utcNow: now));
            Character role = new(1, 0, "EmptyCategory", 0, towns.Room, towns.InitialReturn)
            {
                Job = 0, Level = 95, Inventory = new([source], Gold: 12345)
            };
            That(!source.Core.IsExpired(now.ToUnixTimeSeconds()) &&
                InventoryAvatarPackageService.SourceEligible(role, source, resources.Items.Get(id)),
                "空分类拒绝用例使用真实未过期且满足资源资格的源 " + id);
            int accepted = 0, attempted = 0;
            foreach (var group in parsed.Categories)
                for (int row = 0; row < group.Rewards.Length; row++)
                {
                    var choice = group.Rewards[row]; attempted++;
                    var template = resources.Items.Get(choice.ItemId);
                    var opened = service.Open(role, Select(65, [choice.ItemId], group.Category, group.Subcategory), out var receipt, now);
                    That(opened.Accepted && role.Inventory.Items.Single() == source && role.Inventory.Gold == 12345,
                        "每条真实单选成功规划且不改输入源和钱包 " + id + ":" + choice.ItemId);
                    That(template.Kind is not (ItemKind.Avatar or ItemKind.Creature) && choice.UpgradeChances.IsEmpty,
                        "此真实四源矩阵成功项是确定的普通实体 " + choice.ItemId);
                    var expected = ExpectedItems(role, [choice]);
                    That(receipt!.ItemId == id && receipt.Slot == 65 && receipt.Rewards is [{ } reward] &&
                        reward.ItemId == choice.ItemId && reward.Count == choice.Count &&
                        JsonSerializer.Serialize(opened.Plan!.Before) == JsonSerializer.Serialize(role.Inventory) &&
                        JsonSerializer.Serialize(opened.Plan.After) == JsonSerializer.Serialize(role.Inventory with { Items = expected }),
                        "真实单选完整核心、数量、目标槽、钱包与源一次扣量符合预期 " + id + ":" + choice.ItemId);
                    accepted++;
                }
            That(attempted == rows && accepted > 0, "每源全部原奖励行实际Open且至少一项合法非空成功 " + id);
            if (id == 2749978)
            {
                That(parsed.SelectionCount == 4 && parsed.Categories.Select(group => (group.Category, group.Subcategory)).Distinct().Count() == 7,
                    "真实非空重复源保留selection4、九原块和七个唯一坐标");
                foreach (var group in parsed.Categories.GroupBy(group => (group.Category, group.Subcategory)).Where(groups => groups.Count() > 1).Select(groups => groups.First()))
                {
                    var selected = group.Rewards.Take(4).ToArray();
                    var opened = service.Open(role, Select(65, selected.Select(choice => choice.ItemId).ToArray(), group.Category, group.Subcategory), out var receipt, now);
                    That(opened.Accepted && receipt!.Rewards.Select(reward => (reward.ItemId, reward.Count)).SequenceEqual(selected.Select(choice => (choice.ItemId, choice.Count))) &&
                        JsonSerializer.Serialize(opened.Plan!.After) == JsonSerializer.Serialize(role.Inventory with { Items = ExpectedItems(role, selected) }),
                        "同键非空重复四选按每项原数量发一份，不拼接或翻倍 " + group.Category);
                    foreach (int[] choices in new[] { group.Rewards.Take(5).Select(choice => choice.ItemId).ToArray(), new[] { selected[0].ItemId, selected[0].ItemId } })
                    {
                        var rejected = service.Open(role, Select(65, choices, group.Category, group.Subcategory), out var rejectedReceipt, now);
                        That(!rejected.Accepted && rejected.Plan is null && rejectedReceipt is null && role.Inventory.Items.Single() == source,
                            "非空重复分类仍拒绝五选超限及重复ID，保留源 " + group.Category);
                    }
                }
            }
            Console.WriteLine("Equivalent duplicate selection coverage: " + JsonSerializer.Serialize(new { Source = id, Attempted = attempted, Accepted = accepted, Rejected = attempted - accepted }));
            foreach (var group in parsed.Categories.Where(group => group.Rewards.IsEmpty)
                .DistinctBy(group => (group.Category, group.Subcategory)))
                foreach (int[] choice in new[] { Array.Empty<int>(), new[] { 1106 } })
                {
                    var result = service.Open(role, Select(65, choice, group.Category, group.Subcategory), out var receipt, now);
                    That(!result.Accepted && result.Plan is null && receipt is null && role.Inventory.Items.Single() == source &&
                        role.Inventory.Gold == 12345, "空分类不因兼容重复而发奖、扣源或改钱包 " + id);
                }
        }
        That(failures.Count == 0, "五个真实等价重复分类源保留全部原块、空分类和奖励行：" + string.Join("; ", failures));
        That(!resources.Boosters.Get(490001689).Available, "冲突维数真实源保持根拒绝");

        ImmutableArray<InventoryItem> ExpectedItems(Character role, IEnumerable<BoosterChoice> choices)
        {
            var result = new List<InventoryItem>();
            foreach (var choice in choices)
            {
                var template = resources.Items.Get(choice.ItemId);
                // 预期不调用生产Create/Grant：独立派生普通实体完整核心，未声明字段保持零。
                var core = new ItemCore(template.Id, template.Kind,
                    template.Kind is ItemKind.Equipment or ItemKind.CreatureEquipment ? 999_999_998 : choice.Count)
                {
                    Attr = choice.Upgrade, GenuineUpgrade = choice.Refinement, Durability = template.Durability,
                    SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0,
                    ExpireTime = template.Expiration.Resolve(now)
                };
                var range = ItemSlotBoundService.Range(template.Kind, role.Inventory);
                var list = ItemSlotBoundService.List(template.Kind);
                short slot = range.Start;
                for (int index = 0; index < (core.IsStackable ? 1 : choice.Count); index++)
                {
                    while (result.Any(item => item.Position == new InventorySlot(list, slot))) slot++;
                    That(slot <= range.End, "独立预期未越过真实奖励页边界");
                    result.Add(new(0, new(list, slot), core));
                }
            }
            return result.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray();
        }
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
