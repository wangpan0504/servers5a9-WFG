using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class CargoTests
{
    public static void RunCore()
    {
        var template = new DfoServer.GameWorld.Items.ItemTemplate(50, DfoServer.GameWorld.Items.ItemKind.Consumable,
            0, 10, 0, 100, 0, 0, 0, "[free]", 1, 0, "仓库资格测试") { MaximumLevel = 20, PersonalCargoUpgradeTarget = 24 };
        var catalog = new DfoServer.GameWorld.Items.ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = template });
        var service = new InventoryCargoService(catalog, null!, null!);
        var source = new InventoryItem(1, new(InventoryListType.Main, 65), new ItemCore(50, template.Kind, 2));
        var role = new Character(1, 0, "CargoGuard", 0, new(0, 0, 0, 0), null) { Level = 10, Inventory = new([source]) };
        var captured = InventoryRequests.Move(Convert.FromHexString("007200ED913F1D010000000201000100000000000000FFFFFFFF0000"));
        That(captured.Source == new InventorySlot(InventoryListType.Main, 114) && captured.SourceItemId == 490705389 &&
            captured.Count == 1 && captured.Destination == new InventorySlot(InventoryListType.PersonalCargo, 1) && captured.DestinationItemId == 1,
            "现场存入请求按现行解析器还原字段");
        var move = new InventoryMoveRequest(source.Position, 50, 1, new(InventoryListType.PersonalCargo, 1), 1, 0, -1, 0, 0);
        That(InventoryMoveService.Plan(role, move, catalog, 0).Accepted, "空个人金库格接纳当前客户端格号别名");
        That(InventoryMoveService.Plan(role, move with { DestinationItemId = 0 }, catalog, 0).Accepted, "保留标准空格零值");
        That(!InventoryMoveService.Plan(role, move with { SourceItemId = 51 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(role, move with { DestinationItemId = 2 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(role, move with { Destination = new(InventoryListType.PersonalCargo, 8), DestinationItemId = 8 }, catalog, 0).Accepted,
            "目标别名不放宽源模板、错误格号或容量");
        var occupied = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Add(source with { Uid = 2, Position = move.Destination }) } };
        That(!InventoryMoveService.Plan(occupied, move, catalog, 0).Accepted, "非空目标仍严格核对物品编号");
        var sortRequest = InventoryRequests.Sort(Convert.FromHexString("06000000100218142000"));
        That(sortRequest.List == InventoryListType.PersonalCargo && (byte)sortRequest.Kind == 20 && sortRequest.Condition == 0,
            "本版实收整理请求为个人仓库category20，非物品模板类别");
        var gear = new InventoryItem(2, new(InventoryListType.PersonalCargo, 7),
            new ItemCore(51, GameWorld.Items.ItemKind.Equipment, 1) { Rune = 3, EquipmentLockId = 7 });
        var pinned = source with { Uid = 3, Position = new(InventoryListType.PersonalCargo, 4), Core = source.Core with { SortLockFlag = 1 } };
        var expired = source with { Uid = 4, Position = new(InventoryListType.PersonalCargo, 6), Core = source.Core with { ExpireTime = 1, SortLockFlag = 1 } };
        var beforeSort = role.Inventory with { Items = [source, gear, pinned, expired] };
        var sorted = InventorySortService.Plan(beforeSort, sortRequest.Kind, 0, catalog, 2, sortRequest.List);
        That(sorted.Accepted && sorted.Plan!.After.Find(new(InventoryListType.PersonalCargo, 0)) == gear with { Position = new(InventoryListType.PersonalCargo, 0) } &&
            sorted.Plan.After.Find(pinned.Position) == pinned && sorted.Plan.After.Find(source.Position) == source && sorted.Plan.After.Items.Length == 3,
            "个人仓库整理保持安全锁装备UID及完整核心、排序锁位置和主背包，先清到期物品");
        var repeated = InventorySortService.Plan(sorted.Plan!.After, sortRequest.Kind, 0, catalog, 2, sortRequest.List);
        That(repeated.Accepted && repeated.Plan!.After.Items.SequenceEqual(sorted.Plan.After.Items), "重复整理不复制或移动已排好物品");
        var legacySort = InventorySortService.Plan(beforeSort, (GameWorld.Items.ItemKind)11, 0, catalog, 2, sortRequest.List);
        That(legacySort.Accepted && legacySort.Plan!.After.Items.SequenceEqual(sorted.Plan.After.Items), "保留既有个人仓库11入口相同行为");
        That(!InventorySortService.Plan(beforeSort, (GameWorld.Items.ItemKind)19, 0, catalog, 2, sortRequest.List).Accepted &&
            !InventorySortService.Plan(beforeSort, sortRequest.Kind, 0, catalog, 2, InventoryListType.Main).Accepted,
            "个人仓库别名不放开未知类别或主背包范围");
        That(InventorySortService.Plan(InventorySnapshot.Empty, sortRequest.Kind, 0, catalog, 2, sortRequest.List) is
            { Accepted: true, Plan.After.Items.Length: 0 }, "空个人仓库整理成功且不生成物品");
        foreach (var (level, job, allowed) in new (byte, byte, bool)[] { (9, 0, false), (10, 0, true), (20, 0, true), (21, 0, false), (10, 1, false) })
        {
            var result = service.PersonalUpgrade(role with { Level = level, Job = job }, 65);
            That(result.Accepted == allowed && role.Inventory.Find(source.Position) == source &&
                (!allowed || result.Plan!.After.CargoCapacity == 24 && result.Plan.After.Find(source.Position)!.Core.Count == 1 &&
                    result.Plan.CharacterPrecondition!.Matches(role with { Level = level, Job = job }) &&
                    !result.Plan.CharacterPrecondition.Matches(role with { Level = 21 })),
                "仓库升级校验源工具资格，拒绝不消费，合法边界仍扩容");
        }
        catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = template with { UseForbidden = true } });
        That(!service.PersonalUpgrade(role, 65).Accepted, "仓库工具禁止使用标记不可绕过");
    }

    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception e) when (e is SqliteException or InvalidDataException or InvalidOperationException) { rejected = true; }
            That(rejected, name);
        }
        string root = Path.Combine(Path.GetTempPath(), "s5a9-cargo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("cargo", () => { });
            var items = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            var cargo = new InventoryCargoService(itemResources.Items, itemResources.Cargo, itemResources.GoldLimits);
            var currency = new CurrencyService(itemResources.GoldLimits);
            var current = repo.Create(owner, new("CargoOne", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            That(current.Inventory.CargoCapacity == 8 && current.Inventory.AccountCargo == new AccountCargoState(), "new account closed, personal eight slots");
            That(itemResources.Cargo.AccountTiers.Count == 31 && itemResources.Cargo.AccountTiers[^1].Capacity == 240,
                "matched resource covers all account tiers through 240");
            That(!cargo.AccountUpgrade(current, true).Accepted && !cargo.AccountUpgrade(current, false).Accepted, "insufficient creation fee and unopened upgrade rejected");
            current = Commit(currency.Grant(current, CurrencyKind.Gold, 20_000_000));
            That(!cargo.AccountUpgrade(current with { Level = 59 }, true).Accepted, "creation level comes from PVF");
            current = Commit(cargo.AccountUpgrade(current, true));
            That(current.Inventory.AccountCargo.Capacity == 1 && current.Inventory.Gold == 19_900_000, "create charges once and opens one slot");
            That(!cargo.AccountUpgrade(current, true).Accepted, "duplicate creation cannot charge again");
            current = Commit(cargo.AccountUpgrade(current, false));
            That(current.Inventory.AccountCargo.Capacity == 8 && current.Inventory.Gold == 17_900_000, "first expansion charges two million");
            That(!cargo.Gold(current, 0, true).Accepted && !cargo.Gold(current, -1, false).Accepted, "nonpositive transfer rejected");
            current = Commit(cargo.Gold(current, 1234, true));
            That(current.Inventory.AccountCargo.Gold == 1234 && current.Inventory.Gold == 17_898_766, "deposit conserves gold");
            current = Commit(cargo.Gold(current, 234, false));
            That(current.Inventory.AccountCargo.Gold == 1000 && current.Inventory.Gold == 17_899_000, "withdraw conserves gold");
            That(!cargo.Gold(current, 1001, false).Accepted && !cargo.Gold(current, int.MaxValue, true).Accepted, "insufficient transfer changes neither owner");
            var limitFixture = current with { Inventory = current.Inventory with { Gold = 1, AccountCargo = current.Inventory.AccountCargo with { Gold = 100_000_000 } } };
            That(!cargo.Gold(limitFixture, 1, true).Accepted, "account gold cap uses current tier");
            limitFixture = current with { Inventory = current.Inventory with { Gold = 400_000_000 } };
            That(!cargo.Gold(limitFixture, 1, false).Accepted, "withdraw cannot overflow character carry limit");
            var weapon = current.Inventory.Find(new(InventoryListType.Equipment, 12))!;
            var detailed = weapon with { Core = weapon.Core with { Attr = 9, EnchantCardId = 123, AmplifyValue = 78,
                Rune = 42, Random0 = new(1, 2, 3), GuardianGemKeys = 1234567, Extension114 = new(2, 345), SortLockFlag = 1 } };
            current = Commit(new(true, 0, new(current.Inventory, current.Inventory with { Items = current.Inventory.Items.Replace(weapon, detailed) }, "cargo_fixture")));
            current = Commit(Move(detailed.Position, new(InventoryListType.PersonalCargo, 7)));
            That(current.Inventory.Find(new(InventoryListType.PersonalCargo, 7)) == detailed with { Position = new(InventoryListType.PersonalCargo, 7) },
                "personal deposit preserves UID and all equipment attributes including worn sorting lock");
            current = Commit(items.Lock(current, new(InventoryListType.PersonalCargo, 7), InventoryLockOperation.EquipmentLock));
            That(!Move(new(InventoryListType.PersonalCargo, 7), new(InventoryListType.AccountCargo, 0)).Accepted, "security lock blocks cross-character storage");
            current = Commit(Move(new(InventoryListType.PersonalCargo, 7), new(InventoryListType.Main, 3)));
            That(current.Inventory.Find(new(InventoryListType.Main, 3)) is { Uid: var uid, Core.EquipmentLockId: not 0 } && uid == detailed.Uid,
                "locked equipment may leave personal cargo through quick bar");
            current = Commit(items.Lock(current, new(InventoryListType.Main, 3), InventoryLockOperation.EquipmentUnlock));
            current = Commit(Move(new(InventoryListType.Main, 3), new(InventoryListType.AccountCargo, 7)));
            var sharedItem = current.Inventory.Find(new(InventoryListType.AccountCargo, 7))!;
            That(sharedItem.Core == detailed.Core with { SortLockFlag = 0 } && Query("SELECT character_id IS NULL FROM character_inventory_items WHERE item_uid=" + detailed.Uid) is 1L,
                "account deposit changes actual database owner without cloning core or UID");
            That(!items.Lock(current, sharedItem.Position, InventoryLockOperation.EquipmentLock).Accepted, "account cargo supports sorting lock, not per-character security locks");
            current = Commit(items.Lock(current, sharedItem.Position, InventoryLockOperation.SortLock));
            current = Commit(items.Sort(current, new(InventoryListType.AccountCargo, 0, 0)));
            That(current.Inventory.Find(sharedItem.Position) is not null, "account sorting lock fixes position");
            current = Commit(items.Lock(current, sharedItem.Position, InventoryLockOperation.SortUnlock));
            current = Commit(items.Sort(current, new(InventoryListType.AccountCargo, 0, 0)));
            That(current.Inventory.Find(new(InventoryListType.AccountCargo, 0))?.Uid == sharedItem.Uid, "account sort moves complete instance");
            var oldRole = current;
            var sibling = repo.Create(owner, new("CargoTwin", towns.Room, towns.InitialReturn));
            That(sibling.Inventory.CargoCapacity == 8 && sibling.Inventory.AccountCargo == current.Inventory.AccountCargo &&
                sibling.Inventory.Find(new(InventoryListType.AccountCargo, 0))?.Uid == sharedItem.Uid, "new role loads shared storage without copying it");
            Reject(() => repo.Create(owner, new("CargoBad", towns.Room, towns.InitialReturn) { Inventory = current.Inventory }), "creation rejects caller-owned account cargo seed");
            using var other = owners.Acquire("other-cargo", () => { });
            var isolated = repo.Create(other, new("CargoOther", towns.Room, towns.InitialReturn));
            That(isolated.Inventory.AccountCargo.Capacity == 0 && isolated.Inventory.Items.IsEmpty, "another account sees no shared assets");
            current = sibling;
            current = Commit(Move(new(InventoryListType.AccountCargo, 0), new(InventoryListType.Main, 3)));
            That(current.Inventory.Find(new(InventoryListType.Main, 3))?.Uid == sharedItem.Uid &&
                Query("SELECT character_id FROM character_inventory_items WHERE item_uid=" + sharedItem.Uid) is long owned && owned == sibling.Id,
                "another role withdraws original instance and assumes ownership");
            Reject(() => InventoryService.Commit(repo, owner, oldRole, currency.Grant(oldRole, CurrencyKind.Gold, 1)), "stale account storage cannot be overwritten by another role");
            current = repo.List(owner)[0];
            current = Commit(items.Grant(current, [new(3299, 600), new(50, 2)]));
            var material = current.Inventory.Items.Single(item => item.Core.ItemId == 3299);
            current = Commit(Move(material.Position, new(InventoryListType.AccountCargo, 1), 17));
            That(current.Inventory.Find(new(InventoryListType.AccountCargo, 1)) is { Core.Count: 17 } split && split.Uid != material.Uid,
                "cross-owner partial stack receives exactly one new UID");
            current = Commit(Move(new(InventoryListType.AccountCargo, 1), material.Position, 17));
            That(current.Inventory.Find(material.Position) is { Core.Count: 600 } merged && merged.Uid == material.Uid, "merge preserves target identity and total quantity");
            current = Commit(Move(material.Position, new(InventoryListType.Main, 122), 400));
            short toolSlot = current.Inventory.Items.Single(item => item.Core.ItemId == 50).Position.Index;
            current = Commit(cargo.PersonalUpgrade(current, toolSlot));
            That(current.Inventory.CargoCapacity == 24 && current.Inventory.Find(new(InventoryListType.Main, toolSlot))?.Core.Count == 1,
                "personal tool grants defined capacity and consumes one");
            That(!cargo.PersonalUpgrade(current, toolSlot).Accepted, "duplicate target does not consume second tool");
            current = Commit(currency.Grant(current, CurrencyKind.Cera, 1_000_000));
            string beforeCashFailure = Snapshot();
            Sql("CREATE TRIGGER reject_cargo_cash BEFORE INSERT ON cargo_audit_log BEGIN SELECT RAISE(ABORT,'fixture'); END");
            Reject(() => Commit(cargo.AccountUpgrade(current, false)), "capacity failure rolls back account Cera and wallet version");
            Sql("DROP TRIGGER reject_cargo_cash");
            That(Snapshot() == beforeCashFailure, "failed paid upgrade retains balance, capacity and audits");
            foreach (var tier in itemResources.Cargo.AccountTiers.Where(tier => tier.Capacity > 8))
            {
                var before = current.Inventory;
                current = Commit(cargo.AccountUpgrade(current, false));
                That(current.Inventory.AccountCargo.Capacity == tier.Capacity && current.Inventory.Gold == before.Gold - tier.GoldCost &&
                    current.Inventory.AccountWallet.Get(CurrencyKind.Cera) == before.AccountWallet.Get(CurrencyKind.Cera) - tier.CeraCost,
                    "resource-defined upgrade and cost " + tier.Capacity);
            }
            That(!cargo.AccountUpgrade(current, false).Accepted && current.Inventory.Items.Single(item => item.Core.ItemId == 3299).Core.Count == 100,
                "maximum capacity rejects and both material tiers consumed exactly 250");
            string beforeFailure = Snapshot();
            Sql("CREATE TRIGGER reject_cargo BEFORE INSERT ON cargo_audit_log BEGIN SELECT RAISE(ABORT,'fixture'); END");
            var failed = cargo.Gold(current, 1, true);
            Reject(() => Commit(failed), "cargo failure rolls back both currencies and character/account revisions");
            material = current.Inventory.Items.Single(item => item.Core.ItemId == 3299);
            Reject(() => Commit(Move(material.Position, new(InventoryListType.AccountCargo, 0))), "cargo failure after moving core rolls back owner and bindings");
            Sql("DROP TRIGGER reject_cargo");
            That(Snapshot() == beforeFailure, "failed operations leave durable state and all audits identical");
            current = Commit(Move(material.Position, new(InventoryListType.PersonalCargo, 23)));
            That(!Move(new(InventoryListType.PersonalCargo, 23), new(InventoryListType.PersonalCargo, 24)).Accepted, "closed personal slot rejected");
            current = Commit(items.Sort(current, new(InventoryListType.PersonalCargo, (GameWorld.Items.ItemKind)20, 0)));
            That(current.Inventory.Find(new(InventoryListType.PersonalCargo, 0))?.Uid == material.Uid, "个人仓库按本版category20整理全部类别");
            var template = itemResources.Items.Get(sharedItem.Core.ItemId);
            That(!InventoryCargoService.AllowsInsert(sharedItem.Core, InventoryListType.PersonalCargo, template with { PersonalCargoForbidden = true }) &&
                !InventoryCargoService.AllowsInsert(sharedItem.Core, InventoryListType.AccountCargo, template with { AttachType = "[trade]" }), "PVF forbidden and character binding enforced");
            That(InventoryCargoService.AllowsInsert(sharedItem.Core with { SealFlag = 0 }, InventoryListType.AccountCargo, template with { AttachType = "[sealing]" }) &&
                !InventoryCargoService.AllowsInsert(sharedItem.Core with { TradeRestriction = 1 }, InventoryListType.AccountCargo, template),
                "unsealing alone does not invent a trade restriction");
            That(Convert.ToHexString(CargoPackets.Full(InventorySnapshot.Empty, InventoryListType.PersonalCargo)) == "020800000000" &&
                Convert.ToHexString(CargoPackets.Full(InventorySnapshot.Empty, InventoryListType.AccountCargo)) == "0C0000000000000000", "empty warehouse header widths");
            foreach (var inventory in new[] { InventorySnapshot.Empty, current.Inventory })
            {
                using var cargoBody = new MemoryStream(CargoPackets.Full(inventory, InventoryListType.PersonalCargo));
                using var cargoReader = new BinaryReader(cargoBody);
                That(cargoReader.ReadByte() == 2 && cargoReader.ReadUInt16() == inventory.CargoCapacity, "personal cargo header");
                int count = cargoReader.ReadUInt16();
                That(count == inventory.Items.Count(item => item.Position.List == InventoryListType.PersonalCargo), "personal cargo physical count");
                for (int index = 0; index < count; index++)
                    That(cargoReader.ReadBytes(123).Length == 123, "personal cargo complete physical entry");
                // 物品循环结束后仍须进入0267B480，空列表也不能省略该读取。
                That(cargoReader.ReadByte() == 0 && cargoBody.Position == cargoBody.Length, "personal cargo auxiliary count consumes final byte");
            }
            That(Convert.ToHexString(CargoPackets.Ack(InventoryResult.Reject(20))) == "001400" && CargoRequests.PersonalUpgrade([65, 0]) == 65,
                "Client u16 failure and upgrade tool slot");
            Reject(() => CargoRequests.Gold([1, 0, 0]), "truncated money request");
            Reject(() => CargoRequests.AccountUpgrade([0]), "nonempty account upgrade request");
            var restored = new CharacterRepository(path).List(owner).Single(role => role.Id == current.Id);
            That(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(current), "restart restores both owners and cargo state");
            That(Query("PRAGMA integrity_check") is "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L, "cargo foreign keys and integrity");
            Reject(() => Sql("UPDATE character_inventory_slots SET character_id=NULL,list_type=12 WHERE item_uid=" + material.Uid), "database rejects slot/item ownership mismatch");
            var expiring = current.Inventory.Find(new(InventoryListType.PersonalCargo, 0))!;
            current = Commit(new(true, 0, new(current.Inventory, current.Inventory with
            {
                Items = current.Inventory.Items.Replace(expiring, expiring with { Core = expiring.Core with { ExpireTime = 1, SortLockFlag = 1 } })
                    .Add(new(0, new(InventoryListType.AccountCargo, 239), expiring.Core with { ExpireTime = 1, SortLockFlag = 1 }))
            }, "expiry_fixture")));
            long cargoRevision = current.Inventory.AccountCargo.Revision;
            var selection = new GameSession(repo, towns, owner);
            selection.Select(0);
            That(selection.Selected!.Inventory.Find(new(InventoryListType.PersonalCargo, 0)) is null &&
                selection.Selected.Inventory.Find(new(InventoryListType.AccountCargo, 239)) is null &&
                selection.Selected.Inventory.AccountCargo.Revision == cargoRevision + 1, "selection clears expired personal and shared cargo despite sorting locks");
            current = selection.Selected;
            That(repo.List(owner)[1].Inventory.Items.All(item => item.Position.List != InventoryListType.AccountCargo), "expired shared assets cannot reappear on sibling");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => Commit(cargo.Gold(current, 1, false)), "stale generation cannot withdraw");

            Character Commit(InventoryResult result)
            {
                That(result.Accepted, "planned operation accepted");
                return InventoryService.Commit(repo, owner, current, result);
            }
            InventoryResult Move(InventorySlot from, InventorySlot to, int count = 0) => items.Move(current, new(from,
                current.Inventory.Find(from)?.Core.ItemId ?? 0, count, to, current.Inventory.Find(to)?.Core.ItemId ?? 0, 0, -1, 0, 0));
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") +
                ":" + Query("SELECT count(*) FROM currency_audit_log") + ":" + Query("SELECT count(*) FROM cargo_audit_log");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} cargo assertions");
    }
}
