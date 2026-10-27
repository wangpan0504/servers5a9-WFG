using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Text.Json;
using System.Collections.Immutable;
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
        var accountRole = role with { Inventory = role.Inventory with { AccountCargo = new(Capacity: 8) } };
        var accountMove = move with { Count = 2, Destination = new(InventoryListType.AccountCargo, 1) };
        foreach (short target in new short[] { 1, 7 })
        {
            var result = InventoryMoveService.Plan(accountRole, accountMove with
                { Destination = new(InventoryListType.AccountCargo, target), DestinationItemId = target }, catalog, 0);
            That(result.Accepted && result.Plan!.After.Find(new(InventoryListType.AccountCargo, target)) is { Uid: 1 } accountItem &&
                accountItem.Core == source.Core, "账号仓库升级后的非零空格接受客户端格号且保留UID和完整核心");
        }
        That(!InventoryMoveService.Plan(accountRole, accountMove with { DestinationItemId = 2 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(accountRole, accountMove with
                { Destination = new(InventoryListType.AccountCargo, 8), DestinationItemId = 8 }, catalog, 0).Accepted,
            "账号格号兼容不接受错误编号或超出8格容量的目标");
        var accountStored = source with { Uid = 3, Position = new(InventoryListType.AccountCargo, 1) };
        var accountWithdraw = new InventoryMoveRequest(accountStored.Position, 1, 1, source.Position, 0, 0, -1, 0, 0);
        var accountTaken = InventoryMoveService.Plan(accountRole with { Inventory = accountRole.Inventory with { Items = [accountStored] } },
            accountWithdraw, catalog, 0);
        That(accountTaken.Accepted && accountTaken.Plan!.After.Find(source.Position) is { Core.ItemId: 50, Core.Value: 1 } &&
            accountTaken.Plan.After.Find(accountStored.Position) is { Core.Value: 1 }, "账号非零格取出接受格号并正确同步拆堆数量");
        That(!InventoryMoveService.Plan(role, move with { SourceItemId = 51 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(role, move with { DestinationItemId = 2 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(role, move with { Destination = new(InventoryListType.PersonalCargo, 8), DestinationItemId = 8 }, catalog, 0).Accepted,
            "目标别名不放宽源模板、错误格号或容量");
        var occupied = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Add(source with { Uid = 2, Position = move.Destination }) } };
        var merged = InventoryMoveService.Plan(occupied, move, catalog, 0);
        That(merged.Accepted && merged.Plan!.After.Find(move.Destination)!.Core.Count == 3 &&
            merged.Plan.After.Find(source.Position)!.Core.Count == 1,
            "丢到同模板的已占用金库格时按格号别名并入，不覆盖原堆");
        var other = new DfoServer.GameWorld.Items.ItemTemplate(51, DfoServer.GameWorld.Items.ItemKind.Material,
            0, 1, 0, 100, 0, 0, 0, "[free]", 1, 0, "另一格材料");
        catalog.PrefillTemplates(new Dictionary<int, DfoServer.GameWorld.Items.ItemTemplate> { [50] = template, [51] = other });
        var mirror = source with { Uid = 8, Position = new(InventoryListType.Main, 3) };
        var echoRole = accountRole with { Inventory = accountRole.Inventory with { Items = [source, mirror] } };
        var echoRequest = accountMove with { Destination = new(InventoryListType.AccountCargo, 3), DestinationItemId = 50 };
        var echoResult = InventoryMoveService.Plan(echoRole, echoRequest, catalog, 0);
        That(echoResult.Accepted && echoResult.Plan!.After.Find(echoRequest.Destination) == source with { Position = echoRequest.Destination } &&
            echoResult.Plan.After.Find(mirror.Position) == mirror,
            "空账号格接受源容器同编号格模板回显，只移动真实源，保留被回显实例的UID和完整核心");
        That(InventoryMoveService.Plan(echoRole, echoRequest with { Destination = new(InventoryListType.PersonalCargo, 3) }, catalog, 0).Accepted,
            "同一原生跨容器发送器的个人仓库回显也保持兼容");
        That(!InventoryMoveService.Plan(echoRole, echoRequest with { DestinationItemId = 51 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(echoRole, echoRequest with { SourceItemId = 51 }, catalog, 0).Accepted &&
            !InventoryMoveService.Plan(echoRole with { Inventory = echoRole.Inventory with { AccountCargo = new(Capacity: 1) } },
                echoRequest, catalog, 0).Accepted,
            "回显兼容不接受未知目标编号、错误源模板或未开放仓库格");
        var echoBlocker = new InventoryItem(10, echoRequest.Destination, new ItemCore(51, other.Kind, 4));
        var echoOccupied = InventoryMoveService.Plan(echoRole with { Inventory = echoRole.Inventory with { Items = [source, mirror, echoBlocker] } },
            echoRequest, catalog, 0);
        That(echoOccupied.Accepted && echoOccupied.Plan!.After.Find(echoBlocker.Position) == echoBlocker &&
            echoOccupied.Plan.After.Find(new(InventoryListType.AccountCargo, 0))?.Uid == source.Uid &&
            echoOccupied.Plan.After.Find(mirror.Position) == mirror,
            "目标仓库实际已占用时改投合法空格，不把源容器回显当作授权覆盖目标物品");
        var reverseEcho = InventoryMoveService.Plan(echoRole with { Inventory = echoRole.Inventory with { Items = [mirror, echoBlocker] } },
            new(new(InventoryListType.Main, 121), 0, 0, echoBlocker.Position, 50, 0, -1, 0, 0), catalog, 0);
        That(reverseEcho.Accepted && reverseEcho.Plan!.After.Find(new(InventoryListType.Main, 121))?.Uid == echoBlocker.Uid &&
            reverseEcho.Plan.After.Find(echoBlocker.Position) is null && reverseEcho.Plan.After.Find(mirror.Position) == mirror,
            "空源反向取出不会误走存入改投空格，保留数量与第三格实例且不触发空引用");
        var returning = source with { Position = new(InventoryListType.AccountCargo, 1) };
        var cargoMirror = mirror with { Position = new(InventoryListType.AccountCargo, 3), Core = new(51, other.Kind, 4) };
        var returnEcho = InventoryMoveService.Plan(accountRole with { Inventory = accountRole.Inventory with { Items = [returning, cargoMirror] } },
            new(returning.Position, 50, 2, new(InventoryListType.Main, 3), 51, 0, -1, 0, 0), catalog, 0);
        That(returnEcho.Accepted && returnEcho.Plan!.After.Find(new(InventoryListType.Main, 3))?.Uid == source.Uid &&
            returnEcho.Plan.After.Find(cargoMirror.Position) == cargoMirror,
            "取出到空Main格也按真实源容器解释回显，不误消费另一件仓库物品");
        var blocker = new InventoryItem(9, move.Destination, new ItemCore(51, other.Kind, 4));
        var blocked = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Add(blocker) } };
        var relocated = InventoryMoveService.Plan(blocked, move, catalog, 0);
        That(relocated.Accepted && relocated.Plan!.After.Find(move.Destination) == blocker &&
            relocated.Plan.After.Find(new(InventoryListType.PersonalCargo, 0))!.Core.ItemId == 50,
            "直接丢到已占用金库格时改放空格，不替换原物品");
        That(!InventoryMoveService.Plan(blocked, move with { DestinationItemId = 99 }, catalog, 0).Accepted,
            "既不是格号也不是目标模板的编号仍然拒绝");
        var stored = new InventoryItem(3, new(InventoryListType.PersonalCargo, 4), new ItemCore(50, template.Kind, 6));
        var withdraw = new InventoryMoveRequest(stored.Position, 4, 2, new(InventoryListType.Main, 65), 0, 0, -1, 0, 0);
        var taken = InventoryMoveService.Plan(role with { Inventory = new([stored]) }, withdraw, catalog, 0);
        That(taken.Accepted && taken.Plan!.After.Find(withdraw.Destination)!.Core is { ItemId: 50, Value: 2 } &&
            taken.Plan.After.Find(stored.Position)!.Core.Count == 4,
            "从个人金库取出时接受格号作为源物品编号");
        That(!InventoryMoveService.Plan(role with { Inventory = new([stored]) }, withdraw with { SourceItemId = 51 }, catalog, 0).Accepted,
            "错误的源模板不能借格号别名取出");
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
        That(itemResources.Items.Get(6).PersonalCargoUpgradeTarget == 32 &&
            itemResources.Items.Get(110).PersonalCargoUpgradeTarget == 40 &&
            itemResources.Items.Get(50).PersonalCargoUpgradeTarget == 24,
            "旧银质升级为32格、新银质增加到40格和原24格工具分别保留真实资源容量");
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
            VerifyAccountPurchases(current, itemResources);
            var cashShop = new InventoryCashShopService(itemResources.Items, itemResources.CashShop, itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Cargo);
            var stalePurchaseRole = current;
            var stalePurchase = cashShop.Buy(current, new(0, [new(100534, 0)]), out _);
            current = Commit(currency.Grant(current, CurrencyKind.Gold, 1));
            string freshState = Snapshot();
            Reject(() => InventoryService.Commit(repo, owner, stalePurchaseRole, stalePurchase), "旧角色revision的购买计划拒绝");
            Reject(() => InventoryService.Commit(repo, owner, current, stalePurchase), "刷新expected也不能重绑旧购买资格");
            That(Snapshot() == freshState, "旧购买计划不改变费用、容量或审计");
            string beforeCashFailure = Snapshot();
            Sql("CREATE TRIGGER reject_cargo_cash BEFORE INSERT ON cargo_audit_log BEGIN SELECT RAISE(ABORT,'fixture'); END");
            Reject(() => Commit(cashShop.Buy(current, new(0, [new(100534, 0)]), out _)), "capacity failure rolls back account Cera and wallet version");
            Sql("DROP TRIGGER reject_cargo_cash");
            That(Snapshot() == beforeCashFailure, "failed paid upgrade retains balance, capacity and audits");
            foreach (var tier in itemResources.Cargo.AccountTiers.Where(tier => tier.Capacity > 8))
            {
                var before = current.Inventory;
                if (tier.ProductId == 0 && tier.MaterialCount > 0)
                {
                    var firstInput = current.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main &&
                        item.Core.ItemId == tier.MaterialId).OrderBy(item => item.Position.Index).First();
                    InventoryQualificationTests.CheckInputs(current, role => cargo.AccountUpgrade(role, false),
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), firstInput.Position);
                }
                current = Commit(tier.ProductId > 0 ? cashShop.Buy(current, new(0, [new(tier.ProductId, 0)]), out _) : cargo.AccountUpgrade(current, false));
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
            var purchaseFixture = current with { Inventory = current.Inventory with { AccountCargo = current.Inventory.AccountCargo with { Capacity = 8 } } };
            Reject(() => InventoryService.Commit(repo, owner, purchaseFixture,
                cashShop.Buy(purchaseFixture, new(0, [new(100534, 0)]), out _)), "旧owner不能提交账号购买");

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

    static void VerifyAccountPurchases(Character source, ItemResources resources)
    {
        // 匹配资源独立核对的31档矩阵，商品号与历史工具模板号分别断言。
        (ushort From, ushort To, int Product, int Tool, int Price)[] expected =
        [
            (0, 1, -1, 0, 0), (1, 8, -1, 0, 0), (8, 16, 100534, 2681921, 2000),
            (16, 24, -1, 0, 0), (24, 32, -1, 0, 0), (32, 40, 102045, 2682968, 2000),
            (40, 48, 102108, 2683046, 2000), (48, 56, 102316, 2683324, 2500), (56, 64, 102659, 2683926, 2500),
            (64, 72, 103665, 490700152, 3000), (72, 80, 103666, 490700153, 3000),
            (80, 88, 103667, 490700154, 4000), (88, 96, 103668, 490700155, 4000),
            (96, 104, 103669, 490700156, 5000), (104, 112, 103670, 490700157, 5000),
            (112, 120, 103671, 490700158, 6000), (120, 128, 104893, 490702625, 6000),
            (128, 136, 104894, 490702626, 7000), (136, 144, 104895, 490702627, 7000),
            (144, 152, 104896, 490702628, 8000), (152, 160, 104897, 490702629, 8000),
            (160, 168, 104898, 490702630, 9000), (168, 176, 104899, 490702631, 9000),
            (176, 184, 104900, 490702632, 10000), (184, 192, 104901, 490702633, 10000),
            (192, 200, 104902, 490702634, 11000), (200, 208, 104903, 490702635, 11000),
            (208, 216, 104904, 490702636, 12000), (216, 224, 104905, 490702637, 12000),
            (224, 232, 104906, 490702638, 13000), (232, 240, 104907, 490702639, 13000)
        ];
        That(resources.Cargo.AccountTiers.Count == expected.Length, "账号金库资源不丢失任何档位");
        foreach (var row in expected)
        {
            var tier = resources.Cargo.Next(row.From);
            That(tier is not null && tier.Capacity == row.To && tier.ProductId == row.Product && tier.ToolItemId == row.Tool && tier.CeraCost == row.Price,
                "31档真实商品、模板和点券矩阵 " + row.To);
        }
        var shop = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits, resources.EpicPieces, resources.Cargo);
        var cargo = new InventoryCargoService(resources.Items, resources.Cargo, resources.GoldLimits);
        var role = source with { Inventory = source.Inventory with { Items = [], AccountCargo = source.Inventory.AccountCargo with { Capacity = 8 } } };
        InventoryResult Buy(Character value, params int[] products) => shop.Buy(value, new(0, products.Select(id => new CashShopPurchaseItem(id, 0)).ToArray()), out _);
        That(!cargo.AccountUpgrade(role, false).Accepted, "裸306不能无商品凭据扣付费下一档");
        foreach (var products in new[] { new[] { 102045 }, new[] { 100534, 100534 }, new[] { 100534, int.MaxValue }, new[] { 100534, 102045 } })
            That(!Buy(role, products).Accepted, "未来档、重复商品、后续无效商品及跨材料档混单整笔拒绝");
        var poor = role with { Inventory = role.Inventory with { AccountWallet = role.Inventory.AccountWallet.Set(CurrencyKind.Cera, 1999) } };
        That(!Buy(poor, 100534).Accepted, "点券不足不扩容不发工具");
        var tool = new InventoryItem(100000, new(InventoryListType.Main, 3), new(2681921, GameWorld.Items.ItemKind.Consumable, 2));
        var historical = poor with { Level = 1, Inventory = poor.Inventory with { Items = [tool] } };
        var redeemed = Buy(historical, 100534);
        That(redeemed.Accepted && redeemed.Plan!.After.AccountCargo.Capacity == 16 &&
            redeemed.Plan.After.AccountWallet == historical.Inventory.AccountWallet && redeemed.Plan.After.Find(tool.Position)?.Core.Count == 1,
            "历史合法工具在商城主动购买兑换一张免二次点券，后续升级不增加开通等级门槛");
        var direct = cargo.AccountUpgrade(historical, false);
        That(direct.Accepted && direct.Plan!.After.AccountCargo.Capacity == 16 && direct.Plan.After.Find(tool.Position)?.Core.Count == 1 &&
            direct.Plan.After.AccountWallet == historical.Inventory.AccountWallet, "306也仅兑一张对应预付工具");
        foreach (var invalid in new[]
        {
            tool with { Core = tool.Core with { ItemId = 2682968 } },
            tool with { Core = tool.Core with { Kind = GameWorld.Items.ItemKind.Material } },
            tool with { Core = tool.Core with { EquipmentLockId = 1 } },
            tool with { Core = tool.Core with { ExpireTime = 1 } },
            tool with { Position = new(InventoryListType.PersonalCargo, 0) }
        })
        {
            var value = historical with { Inventory = historical.Inventory with { Items = [invalid] } };
            That(!Buy(value, 100534).Accepted && !cargo.AccountUpgrade(value, false).Accepted && value.Inventory.Items.Single() == invalid,
                "未来档、锁、过期及其他容器工具不充当凭据，余额不足零变化");
        }
        var passed = role with { Inventory = role.Inventory with { AccountCargo = role.Inventory.AccountCargo with { Capacity = 40 }, Items = [tool] } };
        That(!cargo.AccountUpgrade(passed, false).Accepted, "商城成功后跟随306不能推进下一付费档或消费旧档工具");
        That(!Buy(passed, 100534).Accepted && !Buy(role with { Inventory = role.Inventory with { AccountCargo = role.Inventory.AccountCargo with { Capacity = 240 } } }, 104907).Accepted,
            "已过档与满级拒绝重复收费");
        var chain = Buy(role with { Inventory = role.Inventory with { AccountCargo = role.Inventory.AccountCargo with { Capacity = 32 } } }, 102045, 102108);
        That(chain.Accepted && chain.Plan!.After.AccountCargo.Capacity == 48 && chain.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == role.Inventory.AccountWallet.Get(CurrencyKind.Cera) - 4000 &&
            chain.Plan.After.Items.IsEmpty, "连续合法付费档按计划状态推进且各扣一次，不发主工具");
        var mixedCatalog = GameWorld.Items.CashShopCatalog.Parse("[item etc]\n100534 2681921 1 0 0 2000 0 0 0\n700001 1108 1 0 0 7 0 0 0",
            "[bonus item]\n100534 3299 2\n[/bonus item]");
        var mixedShop = new InventoryCashShopService(resources.Items, mixedCatalog, resources.GoldLimits, resources.EpicPieces, resources.Cargo);
        var mixed = mixedShop.Buy(role, new(0, [new(100534, 0), new(700001, 0)]), out var mixedReceipt);
        That(mixed.Accepted && mixed.Plan!.After.AccountCargo.Capacity == 16 &&
            mixed.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == role.Inventory.AccountWallet.Get(CurrencyKind.Cera) - 2007 &&
            mixed.Plan.After.Items.Single(item => item.Core.ItemId == 3299).Core.Count == 2 &&
            mixed.Plan.After.Items.Single(item => item.Core.ItemId == 1108).Core.Count == 1 && mixed.Plan.After.Items.Length == 2 &&
            mixedReceipt!.Bonuses[100534].Single().ItemId == 3299, "账号直扩保留真实附赠及同车普通商品，合并扣费不吞奖励");
        var historicalBonus = mixedShop.Buy(historical, new(0, [new(100534, 0)]), out _);
        That(historicalBonus.Accepted && historicalBonus.Plan!.After.AccountWallet == historical.Inventory.AccountWallet &&
            historicalBonus.Plan.After.Items.Single(item => item.Core.ItemId == 3299).Core.Count == 2,
            "历史工具免点券仍完整规划商品附赠");
        var full = role with { Inventory = role.Inventory with { Items = Enumerable.Range(0, 56).Select(index =>
            new InventoryItem(index + 1, new(InventoryListType.Main, checked((short)(index + 65))), new(1108, GameWorld.Items.ItemKind.Consumable, 1))).ToImmutableArray() } };
        var fullPurchase = Buy(full, 100534);
        That(fullPurchase.Accepted && fullPurchase.Plan!.After.AccountCargo.Capacity == 16 && fullPurchase.Plan.OverflowItems.IsEmpty &&
            fullPurchase.Plan.After.Items.SequenceEqual(full.Inventory.Items), "账号商城扩容不依赖Main空格，不另发工具或溢出邮件");
        var expires = tool with { Core = tool.Core with { ExpireTime = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120) } };
        var expiring = historical with { Inventory = historical.Inventory with { Items = [expires] } };
        var expiringPlan = Buy(expiring, 100534).Plan!;
        That(expiringPlan.CharacterPrecondition!.Matches(expiring, expires.ExpireTime - 1) &&
            !expiringPlan.CharacterPrecondition.Matches(expiring, expires.ExpireTime), "历史券期限在提交时复验");
    }
}
