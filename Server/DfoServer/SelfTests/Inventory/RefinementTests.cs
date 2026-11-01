using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
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
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class RefinementTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int start = Count;
        var catalog = itemResources.Items;
        var weapon = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template;
        var table = itemResources.Refinement;
        That(weapon.EquipmentSlot == 12 && table.MaximumLevel == 8, "matching weapon and PVF refinement cap");
        That(table.TryCost(weapon with { Grade = 85, Rarity = 4 }, 0, out var quote) && quote == new RefinementCost(3326, 60, 10000),
            "grade and material rarity weight, not the stat rarity weight");
        That(table.TryCost(weapon with { Grade = 85, Rarity = 4 }, 1, out quote) && quote == new RefinementCost(3326, 66, 5000),
            "next-level material multiplier truncates once");
        That(table.TryCost(weapon with { Grade = 85, Rarity = 4 }, 7, out quote) && quote == new RefinementCost(3326, 104, 750),
            "last ordinary level uses its actual PVF chance and cost");
        That(table.TryCost(weapon with { Grade = 1, Rarity = 0 }, 0, out quote) && quote.Count == 1, "positive material cost has minimum one");
        That(!table.TryCost(weapon, 8, out _) && !table.TryCost(weapon with { Grade = -1 }, 0, out _) &&
            !table.TryCost(weapon with { Rarity = 7 }, 0, out _), "unsupported grade, rarity and capped level have no guessed cost");
        const string sample = "[separate upgrade max]\n1\n[table]\n2 1 10000 1\n[/table]\n" +
            "[item weights by grade]\n1 3326 1\n[/item weights by grade]\n[item weights by rarity]\n1\n[/item weights by rarity]";
        foreach (string malformed in new[] { sample.Replace("10000", "10001"), sample.Replace("10000", "NaN"),
            sample.Replace("2 1 10000 1", "2 1 10000"), sample.Replace("1 3326 1", "1 3326 1 1 3326 2"),
            sample.Replace("1 3326 1", "1.5 3326 1"), sample.Replace("2 1 10000 1", "2 1 10000 0") })
            Reject(() => new EquipmentRefinementCatalog(malformed), "malformed table rejected instead of defaulting");
        var overflow = new EquipmentRefinementCatalog(sample.Replace("1 3326 1", "1 3326 2147483647").Replace("2 1 10000 1", "2 1 10000 2"));
        That(!overflow.TryCost(weapon with { Grade = 1, Rarity = 0 }, 0, out _), "overflow cannot produce a negative or free cost");

        byte[] body = [0, 3, 0, 128, 62, 0, 0, 4, 0, 1, 0, 0, 0, 65, 0];
        var request = RefinementRequests.Read(body);
        That(request == new RefinementRequest(Bag(3), 16000, 4, 0), "wire keeps template id distinct from quality value");
        foreach (byte flag in new byte[] { 1, 2 })
        {
            var confirm = body.ToArray();
            confirm[^1] = flag;
            That(RefinementRequests.Read(confirm).Confirmation == flag, "confirmation variants retain the same complete request");
        }
        foreach (byte[] malformed in new[] { body[..^1], body.Concat(new byte[1]).ToArray(), new byte[15] })
            Reject(() => RefinementRequests.Read(malformed), "truncated, trailing or empty-name request rejected");
        foreach (int size in new[] { -1, int.MaxValue })
        {
            var malformed = body.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(9), size);
            Reject(() => RefinementRequests.Read(malformed), "name length checked without arithmetic overflow");
        }
        request = request with { ItemId = weapon.Id };

        var now = DateTimeOffset.UtcNow;
        var core = InventoryRewardGrantService.Create(weapon, utcNow: now) with
        {
            Value = 1234567, Attr = 0xA7, EnchantCardId = 3175, EnchantUpgrade = 2, SealFlag = 1,
            AmplifyType = 2, AmplifyValue = 21, SortLockFlag = 1, Rune = 3, ExpireTime = (int)now.ToUnixTimeSeconds() + 3600,
            Random0 = new(1, 2, 3), EmancipateLevel = 1, TradeRestriction = 2, Extension27 = 1234
        };
        That(table.TryCost(weapon, 0, out var firstCost), "real weapon has ordinary refinement rule");
        var material = InventoryRewardGrantService.Create(catalog.Get(firstCost.ItemId), firstCost.Count + 100, now) with { SortLockFlag = 1 };
        var random = new FixedRandom(9999);
        var service = new InventoryRefinementService(catalog, itemResources.Refinement, random);
        Character Role(InventorySlot slot, ItemCore? target = null, ItemCore? source = null) => new(1, 0, "Refine", 0, towns.Room, towns.InitialReturn)
        {
            Job = 15,
            Inventory = new([new(100, slot, target ?? core), new(101, Bag(4), source ?? material), new(102, Bag(5), material)])
        };
        foreach (var slot in new[] { Bag(3), Bag(9), Bag(41), Worn(12), Worn(24) })
        {
            var role = Role(slot);
            var result = service.Refine(role, request with { Position = slot, Confirmation = 2 }, out var receipt, now);
            var updated = result.Plan!.After;
            That(result.Accepted && receipt is { Succeeded: true, OldLevel: 0, NewLevel: 1 } && receipt.MaterialRemaining == 100,
                "first refinement guaranteed, including quickbar and worn weapons with no added job, level, seal or sort-lock restriction");
            That(updated.Find(slot) == role.Inventory.Find(slot)! with { Core = core with { GenuineUpgrade = 1 } } &&
                updated.Find(Bag(4)) == role.Inventory.Find(Bag(4))! with { Core = material with { Value = 100 } } &&
                updated.Find(Bag(5)) == role.Inventory.Find(Bag(5)) && updated.Gold == role.Inventory.Gold,
                "only chosen material count and target refinement change; all UIDs and other fields survive");
            var ack = RefinementPackets.Ack(result, receipt);
            That(ack.Length == 13 && ack[0] == 1 && ack[1] == 4 && BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(3)) == 100 &&
                ack[7] == 0 && ack[8] == 0 && ack[9] == 1 && ack[10] == (byte)slot.List && BinaryPrimitives.ReadInt16LittleEndian(ack.AsSpan(11)) == slot.Index,
                "success ACK preserves exact material slot, remaining count, levels and target position");
        }
        foreach (int roll in new[] { 4999, 5000, 9999 })
        {
            random.Value = roll;
            var role = Role(Bag(3), core with { GenuineUpgrade = 1 });
            var result = service.Refine(role, request, out var receipt, now);
            That(result.Accepted && receipt!.Succeeded == (roll < 5000) && receipt.NewLevel == (roll < 5000 ? 2 : 1),
                "5000 is the exclusive success boundary; failure keeps level instead of breaking or downgrading");
            table.TryCost(weapon, 1, out quote);
            That(result.Plan!.After.Find(Bag(4))!.Core.Count == material.Count - quote.Count &&
                result.Plan.After.Items.Length == role.Inventory.Items.Length && result.Plan.After.Gold == 0,
                "both outcomes consume the same chosen material without any gold debit");
            That(RefinementPackets.Ack(result, receipt)[8] == (roll < 5000 ? 0 : 1), "failed attempt remains an accepted ACK with result byte one");
        }
        int calls = random.Calls;
        void Denied(Character role, RefinementRequest value, ushort error)
        {
            var result = service.Refine(role, value, out var receipt, now);
            That(!result.Accepted && result.Error == error && result.Plan is null && receipt is null && random.Calls == calls,
                "rejected request changes no snapshot and consumes no random draw");
            Bytes(RefinementPackets.Ack(result, receipt), [0, (byte)error, 0], "failure error is u16");
        }
        Denied(Role(Bag(3)), request with { ItemId = core.Value }, 4);
        Denied(Role(Bag(3)), request with { Position = Bag(6) }, 4);
        Denied(Role(Bag(3)), request with { MaterialSlot = 3 }, 4);
        Denied(Role(new(InventoryListType.PersonalCargo, 0)), request with { Position = new(InventoryListType.PersonalCargo, 0) }, 4);
        Denied(Role(Bag(3), core with { EquipmentLockId = 1 }), request, 4);
        Denied(Role(Bag(3), core with { ExpireTime = 1 }), request, 4);
        Denied(Role(Bag(3), InventoryRewardGrantService.Create(catalog.Get(10400))), request with { ItemId = 10400 }, 19);
        Denied(Role(Bag(3), core with { Durability = 0 }), request, 7);
        Denied(Role(Bag(3), core with { GenuineUpgrade = 8 }), request, 95);
        Denied(Role(Bag(3)), request with { MaterialSlot = -1 }, 22);
        Denied(Role(Bag(3), source: material with { ItemId = 999 }), request, 22);
        Denied(Role(Bag(3), source: material with { EquipmentLockId = 1 }), request, 22);
        Denied(Role(Bag(3), source: material with { ExpireTime = 1 }), request, 22);
        var poor = Role(Bag(3), source: material with { Value = firstCost.Count - 1 });
        Denied(poor, request, 22);
        That(poor.Inventory.Find(Bag(5))!.Core.Count > firstCost.Count, "another matching stack is not silently substituted");

        var forgeTemplate = catalog.Get(490705102);
        That(forgeTemplate.RefinementTicket is { Available: true, Portable: true, MinimumItemLevel: 1, MaximumItemLevel: 95 },
            "实际PVF便携炉保留1..95装备等级范围");
        var forge = InventoryRewardGrantService.Create(forgeTemplate, 2, now) with { ExpireTime = int.MaxValue };
        foreach (int roll in new[] { 4999, 5000 })
        {
            random.Value = roll;
            var role = Role(Bag(3), core with { GenuineUpgrade = 1 }, forge);
            var result = service.Refine(role, request, out var receipt, now);
            That(result.Accepted && receipt is { UsedTicket: true, MaterialRemaining: 1 } && receipt.Succeeded == (roll < 5000) &&
                receipt.NewLevel == (roll < 5000 ? 2 : 1), "便携炉使用普通概率边界，成败均只扣一炉");
            That(result.Plan!.After.Find(Bag(5)) == role.Inventory.Find(Bag(5)) && result.Plan.After.Gold == role.Inventory.Gold,
                "便携炉不扣另格气息或金币");
        }
        calls = random.Calls;
        Denied(Role(Bag(3), core with { GenuineUpgrade = 8 }, forge), request, 95);
        Denied(Role(Bag(3), source: forge with { ExpireTime = 1 }), request, 22);
        Denied(Role(Bag(3), source: forge with { EquipmentLockId = 1 }), request, 22);
        foreach (string range in new[] { "", "1", "95 1", "1 95 100" })
            That(RefinementTicketDefinition.Parse(new PvfLib.PvfDocument("[action type]\n`[portable genuine damage upgrade upgrede]`\n[/action type]\n[check usable itemlevel]\n" + range)) is { Available: false },
                "便携炉无效目标范围不得退回普通材料");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-refinement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("refinement", () => { });
            var initial = repo.Create(owner, new("RefineOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), core), new(0, Bag(4), material with { Value = firstCost.Count })], Gold: 1234)
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            That(!session.MutateInventory(character => service.Refine(character, request, out _), 4).Accepted, "not-ready session cannot refine");
            session.MarkReady();
            string before = Snapshot();
            var selected = session.Selected;
            long currencyAudits = (long)Sql("SELECT count(*) FROM currency_audit_log")!;
            Sql("CREATE TRIGGER fail_refinement BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'refinement rollback'); END");
            Reject(() => session.MutateInventory(character => service.Refine(character, request, out _)), "late SQL failure is propagated");
            That(Snapshot() == before && session.Selected == selected, "failure restores target, deleted material, revision, audits and session together");
            Sql("DROP TRIGGER fail_refinement");
            var result = session.MutateInventory(character => service.Refine(character, request, out _));
            That(result.Accepted && session.Selected!.Inventory.Find(Bag(4)) is null && session.Selected.Inventory.Find(Bag(3))!.Uid == initial.Inventory.Find(Bag(3))!.Uid,
                "consumed material disappears in the same commit without reallocating equipment");
            That(session.Selected!.Revision == selected!.Revision + 1 && (long)Sql("SELECT count(*) FROM currency_audit_log")! == currencyAudits,
                "one revision and no currency movement for refinement");
            Reject(() => repo.CommitInventory(owner, selected, result.Plan!), "old revision cannot reapply committed refinement");
            before = Snapshot();
            That(!session.MutateInventory(character => service.Refine(character, request, out _)).Accepted && Snapshot() == before,
                "exhausted material replay cannot charge or grant another level");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                "repository reopen restores full core and committed ownership exactly");
            table.TryCost(weapon, 1, out var failureCost);
            var failureRole = repo.Create(owner, new("RefineFail", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), core with { GenuineUpgrade = 1 }), new(0, Bag(4), material with { Value = failureCost.Count })])
            });
            random.Value = 5000;
            var failure = service.Refine(failureRole, request, out var failedReceipt, now);
            var savedFailure = InventoryService.Commit(repo, owner, failureRole, failure);
            That(failure.Accepted && failedReceipt is { Succeeded: false, OldLevel: 1, NewLevel: 1 } &&
                savedFailure.Inventory.Find(Bag(3)) == failureRole.Inventory.Find(Bag(3)) && savedFailure.Inventory.Find(Bag(4)) is null,
                "failed attempt commits exact material depletion while preserving the complete target");
            That(savedFailure.Revision == failureRole.Revision + 1 &&
                JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single(role => role.Id == savedFailure.Id)) == JsonSerializer.Serialize(savedFailure),
                "failed result is durable and does not return spent materials after reopen");
            calls = random.Calls;
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(character => service.Refine(character, request, out _)), "old generation cannot plan or commit");
            That(random.Calls == calls && Sql("PRAGMA integrity_check") is "ok", "stale owner performs no draw; database remains intact");

            object? Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Sql("SELECT count(*) FROM inventory_audit_log") + ":" +
                Sql("SELECT count(*) FROM currency_audit_log") + ":" + Sql("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - start} refinement domain and transaction assertions");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static InventorySlot Worn(short slot) => new(InventoryListType.Equipment, slot);
    static void Reject(Action action, string name)
    {
        bool failed = false;
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException or FormatException) { failed = true; }
        That(failed, name);
    }
    sealed class FixedRandom(int value) : Random
    {
        public int Value { get; set; } = value;
        public int Calls { get; private set; }
        public override int Next(int maxValue)
        {
            Calls++;
            if (Value < 0 || Value >= maxValue)
                throw new InvalidOperationException("锻造用例超出真实概率范围");
            return Value;
        }
    }
}
