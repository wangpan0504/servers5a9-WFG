using DfoServer.Infrastructure;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using PvfLib;

namespace DfoServer.Testing.Inventory;

internal static class GuildMedalTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        var catalog = itemResources.Items;
        var service = new InventoryService(catalog, itemResources.GoldLimits, itemResources.EpicPieces);
        var medal = catalog.Get(100380044);
        var gem = catalog.Get(90000);
        That(medal.Kind == ItemKind.GuildMedal && medal.EquipmentSlot == 32 && gem.Kind == ItemKind.GuardianGem && gem.StackLimit == 1000,
            "matching PVF classifies flag and flag gem with dedicated Client equipment slot");
        That(gem.GuardianGemEffects.SetEquals(catalog.Get(90001).GuardianGemEffects) &&
            !gem.GuardianGemEffects.Overlaps(catalog.Get(90032).GuardianGemEffects), "gem effect identity comes from PVF, independent of rarity");
        var multiple = ItemCatalog.ReadGuardianGemEffects(new PvfDocument("[enchant]\n[move speed] 24\n[cast speed]\n36\n[/enchant]"));
        That(multiple.SetEquals(["move speed", "cast speed"]), "inline and multiline PVF enchant effect tags");
        foreach (var (kind, first, last) in new[] { (ItemKind.GuildMedal, (short)0, (short)48), (ItemKind.GuardianGem, (short)49, (short)97) })
        {
            That(ItemSlotBoundService.List(kind) == InventoryListType.GuildMedal && ItemSlotBoundService.Range(kind, 0) == (first, last), "fixed special pages");
            That(ItemSlotBoundService.Allows(24, new(InventoryListType.GuildMedal, first), kind) &&
                ItemSlotBoundService.Allows(0, new(InventoryListType.GuildMedal, last), kind) &&
                !ItemSlotBoundService.Allows(24, new(InventoryListType.GuildMedal, (short)(first - 1)), kind) &&
                !ItemSlotBoundService.Allows(24, new(InventoryListType.GuildMedal, (short)(last + 1)), kind), "page boundary rejects cross-kind slots");
            That(ItemSlotBoundService.Allows(0, new(InventoryListType.Main, 3), kind), "special item kinds retain quick-slot access");
        }
        That(InventoryGuardianGemService.ItemId(1UL | 33UL << 16 | 43UL << 48, 0) == 90000 &&
            InventoryGuardianGemService.ItemId(1UL | 33UL << 16 | 43UL << 48, 1) == 90032 &&
            InventoryGuardianGemService.ItemId(1UL | 33UL << 16 | 43UL << 48, 2) == 0 &&
            InventoryGuardianGemService.ItemId(1UL | 33UL << 16 | 43UL << 48, 3) == 90042, "four independent short keys and zero sentinel");
        Bytes(GuardianGemPackets.Used(new(true, 0)), [1], "gem success ACK has no payload");
        Bytes(GuardianGemPackets.Used(InventoryResult.Reject(0x12)), [0, 0x12, 0], "Client gem failure uses common u16 error");
        var parsed = GuardianGemRequests.Use(Convert.FromHexString("8CADFB053100BA5F010000"));
        That(parsed == new GuardianGemUseRequest(medal.Id, 49, 90042, 0), "11B sender layout does not expose DB identity");
        Reject(() => GuardianGemRequests.Use(new byte[10]), "truncated gem request");
        Reject(() => GuardianGemRequests.Use(new byte[12]), "extra gem request byte");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-medals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("medals", () => { });
            var current = repo.Create(owner, new("Medals", towns.Room, towns.InitialReturn)
            {
                Level = 95, Skills = characters.Get(0).Skills, CompletedQuests = [3145], AcceptedStoryLevel = 1
            });
            var fullMedals = service.Grant(current, [new(medal.Id, 49)]);
            That(fullMedals.Accepted && fullMedals.Plan!.After.Items.Length == 49 &&
                fullMedals.Plan.After.Items.All(item => item.Core.Value == 1 && item.Core.Count == 1) &&
                !service.Grant(current with { Inventory = fullMedals.Plan.After }, [new(medal.Id, 1)]).Accepted,
                "medals use count value1 and capacity is exactly49 without merging single instances");
            var fullGems = service.Grant(current, [new(gem.Id, gem.StackLimit)]);
            That(fullGems.Accepted && fullGems.Plan!.After.Items.Length == 1 &&
                !service.Grant(current with { Inventory = fullGems.Plan.After }, [new(gem.Id, 1)]).Accepted &&
                !service.Grant(current, [new(gem.Id, gem.StackLimit + 1)]).Accepted &&
                !service.Grant(current, [new(medal.Id, 50), new(gem.Id, 1)]).Accepted,
                "gem capacity follows PVF stack limits and overflow rejects entire reward batch");
            Apply(service.Grant(current, [new(medal.Id, 2), new(90000, 4), new(90001, 1), new(90032, 3), new(90042, 2)]));
            var original = Find(InventoryListType.GuildMedal, 0);
            original = original with { Core = original.Core with { Attr = 5, Rune = 42, Marker16 = 77, EnchantCardId = 123, Extension114 = new(2, 345) } };
            current = repo.CommitInventory(owner, current, new(current.Inventory,
                current.Inventory with { Items = current.Inventory.Items.Replace(Find(InventoryListType.GuildMedal, 0), original) }, "medal_fixture"));
            That(original.Core.Count == 1 && !original.Core.IsStackable, "medals are single full-core instances");
            Move(InventoryListType.GuildMedal, 0, InventoryListType.Main, 3);
            That(Find(InventoryListType.Main, 3).Uid == original.Uid, "medal quick-slot move preserves UID");
            Move(InventoryListType.Main, 3, InventoryListType.GuildMedal, 0);
            Move(InventoryListType.GuildMedal, 49, InventoryListType.Main, 4);
            Move(InventoryListType.Main, 4, InventoryListType.GuildMedal, 49);
            Move(InventoryListType.GuildMedal, 49, InventoryListType.PersonalCargo, 0);
            Move(InventoryListType.PersonalCargo, 0, InventoryListType.GuildMedal, 49);
            That(!service.Move(current, Request(InventoryListType.GuildMedal, 0, InventoryListType.PersonalCargo, 1)).Accepted,
                "medal personal cargo rejection comes from actual impossible contents");
            var split = Request(InventoryListType.GuildMedal, 49, InventoryListType.GuildMedal, 97) with { Count = 2 };
            Apply(service.Move(current, split));
            That(Find(InventoryListType.GuildMedal, 97).Core.Count == 2 && Find(InventoryListType.GuildMedal, 49).Core.Count == 2,
                "gem split uses normal instance allocation");
            Apply(service.Move(current, Request(InventoryListType.GuildMedal, 97, InventoryListType.GuildMedal, 49) with { Count = 2 }));
            That(Find(InventoryListType.GuildMedal, 49).Core.Count == 4, "gem merge preserves full count");
            That(!service.Move(current with { Level = 49 }, Request(InventoryListType.GuildMedal, 0, InventoryListType.Equipment, 32)).Accepted,
                "medal equip observes its PVF minimum level");
            Move(InventoryListType.GuildMedal, 0, InventoryListType.Equipment, 32);
            That(Find(InventoryListType.Equipment, 32) == original with { Position = new(InventoryListType.Equipment, 32) },
                "wear preserves UID and every core extension");
            Apply(service.UseGuardianGem(current, new(medal.Id, 49, 90000, 0)));
            That(Find(InventoryListType.Equipment, 32).Core.GuardianGemKeys == 1 && Find(InventoryListType.GuildMedal, 49).Core.Count == 3,
                "gem insertion and material consumption commit together");
            string before = Snapshot();
            That(service.UseGuardianGem(current, new(medal.Id, 50, 90001, 1)).Error == 0x12 && Snapshot() == before,
                "same effect in another socket rejected regardless of rarity");
            Apply(service.UseGuardianGem(current, new(medal.Id, 50, 90001, 0)));
            That(Find(InventoryListType.Equipment, 32).Core.GuardianGemKeys == 2 && current.Inventory.Find(new(InventoryListType.GuildMedal, 50)) is null &&
                Find(InventoryListType.GuildMedal, 49).Core.Count == 3, "overwrite destroys old gem without returning or duplicating it");
            Apply(service.UseGuardianGem(current, new(medal.Id, 51, 90032, 1)));
            Apply(service.UseGuardianGem(current, new(medal.Id, 52, 90042, 3)));
            var worn = Find(InventoryListType.Equipment, 32);
            That(worn.Uid == original.Uid && (worn.Core with { GuardianGemKeys = 0 }) == original.Core,
                "embedding changes only gem keys on target, preserving upgrades and other fields");
            Move(InventoryListType.GuildMedal, 1, InventoryListType.Equipment, 32);
            That(Find(InventoryListType.GuildMedal, 1).Uid == original.Uid && Find(InventoryListType.GuildMedal, 1).Core == worn.Core,
                "occupied equipment swap retains embedded gems on the returned medal");
            Move(InventoryListType.GuildMedal, 1, InventoryListType.Equipment, 32);
            foreach (var bad in new[] { new GuardianGemUseRequest(medal.Id, 52, 90042, 4), new(medal.Id, 48, 90042, 2),
                new(medal.Id + 1, 52, 90042, 2), new(medal.Id, 52, 90032, 2), new(medal.Id, 52, 90042, 1) })
                That(!service.UseGuardianGem(current, bad).Accepted, "invalid, stale or duplicate-effect gem request rejected");
            Apply(service.Lock(current, worn.Position, InventoryLockOperation.EquipmentLock));
            That(!service.UseGuardianGem(current, new(medal.Id, 52, 90042, 3)).Accepted, "security lock protects embedded target");
            Apply(service.Lock(current, worn.Position, InventoryLockOperation.EquipmentUnlock));
            var expiring = current.Inventory with { Items = current.Inventory.Items.Replace(worn, worn with { Core = worn.Core with { ExpireTime = 1 } }) };
            That(!InventoryGuardianGemService.Plan(expiring, new(medal.Id, 52, 90042, 3), catalog, 2).Accepted, "expired target cannot be modified");
            var expiringGem = Find(InventoryListType.GuildMedal, 52);
            expiring = current.Inventory with { Items = current.Inventory.Items.Replace(expiringGem, expiringGem with { Core = expiringGem.Core with { ExpireTime = 1 } }) };
            That(!InventoryGuardianGemService.Plan(expiring, new(medal.Id, 52, 90042, 3), catalog, 2).Accepted, "expired material is not consumed");
            var oldPlan = service.UseGuardianGem(current, new(medal.Id, 52, 90042, 3));
            before = Snapshot();
            Sql("CREATE TRIGGER fail_gem BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'test gem rollback'); END");
            Reject(() => InventoryService.Commit(repo, owner, current, oldPlan), "late SQL fault during gem transaction");
            That(Snapshot() == before, "late failure rolls back core, material count, audit and revision");
            Sql("DROP TRIGGER fail_gem");
            Apply(service.Lock(current, new(InventoryListType.GuildMedal, 51), InventoryLockOperation.SortLock));
            Reject(() => InventoryService.Commit(repo, owner, current, oldPlan), "stale revision cannot replay precomputed gem result");
            Apply(service.Sort(current, new(InventoryListType.GuildMedal, ItemKind.GuardianGem, 0)));
            That(Find(InventoryListType.GuildMedal, 51).Core.ItemId == 90032, "sorting respects page and sort lock");
            var sale = new InventoryShopService(catalog, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces).Sell(current, new(new(InventoryListType.GuildMedal, 1), 1, 0, 0, 0));
            That(sale.Accepted, "medal sale uses ordinary pricing and deletion plan");
            var full = InventoryPackets.SpecialFull(current.Inventory, InventoryListType.GuildMedal);
            That(full[0] == 38 && full.Length == 3 + 123 * TcpPeer.U16(full, 1), "special full contains only list and count prefix");
            Move(InventoryListType.Equipment, 32, InventoryListType.GuildMedal, 48);
            Reject(() => Sql("UPDATE character_inventory_slots SET slot_index=98 WHERE list_type=38 AND slot_index=48"),
                "baseline enforces medal container upper bound");
            var restored = new CharacterRepository(path).List(owner).Single();
            That(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(current), "reopen restores complete medallion, gems and unrelated progress");
            var stalePlan = service.Grant(current, [new(90000, 1)]);
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => InventoryService.Commit(repo, owner, current, stalePlan), "replaced generation cannot write special container");
            That(Query("PRAGMA integrity_check") is "ok" && Query("PRAGMA foreign_key_check") is null, "medal fixture database integrity");

            void Apply(InventoryResult result)
            {
                That(result.Accepted, "medal operation accepted");
                current = InventoryService.Commit(repo, owner, current, result);
            }
            InventoryItem Find(InventoryListType list, short slot) => current.Inventory.Find(new(list, slot))!;
            InventoryMoveRequest Request(InventoryListType source, short from, InventoryListType destination, short to) =>
                new(new(source, from), current.Inventory.Find(new(source, from))?.Core.ItemId ?? 0, 0,
                    new(destination, to), current.Inventory.Find(new(destination, to))?.Core.ItemId ?? 0, 0, -1, 0, 0);
            void Move(InventoryListType source, short from, InventoryListType destination, short to) => Apply(service.Move(current, Request(source, from, destination, to)));
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner).Single()) + Query("SELECT count(*) FROM inventory_audit_log");
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);
        }
        finally { Directory.Delete(root, true); }
    }

    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or SqliteException) { rejected = true; }
        That(rejected, message);
    }
}
