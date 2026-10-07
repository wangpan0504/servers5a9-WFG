using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using PvfLib;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class InventorySelfTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL inventory: " + name);
        }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try
            { action(); }
            catch (Exception e) when (e is SqliteException or InvalidOperationException or InvalidDataException)
            {
                rejected = true;
            }
            Check(rejected, name);
        }
        var catalog = itemResources.Items;
        var service = new InventoryService(catalog, itemResources.GoldLimits, itemResources.EpicPieces);
        var stack = PvfScript.List(pvf.ReadText("stackable/stackable.lst")).Keys.Where(id => id > 2)
            .Select(id => TryGet(id)).First(item => item is { Kind: ItemKind.Consumable, StackLimit: >= 30, Expiration: { AbsoluteTime: 0, Days: 0 } })!;
        ItemTemplate? TryGet(int id)
        {
            try
            { return catalog.Get(id); }
            catch (InvalidDataException) { return null; }
        }
        string folder = Path.Combine(Path.GetTempPath(), "s5a9-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            InventoryLockTests.Run(towns, characters, itemResources, Check, Reject);
            string path = Path.Combine(folder, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("inventory-test", () => { });
            for (byte job = 0; job < 16; job++)
            {
                var session = new GameSession(repo, towns, owner);
                var created = session.CreateCharacter("Inventory" + job, characters.Get(job));
                var profile = characters.Get(job);
                Check(created.Inventory.Items.Length == 5 && created.Inventory.Items.All(item => item.Uid > 0), "five allocated starter instances for job " + job);
                Check(created.Inventory.Expansion == 24, "ordinary creation opens all three bag expansions " + job);
                Check(created.Inventory.Items.Select(item => (item.Position.Index, item.Core.ItemId)).SequenceEqual(profile.Equipment.Select(item => ((short)item.Slot, item.Template.Id))), "starter codes and slots come from CHR " + job);
                Check(created.Inventory.Items.All(item => item.Core.Durability == catalog.Get(item.Core.ItemId).Durability && item.Core.Attr == 0 && item.Core.SealFlag == 0), "PVF durability and clean attributes " + job);
                Check(created.Inventory.Items.All(item => InventoryMoveService.Allows(created, item.Position, item.Core, catalog)), "PVF wearing restrictions " + job);
                Check(repo.List(owner)[job].Inventory.Items.SequenceEqual(created.Inventory.Items), "starter persists across reread " + job);
            }
            Check(repo.List(owner).SelectMany(character => character.Inventory.Items).Select(item => item.Uid).Distinct().Count() == 80, "central unique UID allocation");
            var current = repo.List(owner)[0];
            InventoryBehaviorTests.Run(current, pvf, characters, itemResources, Check);
            InventoryStackLimitTests.Run(current, catalog, itemResources, Check);
            var captured = InventoryRequests.Move(Convert.FromHexString("00090000000000010000000310007030000000000000FFFFFFFF0000"));
            Check(captured == new InventoryMoveRequest(new(InventoryListType.Main, 9), 0, 1, new(InventoryListType.Equipment, 16), 12400, 0, -1, 0, 0),
                "captured unequip uses empty main first and template ID second");
            var capturedResult = service.Move(current, captured);
            var jacket = current.Inventory.Find(captured.Destination)!;
            Check(capturedResult.Accepted && capturedResult.Plan!.After.Find(captured.Source) == jacket with { Position = captured.Source } &&
                capturedResult.Plan.After.Find(captured.Destination) is null, "captured unequip preserves UID and all core fields");
            Check(Convert.ToHexString(InventoryPackets.Move(captured, capturedResult)) == "0100090001000000031000", "unequip ACK preserves original main/equipment order");
            Check(service.Move(current, captured with { DestinationItemId = jacket.Core.Value }) is { Accepted: false, Error: 23 },
                "quality value is not the request item ID and does not report area restriction");
            Check(!service.Move(current, captured with { SourceItemId = jacket.Core.ItemId }).Accepted, "empty first slot must echo zero before reverse");
            Check(!service.Move(current, captured with { Source = new(InventoryListType.Main, 65) }).Accepted, "reverse unequip validates destination page");
            Check(service.Move(current, captured with { Count = 2 }) is { Accepted: true, MoveCount: 1 }, "equipment move normalizes to one instance");
            Check(!service.Move(current, captured with { Destination = new(InventoryListType.Main, 10), DestinationItemId = 0 }).Accepted, "two empty slots cannot move");
            foreach (var worn in current.Inventory.Items)
            {
                var takeOff = new InventoryMoveRequest(new(InventoryListType.Main, 9), 0, 1, worn.Position, worn.Core.ItemId, 0, -1, 0, 0);
                var unequipped = service.Move(current, takeOff);
                Check(unequipped.Accepted && unequipped.Plan!.After.Find(takeOff.Source) == worn with { Position = takeOff.Source }, "empty-first unequip slot " + worn.Position.Index);
                var inBag = current with { Inventory = unequipped.Plan!.After };
                Check(!service.Move(inBag, takeOff).Accepted, "replayed unequip cannot equip slot " + worn.Position.Index);
                var putOn = takeOff with { SourceItemId = worn.Core.ItemId, DestinationItemId = 0 };
                var equipped = service.Move(inBag, putOn);
                Check(equipped.Accepted && equipped.Plan!.After.Items.SequenceEqual(current.Inventory.Items), "reequip restores original UID and complete core slot " + worn.Position.Index);
                Check(!service.Move(current, putOn).Accepted, "replayed equip cannot unequip slot " + worn.Position.Index);
            }
            var weapon = current.Inventory.Find(new(InventoryListType.Equipment, 12))!;
            var request = new InventoryMoveRequest(weapon.Position, weapon.Core.ItemId, 1, new(InventoryListType.Main, 9), 0, 0, -1, 0, 0);
            var result = service.Move(current, request);
            current = InventoryService.Commit(repo, owner, current, result);
            Check(current.Inventory.Find(request.Destination) == weapon with { Position = request.Destination }, "unequip preserves UID and complete core");
            Check(!service.Move(current, request).Accepted, "replayed emptied source rejected");
            Check(!service.Move(current, request with { Source = request.Destination, Destination = new(InventoryListType.Main, 65) }).Accepted, "equipment cannot enter consumable page");
            Check(Query("SELECT count_before FROM inventory_audit_log WHERE action_name='move_item' AND list_type=3 AND slot_index=12 ORDER BY audit_id DESC LIMIT 1") is 1L &&
                Query("SELECT count_delta FROM inventory_audit_log WHERE action_name='move_item' AND list_type=3 AND slot_index=12 ORDER BY audit_id DESC LIMIT 1") is -1L,
                "equipment audit records one item rather than quality value");
            var reverse = request with { Source = request.Destination, Destination = weapon.Position };
            var stale = current;
            current = InventoryService.Commit(repo, owner, current, service.Move(current, reverse));
            Check(current.Inventory.Find(weapon.Position)?.Uid == weapon.Uid, "reequip keeps UID");
            Reject(() => repo.CommitInventory(owner, stale, service.Move(stale, reverse).Plan!), "stale revision cannot overwrite committed equipment");
            result = service.Grant(current, [new(stack.Id, 20)]);
            current = InventoryService.Commit(repo, owner, current, result);
            var consumable = current.Inventory.Items.Single(item => item.Core.ItemId == stack.Id);
            var split = new InventoryMoveRequest(consumable.Position, stack.Id, 7, new(InventoryListType.Main, 66), 0, 0, -1, 0, 0);
            current = InventoryService.Commit(repo, owner, current, service.Move(current, split));
            Check(current.Inventory.Find(consumable.Position)?.Core.Value == 13 && current.Inventory.Find(consumable.Position)?.Uid == consumable.Uid, "split reduces original UID count");
            var divided = current.Inventory.Find(split.Destination)!;
            Check(divided.Uid != consumable.Uid && divided.Core == consumable.Core with { Value = 7 }, "split allocates one UID and copies complete metadata");
            Check(!service.Move(current, split).Accepted, "replayed split cannot duplicate items");
            current = InventoryService.Commit(repo, owner, current, service.Move(current, new(divided.Position, stack.Id, 0, consumable.Position, stack.Id, 0, -1, 0, 0)));
            Check(current.Inventory.Find(consumable.Position)?.Core.Value == 20 && current.Inventory.Find(divided.Position) is null, "merge preserves target UID and sums counts");
            var locked = current.Inventory.Find(consumable.Position)!;
            var detailed = locked.Core with
            {
                SortLockFlag = 1,
                EnchantCardId = 31,
                EnchantUpgrade = 2,
                AmplifyType = 3,
                AmplifyValue = 55,
                Marker16 = 17,
                Extension26 = 4,
                Extension27 = 5,
                Extension31 = 6,
                Extension35 = 7,
                Chronicle0 = new(11, 1, 2, 3, 4),
                EmblemSocketCount = 2,
                Emblem1 = 41,
                Emblem2 = 42,
                Random0 = new(1, 2, 3),
                RandomState = 4,
                GuardianGemKeys = 0x1122334455667788,
                Extension97 = 101,
                Extension109 = 102,
                Extension110 = 103,
                Extension114 = new(104, 105),
                Extension119 = 106,
                Extension120 = 107,
                Extension121 = 108
            };
            var detailPlan = new InventoryPlan(current.Inventory, current.Inventory with { Items = current.Inventory.Items.Replace(locked, locked with { Core = detailed }) }, "fixture_attributes");
            current = repo.CommitInventory(owner, current, detailPlan);
            Check(repo.List(owner)[0].Inventory.Find(locked.Position)?.Core == detailed, "all typed fields survive serialization");
            var beforeSort = current.Inventory;
            current = InventoryService.Commit(repo, owner, current, service.Sort(current, ItemKind.Consumable, 0));
            Check(current.Inventory == beforeSort && current.Inventory.Find(locked.Position)?.Core == detailed, "sort lock makes sorting a zero-change plan");
            using (var blocking = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                blocking.Open();
                using var writer = blocking.BeginTransaction(deferred: false);
                var noChange = service.Move(current, new(locked.Position, stack.Id, 0, locked.Position, stack.Id, 0, -1, 0, 0));
                Check(noChange.Accepted && !noChange.Plan!.Changed, "same slot with matching item IDs is a no-op");
                Check(ReferenceEquals(InventoryService.Commit(repo, owner, current, noChange), current), "no-op performs no SQLite write even with writer occupied");
                Check(!service.Move(current, request with { SourceItemId = request.SourceItemId + 1 }).Accepted, "reject remains read-only under writer contention");
            }
            var delete = new InventoryDeleteRequest(InventoryListType.Main, [new(0, locked.Position.Index, 20, 3)], 0);
            current = InventoryService.Commit(repo, owner, current, service.Delete(current, delete));
            Check(current.Inventory.Find(locked.Position)?.Core == detailed with { Value = 17 }, "partial discard preserves metadata");
            Check(!service.Delete(current, delete).Accepted, "replayed discard value mismatch rejects");
            Check(Query("SELECT count_delta FROM inventory_audit_log WHERE action_name='delete_item' ORDER BY audit_id DESC LIMIT 1") is -3L,
                "discard audit records actual removed quantity");
            var mainGear = current.Inventory with { Expansion = 24 };
            var core = InventoryRewardGrantService.Create(catalog.Get(weapon.Core.ItemId));
            var quickGear = new InventoryItem(1003, new(InventoryListType.Main, 21), detailed with
            {
                ItemId = 18400,
                Kind = ItemKind.Equipment,
                Value = InventoryRewardGrantService.InitialQuality,
                SortLockFlag = 0
            });
            var quickCharacter = current with { Inventory = new([quickGear]) };
            var quickRequest = InventoryRequests.Move(Convert.FromHexString("0003000000000000000000001500E047000000000000FFFFFFFF0000"));
            var quickMove = service.Move(quickCharacter, quickRequest);
            Check(quickMove.Accepted && quickMove.Plan!.After.Find(quickRequest.Source) == quickGear with { Position = quickRequest.Source } &&
                quickMove.Plan.After.Find(quickRequest.Destination) is null, "captured equipment move into empty quick slot preserves UID and complete core");
            Check(Convert.ToHexString(InventoryPackets.Move(quickRequest, quickMove)) == "0100030001000000001500", "quick slot count-zero request replies one equipment in original order");
            quickCharacter = quickCharacter with { Inventory = quickMove.Plan!.After };
            Check(!service.Move(quickCharacter, quickRequest).Accepted, "replayed quick slot move cannot return equipment to page");
            Check(!service.Sort(quickCharacter, ItemKind.Equipment, 0).Plan!.Changed, "equipment page sort leaves unlocked quick slot equipment in place");
            var quickGrant = service.Grant(quickCharacter, [new(18400, 1)]);
            Check(quickGrant.Accepted && quickGrant.Plan!.After.Find(quickRequest.Source)?.Uid == quickGear.Uid &&
                quickGrant.Plan.After.Find(new(InventoryListType.Main, 9))?.Core.ItemId == 18400, "new equipment still goes to its page without displacing quick slot equipment");
            var quickWear = service.Move(quickCharacter, new(quickRequest.Source, 18400, 1, new(InventoryListType.Equipment, 17), 0, 0, -1, 0, 0));
            Check(quickWear.Accepted && quickWear.Plan!.After.Find(new(InventoryListType.Equipment, 17)) == quickGear with { Position = new(InventoryListType.Equipment, 17) },
                "wear directly from quick slot keeps complete equipment core");
            var quickEight = service.Move(quickCharacter with { Inventory = quickWear.Plan!.After }, new(new(InventoryListType.Main, 8), 0, 1, new(InventoryListType.Equipment, 17), 18400, 0, -1, 0, 0));
            Check(quickEight.Accepted && quickEight.Plan!.After.Find(new(InventoryListType.Main, 8))?.Uid == quickGear.Uid, "unequip directly into final quick slot");
            var quickSwapState = quickCharacter.Inventory with { Items = quickCharacter.Inventory.Items.Add(new(1004, new(InventoryListType.Main, 8), consumable.Core)) };
            var quickSwap = service.Move(quickCharacter with { Inventory = quickSwapState }, new(new(InventoryListType.Main, 3), 18400, 1, new(InventoryListType.Main, 8), stack.Id, 0, -1, 0, 0));
            Check(quickSwap.Accepted && quickSwap.Plan!.After.Find(new(InventoryListType.Main, 3))?.Uid == 1004 &&
                quickSwap.Plan.After.Find(new(InventoryListType.Main, 8))?.Uid == quickGear.Uid, "equipment and consumable can exchange between quick slots");
            var wrongPage = quickSwapState with { Items = quickSwapState.Items.SetItem(0, quickGear with { Position = new(InventoryListType.Main, 9) }) };
            Check(!service.Move(quickCharacter with { Inventory = wrongPage }, new(new(InventoryListType.Main, 9), 18400, 1, new(InventoryListType.Main, 8), stack.Id, 0, -1, 0, 0)).Accepted,
                "quick slot swap cannot move consumable into equipment page");
            Check(!ItemSlotBoundService.Allows(0, new(InventoryListType.Main, 2), ItemKind.Equipment) &&
                !ItemSlotBoundService.Allows(0, new(InventoryListType.Main, 65), ItemKind.Equipment), "quick slot support preserves reserved and page boundaries");
            Check(!ItemSlotBoundService.Allows(0, new(InventoryListType.Main, 3), ItemKind.Emblem) &&
                !ItemSlotBoundService.Allows(0, new(InventoryListType.Main, 3), (ItemKind)255), "quick slot support excludes emblems and unknown kinds");
            var fill = Enumerable.Range(9, 56).Select(slot => new InventoryItem(1000 + slot, new(InventoryListType.Main, (short)slot), core)).ToImmutableArray();
            var full = mainGear with { Items = fill };
            Check(!InventoryRewardGrantService.TryGrantBatch(current with { Inventory = full }, [new(stack.Id, 1), new(core.ItemId, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces).Accepted && full.Items == fill, "whole reward batch rejects overflow without partial grant");
            var swapSnapshot = new InventorySnapshot([
                new(1001, new(InventoryListType.Main, 9), core with { Attr = 3, EnchantCardId = 55 }),
                new(1002, new(InventoryListType.Main, 10), core with { Attr = 5, ExpireTime = int.MaxValue })]);
            var swap = service.Move(current with { Inventory = swapSnapshot }, new(new(InventoryListType.Main, 9), core.ItemId, 1, new(InventoryListType.Main, 10), core.ItemId, 0, -1, 0, 0));
            Check(swap.Accepted && swap.Plan!.After.Find(new(InventoryListType.Main, 9)) == swapSnapshot.Items[1] with { Position = new(InventoryListType.Main, 9) } &&
                swap.Plan.After.Find(new(InventoryListType.Main, 10)) == swapSnapshot.Items[0] with { Position = new(InventoryListType.Main, 10) },
                "whole equipment exchange preserves both UIDs and complete metadata");
            var fallback = service.Sort(current with { Inventory = swapSnapshot }, ItemKind.Equipment, 4);
            Check(!fallback.Plan!.Changed, "unsupported equipment expiration condition falls back to stable default");
            Check(InventorySortService.RarityRank(4) > InventorySortService.RarityRank(6) &&
                InventorySortService.RarityRank(6) > InventorySortService.RarityRank(3) && InventorySortService.RarityRank(3) > InventorySortService.RarityRank(5),
                "equipment rarity ordering ranks epic, legendary, artifact and chronicle");
            foreach (var (expansion, stage) in new (ushort, int)[] { (0, 0), (8, 1), (16, 2), (24, 3) })
            {
                var range = ItemSlotBoundService.Range(ItemKind.Equipment, expansion);
                Check(range.Start == 9 && range.End == 40 + expansion && !ItemSlotBoundService.Allows(expansion, new(InventoryListType.Main, (short)(range.End + 1)), ItemKind.Equipment), "page bounds " + expansion);
                var body = InventoryPackets.Full(InventorySnapshot.Empty with { Expansion = expansion });
                // 按客户端023BDF17/023BDF38消费字段，不能将已除8的阶段放入高字节。
                int clientStage = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)) >> 3;
                Check(clientStage == stage && clientStage <= 3 && range.End == 40 + clientStage * 8,
                    "client expansion stage agrees with server page capacity " + expansion);
            }
            var expired = locked with { Core = detailed with { Value = 17, SortLockFlag = 0, ExpireTime = 1 } };
            var expCharacter = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.Replace(current.Inventory.Find(locked.Position)!, expired) } };
            Check(!service.Move(expCharacter, new(locked.Position, stack.Id, 1, new(InventoryListType.Main, 66), 0, 0, -1, 0, 0)).Accepted, "expired item cannot move");
            var expiration = service.Sort(expCharacter, ItemKind.Consumable, 0);
            Check(expiration.Plan!.After.Find(locked.Position) is null, "sort expires movable items before assigning slots");
            var bound = expired with { Core = expired.Core with { EquipmentLockId = 1 } };
            Check(!InventoryDeleteService.Plan(expCharacter.Inventory with { Items = [bound] }, bound.Position, 17, 1).Accepted, "equipment lock prevents deletion");
            string auditBefore = Query("SELECT count(*) FROM inventory_audit_log")!.ToString()!;
            Sql("CREATE TRIGGER fail_item BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'test item failure'); END");
            Reject(() => repo.CommitInventory(owner, current, service.Move(current, request).Plan!), "item failure rolls back whole mutation");
            Check(repo.List(owner)[0].Revision == current.Revision && repo.List(owner)[0].Inventory.Items.SequenceEqual(current.Inventory.Items) && Query("SELECT count(*) FROM inventory_audit_log")?.ToString() == auditBefore, "rollback restores revision, items and audit");
            Sql("DROP TRIGGER fail_item");
            Sql("CREATE TRIGGER fail_starter BEFORE INSERT ON character_inventory_items BEGIN SELECT RAISE(ABORT,'test starter failure'); END");
            Reject(() => new GameSession(repo, towns, owner).CreateCharacter("StarterRollback", characters.Get(0)), "starter failure rejects creation");
            Check(repo.List(owner).Length == 16 && Query("SELECT count(*) FROM character_inventory_items")?.ToString() == "81", "starter failure leaves no character, items or partial grant");
            Sql("DROP TRIGGER fail_starter");
            Sql("UPDATE character_inventory_items SET item_core=json_set(item_core,'$.Value',18) WHERE item_uid=" + locked.Uid);
            Reject(() => repo.CommitInventory(owner, current, service.Move(current, request).Plan!), "external core mutation without revision cannot be overwritten");
            current = repo.List(owner)[0];
            var saved = repo.Save(owner, current, current.Position with { X = 460 }, current.Return);
            Check(repo.List(owner)[0].Inventory.Items.SequenceEqual(saved.Inventory.Items), "position changes preserve the current inventory");
            using var next = owners.Acquire(owner.Account, () => { });
            Reject(() => repo.CommitInventory(owner, saved, service.Move(saved, request).Plan!), "old generation cannot mutate inventory");
            Check(new CharacterRepository(path).List(next)[0].Inventory.Items.SequenceEqual(saved.Inventory.Items), "reopen and new generation retain inventory");

            using var wire = new MemoryStream();
            using var itemWriter = new BinaryWriter(wire);
            ItemListProtocolWriter.Write(itemWriter, 65, detailed);
            var entry = wire.ToArray();
            Check(entry.Length == 123 && BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(2)) == stack.Id && BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(101)) == detailed.GuardianGemKeys, "independent 123B entry offsets");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(114)) == detailed.Extension114.Value && entry[118] == detailed.Extension114.Flag && entry[119] == 106 && entry[120] == 107, "raw extension field order");
            Check(entry[39] == 1 && BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(40)) == 11 &&
                entry[48] == 1 && entry[50] == 2 && entry[52] == 3 && entry[54] == 4,
                "chronicle byte arrays keep native field order");
            Check(entry[71] == 1 && entry[72] == 1 && entry[75] == 2 && entry[78] == 3,
                "random byte arrays keep native field order");
            var clear = InventoryPackets.Delta(InventorySnapshot.Empty, InventoryListType.Main, [new(InventoryListType.Main, 65)]);
            Check(clear.Length == 126 && BinaryPrimitives.ReadInt32LittleEndian(clear.AsSpan(5)) == -1, "delta clears an absent slot with -1");
            Check(InventoryRequests.Sort(Convert.FromHexString("080000000814100018012000")) == new InventorySortRequest(InventoryListType.Main, ItemKind.Equipment, 0), "native protobuf sort tags");
            var discard = InventoryRequests.Delete(Convert.FromHexString("10000000081210001A0808001041181420032000"));
            Check(discard.Items is [{ Slot: 65, Value: 20, Count: 3 }], "native protobuf discard tags");
            Reject(() => InventoryRequests.Sort([0, 0, 0, 0, 8]), "protobuf length mismatch rejects");
            Reject(() => InventoryRequests.Sort(Convert.FromHexString("0200000008FF")), "truncated varint rejects");
            Reject(() => InventoryRequests.Move(new byte[27]), "move truncated body rejects");
            var actor = TownPackets.Actor(saved);
            var roster = CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(saved)]);
            Check(actor[133 + CharacterName.Encoding.GetByteCount(saved.Name)] == 5 && roster[36 + CharacterName.Encoding.GetByteCount(saved.Name)] == 5, "actor and roster share worn appearance count");
            CheckPlayerItems(saved.Inventory, "five starter items follow nested native readers");
            var playerDetail = detailed with { ItemId = weapon.Core.ItemId, Kind = ItemKind.Equipment, Rune = 0x1234 };
            var detailedWorn = InventorySnapshot.Empty with { Items = [new InventoryItem(1, new(InventoryListType.Equipment, 12), playerDetail)] };
            CheckPlayerItems(detailedWorn, "player preserves rune and nonempty random option fields");
            CheckPlayerItems(detailedWorn with { Items = [detailedWorn.Items[0] with { Core = playerDetail with { RandomChangedIndex = byte.MaxValue } }] }, "random option sentinel skips changed option payload");
            Check((string?)Query("PRAGMA integrity_check") == "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check")?.ToString() == "0", "inventory integrity and foreign keys");

            void CheckPlayerItems(InventorySnapshot inventory, string message)
            {
                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream);
                EquipmentProtocolWriter.Player(writer, inventory);
                stream.Position = 0;
                using var reader = new BinaryReader(stream);
                var expected = inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment).OrderBy(item => item.Position.Index).ToArray();
                bool valid = reader.ReadByte() == expected.Length;
                foreach (var item in expected)
                {
                    var core = item.Core;
                    valid &= reader.ReadByte() == item.Position.Index && reader.ReadInt32() == core.ItemId && reader.ReadInt32() == core.Value;
                    stream.Position += 17; // 023C3BA1..023C3C97 的普通前缀共26B。
                    byte chronicles = reader.ReadByte();
                    stream.Position += chronicles * 8;
                    valid &= reader.ReadInt32() == core.ExpireTime;
                    byte emblems = reader.ReadByte();
                    stream.Position += emblems * 4;
                    valid &= reader.ReadUInt16() == core.Rune; // 024B2D60 是嵌套读包函数。
                    byte count = reader.ReadByte(); // 022913F0 不等同于只更新UI的helper。
                    var options = new[] { core.Random0, core.Random1, core.Random2 }.Where(option => option.Type != 0).ToArray();
                    valid &= count == options.Length;
                    foreach (var option in options)
                        valid &= reader.ReadByte() == option.Type && reader.ReadByte() == option.Value1 && reader.ReadByte() == option.Value2;
                    if (count != 0)
                    {
                        valid &= reader.ReadByte() == core.RandomState;
                        byte changed = reader.ReadByte();
                        valid &= changed == core.RandomChangedIndex;
                        if (changed != byte.MaxValue)
                            valid &= reader.ReadByte() == core.RandomChangeState && reader.ReadByte() == core.RandomChange.Type && reader.ReadByte() == core.RandomChange.Value1 && reader.ReadByte() == core.RandomChange.Value2;
                    }
                    stream.Position += 10; // 023C3F9C..023C404A。
                    foreach (int size in new[] { 4, 8, 4, 5, 4 })
                    {
                        uint length = reader.ReadUInt32();
                        if (length != size || length > stream.Length - stream.Position)
                            throw new InvalidDataException("Player扩展块偏移或长度与客户端不符");
                        stream.Position += length;
                        if (size == 8)
                            valid &= reader.ReadByte() == core.Extension109;
                    }
                }
                Check(valid && stream.Position == stream.Length, message);
            }

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);

        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine($"PASS {checks} inventory assertions; starter jobs=16");
    }
}
