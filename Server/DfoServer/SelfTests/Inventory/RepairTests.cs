using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
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

internal static class RepairTests
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
        var repair = new InventoryRepairService(catalog, itemResources.Repair);
        var coat = catalog.Get(10400);
        That(coat is { Durability: 33, Grade: 1, RepairPrice: 400 }, "repair metadata from matching PVF");
        That(itemResources.Repair.TryPrice(coat, 0, 0, false, out int cost) && cost == 19, "ordinary repair price truncates per item");
        That(itemResources.Repair.TryPrice(coat, 0, 0, true, out cost) && cost == 28, "quick rate applied before truncation");
        That(itemResources.Repair.TryPrice(coat, 1, 255, true, out cost) && cost == 27, "upgrade table clamps at last declared rate");
        That(itemResources.Repair.TryPrice(coat, 33, 0, false, out cost) && cost == 0, "full durability is free no-op");
        That(!itemResources.Repair.TryPrice(coat with { RepairPrice = int.MaxValue, Grade = int.MaxValue }, 0, 0, false, out _), "overflow never becomes free repair");
        var request = RepairRequests.Read([0, 3, 0, 255, 255, 0, 0, 0]);
        That(request == new RepairEquipmentRequest(Bag(3), -1, 0, false), "eight-byte manual request");
        That(RepairRequests.Read([0, 255, 255, 255, 255, 1, 0]) is { Mode: 1, Quick: false, Position.Index: -1 }, "seven-byte automatic request preserves mode");
        That(RepairRequests.Read([0, 3, 0, 255, 255, 0, 1, 1]) is { Mode: 256, Quick: true }, "mode is a complete ushort");
        foreach (var body in new byte[][] { new byte[6], new byte[9], [0, 3, 0, 255, 255, 0, 0, 2] })
            Reject(() => RepairRequests.Read(body), "malformed repair rejected");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("repair", () => { });
            var core = InventoryRewardGrantService.Create(coat) with
            {
                Durability = 1, Attr = 7, EnchantUpgrade = 11, EnchantCardId = 3175, AmplifyType = 2, AmplifyValue = 21,
                EquipmentLockId = 1, SortLockFlag = 1, SealFlag = 1, Extension27 = 1234, Rune = 8
            };
            var current = repo.Create(owner, new("RepairOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), core), new(0, Bag(9), core with { EquipmentLockId = 0 }),
                    new(0, Worn(14), InventoryRewardGrantService.Create(catalog.Get(10402)) with { Durability = 0 }),
                    new(0, Cargo(0), core)], Gold: 1000)
            });
            var before = current;
            var batch = repair.Repair(current, request with { Position = Bag(-1) });
            That(batch.Accepted && batch.Plan!.ChangedSlots.Length == 2 && batch.Plan.After.Gold == 822,
                "all repair includes both damaged quickbar and worn equipment");
            That(!repair.Repair(current with { Inventory = current.Inventory with { Gold = 177 } }, request with { Position = Bag(-1) }).Accepted,
                "all-repair fee is preflighted across every candidate");
            InventoryQualificationTests.CheckInputs(current, role => repair.Repair(role, request),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Bag(3));
            InventoryQualificationTests.CheckInputs(current, role => repair.Repair(role, request with { Position = Bag(-1) }),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Bag(3), Worn(14));
            var result = repair.Repair(current, request);
            That(result.Accepted && result.Plan!.After.Gold == 982, "single quickbar costs only missing durability");
            var ack = RepairPackets.Ack(request, result);
            That(ack.Length == 10 && ack[0] == 1 && BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(1)) == 982 &&
                ack[5] == 0 && ack[6] == 3 && ack[8] == 0 && ack[9] == 0, "ACK contains balance, original position and required tail");
            current = InventoryService.Commit(repo, owner, current, result);
            var old = before.Inventory.Find(Bag(3))!;
            var saved = current.Inventory.Find(Bag(3))!;
            That(saved.Uid == old.Uid && (saved.Core with { Durability = old.Core.Durability }) == old.Core,
                "repair preserves UID, locks, seal and every non-durability field");
            long audit = (long)Query("SELECT count(*) FROM inventory_audit_log")!;
            InventoryQualificationTests.CheckInputs(current, role => repair.Repair(role, request),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Bag(3));
            var repeated = repair.Repair(current, request);
            That(repeated.Accepted && !repeated.Plan!.Changed && InventoryService.Commit(repo, owner, current, repeated) == current,
                "repeat full repair does not charge or advance revision");
            That((long)Query("SELECT count(*) FROM inventory_audit_log")! == audit, "repeat full repair writes no audit");
            Reject(() => repo.CommitInventory(owner, before, result.Plan!), "old revision cannot repair twice");
            That(!repair.Repair(current, request with { Position = new(InventoryListType.AccountCargo, -1) }).Accepted, "unknown source cannot select all");
            That(!repair.Repair(current, request with { Position = Bag(-2) }).Accepted, "only minus one is all-repair sentinel");
            That(!repair.Repair(current, request with { Mode = 1 }).Accepted && !repair.Repair(current, request with { RepairItemSlot = 5 }).Accepted,
                "contract and coupon requests cannot become unauthorized free or gold repairs");
            var expired = current with { Inventory = current.Inventory with
            {
                Items = current.Inventory.Items.Replace(saved, saved with { Core = saved.Core with { Durability = 0, ExpireTime = 1 } })
            } };
            That(!repair.Repair(expired, request).Accepted, "repair does not revive expired equipment");
            var all = request with { Position = Bag(-1) };
            result = repair.Repair(current with { Inventory = current.Inventory with { Gold = 159 } }, all);
            That(!result.Accepted && current.Inventory.Find(Worn(14))!.Core.Durability == 0, "unaffordable all-repair changes no item");
            result = repair.Repair(current, all);
            That(result.Accepted && result.Plan!.After.Gold == 822 && result.Plan.ChangedSlots.Length == 1,
                "all repair includes worn equipment, excludes ordinary bag and cargo");
            current = InventoryService.Commit(repo, owner, current, result);
            That(current.Inventory.Find(Bag(9))!.Core.Durability == 1 && current.Inventory.Find(Cargo(0))!.Core.Durability == 1,
                "unselected containers remain damaged");
            result = repair.Repair(current, request with { Position = Cargo(0), Quick = true });
            That(result.Accepted && result.Plan!.After.Gold == 795, "single cargo and quick repair supported");
            current = InventoryService.Commit(repo, owner, current, result);
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            current = session.Selected!;
            string snapshot = Snapshot();
            Query("CREATE TRIGGER fail_repair BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'repair rollback'); END");
            Reject(() => session.MutateInventory(character => repair.Repair(character, request with { Position = Bag(9) })), "SQL failure rejects before success");
            That(Snapshot() == snapshot && session.Selected == current, "failed repair rolls back gold, durability, audit and session");
            Query("DROP TRIGGER fail_repair");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(current),
                "restart restores exact durability, attributes and ownership");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(character => repair.Repair(character, request)), "old generation rejected even for no-op");
            That(Query("PRAGMA integrity_check") is "ok", "repair database integrity");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") + ":" + Query("SELECT count(*) FROM currency_audit_log");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} equipment repair assertions");
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static InventorySlot Cargo(short slot) => new(InventoryListType.PersonalCargo, slot);
    static InventorySlot Worn(short slot) => new(InventoryListType.Equipment, slot);
}
