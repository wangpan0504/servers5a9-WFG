using DfoServer.Infrastructure;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using PvfLib;

namespace DfoServer.Testing.Inventory;

internal static class RefinementTicketTests
{
    public static async Task RunAsync(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int start = Count;
        var computer = new CharacterStatComputer(characters);
        var items = itemResources.Items;
        var fixedTicket = items.Get(10007131).RefinementTicket!;
        var additional = items.Get(10007990).RefinementTicket!;
        var restricted = items.Get(10006551).RefinementTicket!;
        That(fixedTicket is { Available: true, Level: 5, SuccessWeight: 10000, Additional: false, MaximumSourceLevel: -1 }, "fixed ticket reads matching PVF");
        That(additional is { Available: true, Level: 1, SuccessWeight: 10000, Additional: true, MaximumSourceLevel: -1 }, "additional ticket reads matching PVF");
        That(restricted.AllowedItemIds.Length == 58 && restricted.AllowedItemIds.Contains(401000012), "special ticket keeps declared weapon list");
        foreach (int chance in new[] { 0, 1, 50, 100 })
            That(Parse($"5 {chance} `fixed` -1").SuccessWeight == chance * 100, "ticket percent converts to ten-thousand weight without rounding or clamping");
        That(Parse("5 100 `fixed`") is { Available: true, MaximumSourceLevel: -1 }, "omitted source limit retains resource default");
        That(Parse("9 100 `fixed` -1").TryTargetLevel(8, 8, out byte targetLevel) && targetLevel == 9,
            "ordinary NPC maximum does not override an explicitly declared fixed ticket level");
        var capped = Parse("2 100 `additional` 3");
        That(capped.TryTargetLevel(3, 8, out targetLevel) && targetLevel == 5 && !capped.TryTargetLevel(4, 8, out _), "additional source-level bound is inclusive");
        That(Parse("2 100 `additional` -1").TryTargetLevel(6, 8, out targetLevel) && targetLevel == 8 &&
            !Parse("2 100 `additional` -1").TryTargetLevel(7, 8, out _), "negative source limit still obeys ordinary maximum");
        foreach (string value in new[] { "0 100 `fixed` -1", "128 100 `fixed` -1", "5 101 `fixed` -1", "5 -1 `fixed` -1",
            "5 50 `unknown` -1", "5 50 `fixed` -1 1", "5 50.5 `fixed` -1" })
            That(Parse(value) is { Available: false, PendingRule: not null }, "unavailable ticket metadata is retained without ordinary-material fallback");
        That(RefinementTicketDefinition.Parse(new PvfDocument("[name]\n`普通物品`")) is null, "ordinary item has no implicit ticket definition");
        That(RefinementTicketDefinition.Parse(new PvfDocument("[equipment separate reinforcement ticket]\n5 100 `fixed` -1\n" +
            "[reinforcement usable item list]\n401000012")) is { Available: false }, "unclosed restrictions are not silently ignored");

        var now = DateTimeOffset.UtcNow;
        var weapon = characters.Get(0).Equipment.Single(row => row.Slot == 12).Template;
        var core = InventoryRewardGrantService.Create(weapon) with
        {
            GenuineUpgrade = 7, Durability = 0, Value = 1234567, Attr = 0xA7, SealFlag = 1, EnchantCardId = 3175,
            EnchantUpgrade = 2, AmplifyType = 2, AmplifyValue = 21, Rune = 3, Random0 = new(1, 2, 3), SortLockFlag = 1
        };
        var random = new LastRoll();
        var service = new InventoryRefinementService(items, itemResources.Refinement, random);
        var request = new RefinementRequest(Bag(3), weapon.Id, 4, 0);
        ItemCore Ticket(int id, int count = 2) => InventoryRewardGrantService.Create(items.Get(id), count) with { ExpireTime = 0 };
        // 历史PVF活动券在隔离fixture中显式构造未到期实例；生产创建和使用仍遵守原期限。
        Character Role(InventorySlot slot, ItemCore? equipment = null, ItemCore? coupon = null) => new(1, 0, "Ticket", 0, towns.Room, towns.InitialReturn)
        {
            Job = 15, Inventory = new([new(100, slot, equipment ?? core), new(101, Bag(4), coupon ?? Ticket(10007131)),
                new(102, Bag(5), Ticket(10007990)), new(103, Bag(6), InventoryRewardGrantService.Create(items.Get(3326), 100))], Gold: 1234)
        };
        foreach (var slot in new[] { Bag(3), Bag(9), new InventorySlot(InventoryListType.Equipment, 12), new InventorySlot(InventoryListType.Equipment, 24) })
        {
            var role = Role(slot);
            var result = service.Refine(role, request with { Position = slot, Confirmation = 2 }, out var receipt, now);
            That(result.Accepted && receipt is { UsedTicket: true, Succeeded: true, OldLevel: 7, NewLevel: 5, MaterialRemaining: 1, SourceItemId: 10007131, Announce: true },
                "fixed ticket overwrites a higher level on damaged, sealed, sorted or quickbar/worn weapon");
            That(result.Plan!.After.Find(slot) == role.Inventory.Find(slot)! with { Core = core with { GenuineUpgrade = 5 } } &&
                result.Plan.After.Find(Bag(5)) == role.Inventory.Find(Bag(5)) && result.Plan.After.Find(Bag(6)) == role.Inventory.Find(Bag(6)) && result.Plan.After.Gold == 1234,
                "one chosen ticket consumed; other stacks, ordinary material, gold, UID and unrelated core fields unchanged");
            That(receipt!.Target == result.Plan.After.Find(slot) && receipt.NeedsAttributeRefresh == (slot.List == InventoryListType.Equipment),
                "dependent-system interface receives exact full target and worn-attribute invalidation flag");
        }
        var same = service.Refine(Role(Bag(3), core with { GenuineUpgrade = 5 }), request, out var sameReceipt, now);
        That(same.Accepted && sameReceipt is { Succeeded: true, OldLevel: 5, NewLevel: 5, NeedsAttributeRefresh: false, MaterialRemaining: 1 },
            "fixed ticket on equal level still consumes exactly one without inventing a no-op exemption");
        var plus = service.Refine(Role(Bag(3), coupon: Ticket(10007990)), request with { Confirmation = 1 }, out var plusReceipt, now);
        That(plus.Accepted && plusReceipt is { UsedTicket: true, OldLevel: 7, NewLevel: 8, Announce: true }, "additional ticket reaches cap despite damaged durability");
        var allowedCore = InventoryRewardGrantService.Create(items.Get(401000012)) with { Durability = 0, ExpireTime = 0 };
        That(service.Refine(Role(Bag(3), allowedCore, Ticket(10006551)), request with { ItemId = allowedCore.ItemId }, out _, now).Accepted,
            "declared special weapon remains eligible without added character job or level rules");
        int calls = random.Calls;
        void Denied(Character role, RefinementRequest value, ushort error)
        {
            var result = service.Refine(role, value, out var receipt, now);
            That(!result.Accepted && result.Error == error && receipt is null && random.Calls == calls, "invalid ticket attempt consumes neither ticket nor random draw");
        }
        Denied(Role(Bag(3), core with { GenuineUpgrade = 8 }, Ticket(10007990)), request, 95);
        Denied(Role(Bag(3), coupon: Ticket(10006551)), request, 19);
        Denied(Role(Bag(3), coupon: Ticket(10007131) with { ExpireTime = 1 }), request, 22);
        Denied(Role(Bag(3), coupon: Ticket(10007131) with { EquipmentLockId = 1 }), request, 22);
        Denied(Role(Bag(3), core with { EquipmentLockId = 1 }), request, 4);
        Denied(Role(Bag(3), core with { ExpireTime = 1 }), request, 4);
        Denied(Role(Bag(3)), request with { MaterialSlot = 3 }, 4);
        Denied(Role(Bag(3)), request with { MaterialSlot = 6 }, 7);

        string root = Path.Combine(Path.GetTempPath(), "s5a9-refine-ticket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("ticket", () => { });
            repo.Create(owner, new("TicketOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), core with { SortLockFlag = 0 }), new(0, Bag(4), Ticket(10007131, 3))], Gold: 1234)
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            var frames = new List<(byte Kind, ushort Opcode)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0, (kind, opcode, _, _, _) => frames.Add((kind, opcode)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            var effects = new RecordedEffects((committed, receipt) =>
            {
                That(JsonSerializer.Serialize(repo.List(owner).Single()) == JsonSerializer.Serialize(committed), "callback sees the committed database revision");
                That(receipt.Target == committed.Inventory.Find(receipt.Position), "callback never receives a planned or stale target");
                That(frames[^1] == (0, 14), "asset ACK and deltas precede optional effects");
            });
            var commands = new GameCommandRegistry();
            new RefinementHandler(service, new InventoryProjection(computer, new TownProjection(towns, computer, null, itemResources.EpicPieces), itemResources.EpicPieces), effects).Register(commands);
            commands.Seal();
            var packet = new Packet(1, 438, Request(weapon.Id));
            await commands.DispatchAsync(context, packet);
            That(effects.Calls == 1 && session.Selected!.Inventory.Find(Bag(4))!.Core.Count == 2, "valid ticket commits and publishes once");
            var saved = session.Selected;
            string before = Snapshot();
            Sql("CREATE TRIGGER fail_ticket BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'ticket rollback'); END");
            await RejectAsync(() => commands.DispatchAsync(context, packet), "late database failure propagates");
            That(Snapshot() == before && session.Selected == saved && effects.Calls == 1, "rollback publishes no effect and preserves full ticket, weapon and revision");
            Sql("DROP TRIGGER fail_ticket");
            effects.Fail = true;
            await RejectAsync(() => commands.DispatchAsync(context, packet), "optional system failure propagates without retrying assets");
            That(effects.Calls == 2 && session.Selected!.Inventory.Find(Bag(4))!.Core.Count == 1 && session.Selected.Revision == saved!.Revision + 1,
                "failed optional callback cannot refund, duplicate or reroll committed refinement");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected), "reopen restores ticket effects exactly");
            calls = random.Calls;
            using var replacement = owners.Acquire(owner.Account, () => { });
            await RejectAsync(() => commands.DispatchAsync(context, packet), "replaced owner cannot invoke the ticket or effects interface");
            That(effects.Calls == 2 && random.Calls == calls && Sql("PRAGMA integrity_check") is "ok", "stale owner causes no draw, write or callback");

            object? Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Sql("SELECT count(*) FROM inventory_audit_log") + ":" + Sql("SELECT count(*) FROM currency_audit_log");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - start} refinement ticket and dependent-system interface assertions");
    }

    static RefinementTicketDefinition Parse(string value) => RefinementTicketDefinition.Parse(new PvfDocument("[equipment separate reinforcement ticket]\n" + value))!;
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static byte[] Request(int itemId) => TcpPeer.Build(writer =>
    {
        writer.Write((byte)0);
        writer.Write((short)3);
        writer.Write(itemId);
        writer.Write((short)4);
        writer.Write(3);
        writer.Write("NPC"u8);
        writer.Write((byte)2);
    });
    static async Task RejectAsync(Func<Task> action, string message)
    {
        bool failed = false;
        try { await action(); }
        catch (Exception e) when (e is SqliteException or InvalidOperationException or IOException) { failed = true; }
        That(failed, message);
    }
    sealed class LastRoll : Random
    {
        public int Calls { get; private set; }
        public override int Next(int maxValue)
        {
            Calls++;
            return maxValue - 1;
        }
    }
    sealed class RecordedEffects(Action<Character, RefinementReceipt> check) : IRefinementEffects
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task PublishAsync(GameConnectionContext context, Character committed, RefinementReceipt receipt)
        {
            Calls++;
            context.Owner.Execute(() =>
            {
                check(committed, receipt);
                return true;
            });
            return Fail ? Task.FromException(new IOException("测试回调失败")) : Task.CompletedTask;
        }
    }
}
