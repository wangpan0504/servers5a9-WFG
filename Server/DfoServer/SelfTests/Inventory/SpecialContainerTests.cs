using DfoServer.Infrastructure;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using PvfLib;

namespace DfoServer.Testing.Inventory;

internal static class SpecialContainerTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int initial = Count;
        var catalog = itemResources.Items;
        var service = new InventoryService(catalog, itemResources.GoldLimits, itemResources.EpicPieces);
        var avatar = catalog.Get(101550507);
        var creature = catalog.Get(63000);
        var artifact = catalog.Get(2680000);
        var feed = catalog.Get(24);
        That(avatar.Kind == ItemKind.Avatar && avatar.EquipmentSlot == 0 && creature.Kind == ItemKind.Creature && creature.EquipmentSlot == 26 &&
            artifact.Kind == ItemKind.CreatureEquipment && artifact.EquipmentSlot == 28 && feed.Kind == ItemKind.CreatureConsumable,
            "matching PVF routes avatar, creature, artifact and feed without path heuristics");
        That(catalog.Get(25).Kind == ItemKind.CreatureConsumable, "stackable creature rename card is not a pet instance");
        var authoredSockets = new PvfDocument("[emblem socket default]\n`[A socket]` `[B socket]` `[C socket]` `[D socket]` `[M socket]`\n[/emblem socket default]");
        That(ItemCatalog.ReadAvatarSockets(authoredSockets) == new AvatarSocketTypes(1, 2, 4, 8, 239) &&
            ItemCatalog.ReadAvatarSockets(new PvfDocument("[avatar emblem socket num]\n0", authoredSockets)) == default,
            "PVF socket parsing handles five declarations and explicit zero overriding inheritance");
        var socketTemplate = avatar with { AvatarSockets = new(1, 2, 4, 8, 16) };
        var defaults = InventoryRewardGrantService.CreateItem(socketTemplate, new(InventoryListType.Avatar, 0), InventoryRewardGrantService.Create(socketTemplate));
        That(defaults.Avatar is { Socket0: { Type: 1, EmblemId: -1 }, Socket4: { Type: 16, EmblemId: -1 } }, "five PVF default sockets retain open-empty sentinel");
        string folder = Path.Combine(Path.GetTempPath(), "s5a9-special-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "game.db");
        try
        {
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("special-test", () => { });
            var current = repo.Create(owner, new("Special", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1 });
            Apply(service.Grant(current, [new(avatar.Id, 2), new(creature.Id, 2), new(artifact.Id, 1), new(feed.Id, 20)]));
            That(current.Inventory.Items.Count(item => item.Position.List == InventoryListType.Avatar) == 2 &&
                current.Inventory.Items.Count(item => item.Position.List == InventoryListType.Pet) == 4, "batch grant chooses separate container pages");
            That(current.Inventory.Items.All(item => item.Core.Count == (item.Core.Kind == ItemKind.CreatureConsumable ? 20 : 1)), "details are single instances, feed is stackable");
            That(current.Inventory.Items.Where(item => item.Avatar is not null || item.Creature is not null).All(item => item.Uid == item.Core.Value && item.Core.ExpireTime == 0),
                "one transaction allocates linked detail identity and keeps expiry in details");
            That(Query("SELECT count(*) FROM inventory_avatar_details") is 2L && Query("SELECT count(*) FROM inventory_creature_details") is 2L, "both detail tables populated");
            var shop = new InventoryShopService(catalog, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces);
            That(shop.Sell(current, new(new(InventoryListType.Avatar, 0), 1, 0, 0, 0)) is { Accepted: false, Plan: null },
                "时装背包拒绝 NPC 出售，不生成删除或金币计划");
            That(shop.Sell(current, new(new(InventoryListType.Pet, 0), 1, 0, 0, 0)).Accepted,
                "宠物仍通过统一删除计划出售");
            var a = current.Inventory.Find(new(InventoryListType.Avatar, 0))!;
            var p = current.Inventory.Find(new(InventoryListType.Pet, 0))!;
            That(InventoryMoveService.Allows(current with { Level = 0, Job = 15, Skills = [] }, new(InventoryListType.Equipment, 28),
                current.Inventory.Find(new(InventoryListType.Pet, 140))!.Core, catalog), "pet artifacts do not inherit character-level or job restrictions");
            (new InventorySnapshot([p with { Core = p.Core with { SealFlag = 2 } }])).Validate();
            That(!service.Lock(current with { Inventory = new([p with { Core = p.Core with { SealFlag = 2 } }]) }, p.Position,
                InventoryLockOperation.EquipmentLock).Accepted, "pet type field is not boolean, while nonzero pet types retain their security-lock rule");
            var aDetail = a.Avatar! with { Color1 = 0x1234, Color2 = 0x5678, Socket0 = new(1, 3001), Socket4 = new(239, -1) };
            var pDetail = p.Creature! with { Name = "小龙", Mode = 1, Mode1Field0 = 7, Mode1Field1 = 8, Experience = 1234, Level = 9, Satiety = 77, TailFlag = 2 };
            var rich = current.Inventory with { Items = current.Inventory.Items.Replace(a, a with { Avatar = aDetail }).Replace(p, p with { Creature = pDetail }) };
            current = repo.CommitInventory(owner, current, new(current.Inventory, rich, "special_detail_fixture"));
            That(repo.List(owner).Single().Inventory.Items.SequenceEqual(current.Inventory.Items), "non-default detail-only change participates in revision and persistence");
            var bought = ShopPackets.Bought(new(true, 0), new(creature.Id, 1, p.Position, InventoryRewardGrantService.Create(creature),
                InventorySnapshot.Empty, []), current.Inventory);
            That(bought.Length == 141 && TcpPeer.U32(bought, 23) == p.Uid, "shop receipt reads allocated pet UID from committed snapshot");
            foreach (var pair in new[] { (new InventorySlot(InventoryListType.Avatar, 0), (short)0), (new InventorySlot(InventoryListType.Pet, 0), (short)26), (new InventorySlot(InventoryListType.Pet, 140), (short)28) })
            {
                var source = current.Inventory.Find(pair.Item1)!;
                var worn = new InventorySlot(InventoryListType.Equipment, pair.Item2);
                var stale = current;
                var equip = Move(source.Position, worn);
                Apply(equip);
                That(current.Inventory.Find(worn)?.Uid == source.Uid && current.Inventory.Find(worn)?.Avatar == source.Avatar &&
                    current.Inventory.Find(worn)?.Creature == source.Creature, "equip preserves complete linked detail");
                var sale = shop.Sell(current, new(worn, 1, 0, 0, 0));
                That(source.Core.Kind == ItemKind.Avatar ? sale is { Accepted: false, Plan: null } : sale.Accepted,
                    "穿戴栏按物品类别拒绝时装出售，保留宠物与宠物装备出售");
                Reject(() => repo.CommitInventory(owner, stale, equip.Plan!), "stale character cannot overwrite special items");
                var wrong = InventoryMoveService.Plan(current, new(worn, source.Core.ItemId, 1, new(InventoryListType.Main, 3), 0, 0, -1, 0, 0), catalog, long.MinValue);
                That(!wrong.Accepted, "dedicated resource categories cannot enter Main quickbar");
                Apply(Move(worn, source.Position));
                var expected = source.Core.Kind == ItemKind.CreatureEquipment && catalog.Get(source.Core.ItemId).AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase)
                    ? source with { Core = source.Core with { SealFlag = 0 } } : source;
                That(current.Inventory.Find(source.Position) == expected, "unequip preserves instance, detail and committed artifact unsealing");
            }
            // 同模板实例交换也不能混淆详情；模板编号相同不能当作幂等身份。
            Apply(Move(new(InventoryListType.Avatar, 0), new(InventoryListType.Avatar, 1)));
            That(current.Inventory.Find(new(InventoryListType.Avatar, 1))?.Avatar == aDetail, "same-template avatar swap carries the right detail");
            Apply(Move(new(InventoryListType.Pet, 0), new(InventoryListType.Pet, 1)));
            That(current.Inventory.Find(new(InventoryListType.Pet, 1))?.Creature == pDetail, "same-template pet swap carries name and progress");
            var food = current.Inventory.Find(new(InventoryListType.Pet, 189))!;
            Apply(service.Move(current, new(food.Position, feed.Id, 7, new(InventoryListType.Pet, 190), 0, 0, -1, 0, 0)));
            That(current.Inventory.Find(new(InventoryListType.Pet, 190)) is { Core.Value: 7, Avatar: null, Creature: null }, "pet consumable split allocates no pet detail");
            Apply(Move(new(InventoryListType.Pet, 190), food.Position, 0));
            That(current.Inventory.Find(food.Position)?.Core.Value == 20, "pet consumable merge preserves stack count");
            Apply(service.Lock(current, new(InventoryListType.Avatar, 1), InventoryLockOperation.SortLock));
            Apply(service.Sort(current, new(InventoryListType.Avatar, ItemKind.Avatar, 3)));
            That(current.Inventory.Find(new(InventoryListType.Avatar, 1))?.Avatar == aDetail, "avatar sort respects fixed slots and detail identity");
            Apply(service.Lock(current, new(InventoryListType.Pet, 1), InventoryLockOperation.EquipmentLock));
            That(!InventoryDeleteService.Plan(current.Inventory, new(InventoryListType.Pet, 1), (int)p.Uid, 1).Accepted, "pet security lock prevents deletion");
            Apply(service.Lock(current, new(InventoryListType.Pet, 1), InventoryLockOperation.EquipmentUnlock));

            var full = current.Inventory with { Items = Enumerable.Range(0, 210).Select(index => a with { Uid = index + 1,
                Position = new(InventoryListType.Avatar, (short)index), Core = a.Core with { Value = index + 1 } }).ToImmutableArray() };
            That(!service.Grant(current with { Inventory = full }, [new(feed.Id, 1), new(avatar.Id, 1)]).Accepted,
                "later full avatar container rejects whole mixed reward batch");
            foreach (var boundary in new[] { (ItemKind.Avatar, 0, 209), (ItemKind.Creature, 0, 139), (ItemKind.CreatureEquipment, 140, 188), (ItemKind.CreatureConsumable, 189, 239) })
            {
                var list = ItemSlotBoundService.List(boundary.Item1);
                That(ItemSlotBoundService.Allows(current.Inventory, new(list, (short)boundary.Item2), boundary.Item1) &&
                    ItemSlotBoundService.Allows(current.Inventory, new(list, (short)boundary.Item3), boundary.Item1) &&
                    !ItemSlotBoundService.Allows(current.Inventory, new(list, (short)(boundary.Item3 + 1)), boundary.Item1), "container first and last slot boundaries");
            }

            string beforeFailure = Snapshot();
            Query("CREATE TRIGGER fail_detail BEFORE INSERT ON inventory_creature_details BEGIN SELECT RAISE(ABORT,'detail failure'); END");
            Reject(() => repo.CommitInventory(owner, current, service.Grant(current, [new(avatar.Id, 1), new(creature.Id, 1)]).Plan!), "late detail write failure rolls back mixed grant");
            That(Snapshot() == beforeFailure, "failed detail write leaves no core, slot, UID allocation, revision or audit");
            Query("DROP TRIGGER fail_detail");
            Query("CREATE TRIGGER fail_detail_audit BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'audit failure'); END");
            Reject(() => repo.CommitInventory(owner, current, InventoryDeleteService.Plan(current.Inventory, new(InventoryListType.Pet, 1), (int)p.Uid, 1).Plan!), "late audit failure rolls back cascade delete");
            That(Snapshot() == beforeFailure, "rollback restores pet detail and position together");
            Query("DROP TRIGGER fail_detail_audit");
            Apply(InventoryDeleteService.Plan(current.Inventory, new(InventoryListType.Pet, 1), (int)p.Uid, 1));
            That(Query("SELECT count(*) FROM inventory_creature_details") is 1L, "pet deletion cascades exactly one detail");
            Apply(InventoryDeleteService.Plan(current.Inventory, new(InventoryListType.Avatar, 1), 0, 1));
            That(Query("SELECT count(*) FROM inventory_avatar_details") is 1L, "avatar deletion uses countdown value, not server detail UID");

            var expiring = current.Inventory.Items.First(item => item.Avatar is not null);
            rich = current.Inventory with { Items = current.Inventory.Items.Replace(expiring, expiring with { Avatar = expiring.Avatar! with { ExpireTime = 123 } }) };
            current = repo.CommitInventory(owner, current, new(current.Inventory, rich, "expiry_fixture"));
            Apply(InventoryExpiryService.Plan(current.Inventory, 124));
            That(Query("SELECT count(*) FROM inventory_avatar_details") is 0L && !current.Inventory.Items.Any(item => item.Uid == expiring.Uid), "detail expiration removes core and slots");
            That(new CharacterRepository(path).List(owner).Single().Inventory.Items.SequenceEqual(current.Inventory.Items), "repository reopen preserves remaining special container state");
            That(Query("PRAGMA integrity_check") is "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L && Convert.ToInt64(Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion,
                "schema12 integrity and foreign keys");
            using (var other = owners.Acquire("other-special-owner", () => { }))
                Reject(() => repo.CommitInventory(other, current, service.Grant(current, [new(avatar.Id, 1)]).Plan!), "other owner cannot alter character details");
            Wire(a with { Avatar = aDetail }, p with { Creature = pDetail }, characters.Get(0).Equipment.First().Template);
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => repo.CommitInventory(owner, current, service.Grant(current, [new(avatar.Id, 1)]).Plan!), "old generation cannot allocate special details");
            That(repo.List(replacement).Single().Inventory.Items.SequenceEqual(current.Inventory.Items), "replacement owner sees only committed special assets");

            void Apply(InventoryResult result)
            {
                That(result.Accepted, "container operation accepted");
                current = InventoryService.Commit(repo, owner, current, result);
            }
            InventoryResult Move(InventorySlot from, InventorySlot to, int count = 1) => service.Move(current,
                new(from, current.Inventory.Find(from)?.Core.ItemId ?? 0, count, to, current.Inventory.Find(to)?.Core.ItemId ?? 0, 0, -1, 0, 0));
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path);
                db.Open();
                using var cmd = db.CreateCommand(); cmd.CommandText = sql;
                return cmd.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + ":" + Query("SELECT group_concat(name||':'||seq,';') FROM sqlite_sequence");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
        Console.WriteLine($"PASS special container domain: {Count - initial} assertions");
    }

    static void Wire(InventoryItem avatar, InventoryItem creature, ItemTemplate ordinary)
    {
        const long now = 1000;
        avatar = avatar with { Avatar = avatar.Avatar! with { ExpireTime = 1600 } };
        creature = creature with { Creature = creature.Creature! with { ExpireTime = 1900 } };
        var inventory = new InventorySnapshot([avatar, creature]);
        using (var reader = new BinaryReader(new MemoryStream(InventoryPackets.SpecialFull(inventory, InventoryListType.Avatar, now))))
        {
            That(reader.ReadByte() == 1 && reader.ReadUInt16() == 105 && reader.ReadUInt16() == 1, "avatar header extension and count");
            reader.ReadInt16(); reader.ReadInt32();
            That(reader.ReadInt32() == 600, "avatar wire value is remaining duration");
            reader.ReadBytes(113);
            Sockets(reader);
            That(reader.ReadInt32() == 4 && reader.ReadUInt16() == 0x1234 && reader.ReadUInt16() == 0x5678 && reader.BaseStream.Position == reader.BaseStream.Length,
                "avatar color block follows all five sockets without trailing data");
        }
        var clear = InventoryPackets.Delta(InventorySnapshot.Empty, InventoryListType.Avatar, [avatar.Position], now);
        That(clear.Length == 168 && TcpPeer.U32(clear, 5) == uint.MaxValue && TcpPeer.U32(clear, 126) == 30 && TcpPeer.U32(clear, 160) == 4,
            "clearing avatar slot retains both mandatory RAW blocks");
        var pets = InventoryPackets.SpecialFull(inventory, InventoryListType.Pet, now);
        That(pets.Length == 126 && TcpPeer.U32(pets, 9) == creature.Uid && TcpPeer.U32(pets, 25) == 900, "pet core sends UID and remaining duration separately");
        using (var reader = new BinaryReader(new MemoryStream(InventoryPackets.Creatures(inventory))))
        {
            That(reader.ReadByte() == 1 && reader.ReadInt32() == creature.Uid && reader.ReadByte() == 77 && reader.ReadByte() == 1 && reader.ReadInt32() == 1234,
                "NOTI105 pet identity, satiety, mode and experience");
            That(reader.ReadByte() == 7 && reader.ReadByte() == 8 && reader.ReadByte() == 9 && reader.ReadInt32() == 4 &&
                CharacterName.Encoding.GetString(reader.ReadBytes(4)) == "小龙" && reader.ReadByte() == 2 && reader.BaseStream.Position == reader.BaseStream.Length,
                "NOTI105 mode1 extension and CP936 length-prefixed name");
        }
        inventory = new([avatar with { Position = new(InventoryListType.Equipment, 0) }, creature with { Position = new(InventoryListType.Equipment, 26) },
            new(900, new(InventoryListType.Equipment, 12), InventoryRewardGrantService.Create(ordinary))]);
        var player = TcpPeer.Build(writer => EquipmentProtocolWriter.Player(writer, inventory, now));
        using (var reader = new BinaryReader(new MemoryStream(player)))
        {
            That(reader.ReadByte() == 3 && reader.ReadByte() == 0, "mixed Player count and first avatar slot");
            reader.ReadBytes(25);
            Sockets(reader); reader.ReadBytes(8); reader.ReadBytes(62);
            That(reader.ReadByte() == 12, "following ordinary Player starts at exact offset after avatar");
            reader.ReadBytes(87);
            That(reader.ReadByte() == 26, "pet Player follows unchanged ordinary entry");
            reader.ReadBytes(25);
            That(reader.ReadInt32() == 900, "pet Player consumes extra remaining time");
            reader.ReadBytes(62);
            That(reader.BaseStream.Position == player.Length, "mixed Player parser consumes complete payload");
        }
        var appearance = TcpPeer.Build(writer => EquipmentProtocolWriter.Appearance(writer, inventory));
        That(appearance.Length == 76 && TcpPeer.U16(appearance, 10) == 0x1234 && appearance[26] == 12 && appearance[51] == 26,
            "roster and town appearance retain avatar colors and fixed ordinary boundaries");
        Reject(() => (creature.Creature! with { Name = new string('龙', 16) }).Validate(), "pet name limit is bytes, not characters");

        static void Sockets(BinaryReader reader)
        {
            That(reader.ReadInt32() == 30 && reader.ReadUInt16() == 1 && reader.ReadInt32() == 3001, "avatar RAW first socket");
            reader.ReadBytes(18);
            That(reader.ReadUInt16() == 239 && reader.ReadInt32() == -1, "avatar RAW fifth socket preserves sentinel");
        }
    }

    static void Reject(Action action, string name)
    {
        bool failed = false;
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or SqliteException) { failed = true; }
        That(failed, name);
    }
}
