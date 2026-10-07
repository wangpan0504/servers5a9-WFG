using DfoServer.Infrastructure;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class InventoryLockTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources, Action<bool, string> check, Action<Action, string> reject)
    {
        string folder = Path.Combine(Path.GetTempPath(), "s5a9-locks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("locks", () => { });
            var session = new GameSession(repository, towns, owner);
            var character = session.CreateCharacter("LockTest", characters.Get(0));
            var weapon = character.Inventory.Find(new(InventoryListType.Equipment, 12))!;
            var core = weapon.Core with
            {
                Attr = 8, EnchantCardId = 123, EnchantUpgrade = 2, AmplifyType = 1, AmplifyValue = 22,
                Rune = 17, TradeRestriction = 2, ExpireTime = int.MaxValue, Extension27 = 5,
                Chronicle0 = new(11, 1, 2, 3, 4), Random0 = new(1, 2, 3), RandomState = 4,
                GuardianGemKeys = 0x1122334455667788, Extension114 = new(104, 105)
            };
            character = repository.CommitInventory(owner, character, new(character.Inventory,
                character.Inventory with { Items = character.Inventory.Items.Replace(weapon, weapon with { Core = core }) }, "fixture_details"));
            session.Select(0);
            session.MarkReady();
            var service = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            InventoryResult Lock(InventorySlot slot, InventoryLockOperation operation) => session.MutateInventory(c => service.Lock(c, slot, operation));
            InventoryResult Move(InventorySlot from, InventorySlot to) => session.MutateInventory(c => service.Move(c,
                new(from, core.ItemId, 1, to, 0, 0, -1, 0, 0)));
            var worn = weapon.Position;
            var last = new InventorySlot(InventoryListType.Main, 64);
            var quick = new InventorySlot(InventoryListType.Main, 8);
            check(Lock(worn, InventoryLockOperation.EquipmentLock).Accepted, "lock worn equipment");
            check(session.Selected!.Inventory.Find(worn)?.Core == core with { EquipmentLockId = 1 }, "lock changes only safety lock ID");
            string locked = Snapshot();
            check(!Lock(worn, InventoryLockOperation.EquipmentLock).Accepted && Snapshot() == locked, "repeated equipment lock causes no audit or revision");
            check(Move(worn, last).Accepted && session.Selected!.Inventory.Find(last)?.Uid == weapon.Uid, "safety-locked equipment moves to final expanded slot");
            check(Lock(last, InventoryLockOperation.SortLock).Accepted, "set sorting lock");
            locked = Snapshot();
            check(Lock(last, InventoryLockOperation.SortLock).Accepted && Snapshot() == locked, "repeated Client sort lock is idempotent");
            check(!service.Sort(session.Selected!, ItemKind.Equipment, 0).Plan!.Changed, "sorting lock holds expanded page position");
            check(!InventoryDeleteService.Plan(session.Selected!.Inventory, last, core.Value, 1).Accepted, "safety lock protects discard");
            check(Move(last, quick).Accepted && session.Selected!.Inventory.Find(quick)?.Core == core with { EquipmentLockId = 1 },
                "quick bar clears only sorting lock and preserves every asset field");
            check(Lock(quick, InventoryLockOperation.EquipmentUnlock).Accepted && session.Selected!.Inventory.Find(quick)?.Core == core,
                "immediate unlock preserves complete item core");
            check(!Lock(quick, InventoryLockOperation.CancelUnlock).Accepted, "cancel cannot relock already unlocked item");
            check(Lock(quick, InventoryLockOperation.EquipmentLock).Accepted && session.Selected!.Inventory.Find(quick)?.Core.EquipmentLockId == 1,
                "released lock ID is reusable without changing UID");
            locked = Snapshot();
            check(Lock(quick, InventoryLockOperation.CancelUnlock).Accepted && Snapshot() == locked, "cancel keeps existing lock without duplicate persistence");
            check(Lock(quick, InventoryLockOperation.SortUnlock).Accepted && Snapshot() == locked, "repeated sort unlock is read-only");

            var beforeFailure = session.Selected;
            Sql("CREATE TRIGGER fail_lock BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'lock failure'); END");
            reject(() => Lock(quick, InventoryLockOperation.EquipmentUnlock), "failed lock commit throws");
            check(session.Selected == beforeFailure && Snapshot() == locked, "lock failure rolls back core, revision, audit and online snapshot");
            Sql("DROP TRIGGER fail_lock");
            var stale = session.Selected!;
            var stalePlan = service.Lock(stale, quick, InventoryLockOperation.EquipmentUnlock).Plan!;
            Lock(quick, InventoryLockOperation.SortLock);
            reject(() => repository.CommitInventory(owner, stale, stalePlan), "stale revision cannot remove newer sorting lock");
            session.ReturnToRoster();
            session.Select(0);
            session.MarkReady();
            check(session.Selected!.Inventory.Find(quick) is { Uid: var uid, Core.EquipmentLockId: 1, Core.SortLockFlag: 1 } && uid == weapon.Uid,
                "reselection restores both locks on original UID");
            check(Lock(new((InventoryListType)2, 8), InventoryLockOperation.EquipmentLock).Error == 19 &&
                Lock(new(InventoryListType.Main, 9), InventoryLockOperation.EquipmentLock).Error == 21, "invalid container and empty lock target are distinct");

            using var fresh = owners.Acquire(owner.Account, () => { });
            reject(() => Lock(quick, InventoryLockOperation.EquipmentUnlock), "old generation cannot unlock");
            character = repository.List(fresh).Single();
            var expiring = character.Inventory.Find(quick)!;
            var wornExpiring = character.Inventory.Items.First(item => item.Position.List == InventoryListType.Equipment);
            var expiredInventory = character.Inventory with { Items = character.Inventory.Items
                .Replace(expiring, expiring with { Core = expiring.Core with { ExpireTime = 1 } })
                .Replace(wornExpiring, wornExpiring with { Core = wornExpiring.Core with { ExpireTime = 1, EquipmentLockId = 2 } }) };
            character = repository.CommitInventory(fresh, character, new(character.Inventory, expiredInventory, "fixture_expiry"));
            var selection = new GameSession(repository, towns, fresh);
            string beforeExpiry = Snapshot();
            Sql("CREATE TRIGGER fail_expiry BEFORE DELETE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'expiry failure'); END");
            reject(() => selection.Select(0), "expiry failure rejects selection");
            check(selection.Selected is null && Snapshot() == beforeExpiry, "failed expiry leaves no published character or partial deletions");
            Sql("DROP TRIGGER fail_expiry");
            selection.Select(0);
            check(selection.Selected!.Inventory.Items.Length == character.Inventory.Items.Length - 2 &&
                selection.Selected.Inventory.Find(quick) is null && selection.Selected.Inventory.Find(wornExpiring.Position) is null,
                "selection removes expired locked quick-bar and worn items before projection");
            check(Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired'") is 2L,
                "expiry audit records each removed position");
            string afterExpiry = Snapshot();
            selection.ReturnToRoster();
            selection.Select(0);
            check(Snapshot() == afterExpiry, "reselection does not repeat expiry writes");
            check(InventoryExpiryService.Plan(new([new(123, last, core)]), int.MaxValue - 1L).Plan is { Changed: false }, "future deadline stays intact");
            check(InventoryExpiryService.Plan(new([new(123, last, core)]), int.MaxValue).Plan!.After.Items.IsEmpty, "deadline is inclusive");

            new CharacterRepository(path).List(fresh);
            check(Snapshot() == afterExpiry, "reopen preserves inventory after expiry without granting assets");
            check(Query("PRAGMA integrity_check") is "ok", "lock database integrity");

            check(InventoryRequests.Lock([3, 12, 0]) == worn, "Client lock request widths");
            reject(() => InventoryRequests.Lock([3, 12]), "truncated lock request rejected");
            reject(() => InventoryRequests.Lock([3, 12, 0, 0]), "lock request trailing bytes rejected");
            check(Convert.ToHexString(InventoryLockPackets.Ack(worn, InventoryLockOperation.EquipmentUnlock, new(true, 0))) == "01030C0000000000", "unlock success has zero remaining seconds");
            check(Convert.ToHexString(InventoryLockPackets.Ack(worn, InventoryLockOperation.EquipmentUnlock, InventoryResult.Reject(19))) == "00130000000000", "unlock failure includes public u16 and i32");
            check(Convert.ToHexString(InventoryLockPackets.Ack(worn, InventoryLockOperation.SortLock, InventoryResult.Reject(21))) == "00150000", "sort lock failure includes trailing reader byte");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                return cmd.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);
            string Snapshot() => JsonSerializer.Serialize(new { Character = Query("SELECT json_group_array(json_object('id',id,'revision',revision,'expansion',inventory_expansion)) FROM characters"),
                Items = Query("SELECT json_group_array(json_object('uid',item_uid,'core',item_core)) FROM character_inventory_items"),
                Slots = Query("SELECT json_group_array(json_object('uid',item_uid,'list',list_type,'slot',slot_index)) FROM character_inventory_slots"),
                Audit = Query("SELECT count(*) FROM inventory_audit_log") });
        }
        finally { Directory.Delete(folder, true); }
    }
}
