using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class UpgradeChronicleTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "upgrade-chronicle"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources;
        Character actor;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("VictoryTcp", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get) with { Level = 95 };
            actor = repository.Create(owner, candidate);
            var grant = InventoryRewardGrantService.TryGrantBatch(actor,
                [new(10308623, 2), new(101040543, 1), new(10308622, 2)], items.Items, items.GoldLimits, items.EpicPieces);
            That(grant.Accepted, "转换TCP真实模板准备");
            actor = repository.CommitInventory(owner, actor, new(actor.Inventory, grant.Plan!.After with
            {
                Items = grant.Plan.After.Items.Select(item => item.Core.ItemId == 101040543 ? item with
                { Core = item.Core with { Attr = 12, GenuineUpgrade = 5, AmplifyType = 2, AmplifyValue = 43,
                    EnchantCardId = 100000201, Random0 = new(1, 2, 3), RandomState = 1 } } : item).ToImmutableArray()
            }, "victory_setup"));
        }
        var book = actor.Inventory.Items.Single(item => item.Core.ItemId == 10308623);
        var original = actor.Inventory.Items.Single(item => item.Core.ItemId == 101040543);
        var fragment = actor.Inventory.Items.Single(item => item.Core.ItemId == 10308622);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(actor.Slot);
            string before = Snapshot();
            Send(peer, 5);
            peer.Expect(1, 271, [0, 4, 0]);
            Send(peer, 0, 101040545);
            peer.Expect(1, 271, [0, 4, 0]);
            peer.Barrier();
            That(Snapshot() == before, "非法档位或交换目标模板不能扣料");
            Send(peer, 0);
            var ack = peer.Expect(1, 271).Body;
            // 独立按本版读包顺序解码，不调用生产 builder；空消费列表之后由权威槽更新。
            That(ack.Length == 3 && ack[0] == 1 && ack[1] == 0 && ack[2] == 0,
                "271成功包含result和零消费列表，不会截断或双扣");
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, original.Position.Index, -1, 0);
            var delta = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(delta, original.Position.Index, 101040551, original.Core.Value);
            BoosterTcpTests.Entry(delta, book.Position.Index, 10308623, 1);
            BoosterTcpTests.Entry(delta, fragment.Position.Index, -1, 0);
            peer.Barrier();
            using (var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var current = repository.LoadCharacter(reader, actor.Slot);
                var output = current.Inventory.Items.Single(item => item.Core.ItemId == 101040551);
                That(output.Uid == original.Uid && output.Position == original.Position &&
                    output.Core == original.Core with { ItemId = 101040551 }, "真实事务原位保留UID及完整属性");
                That(current.Inventory.Find(book.Position)!.Core.Count == 1 &&
                    !current.Inventory.Items.Any(item => item.Core.ItemId == 10308622), "只扣书1及碎片2而非购买价8");
            }
            before = Snapshot();
            Send(peer, 4);
            peer.Expect(1, 271, [0, 4, 0]);
            peer.Barrier();
            That(Snapshot() == before, "重放旧目标请求不能消费剩余书或复制产物");
        }
        // 补第二把原武器和材料，晚期SQL失败必须整单恢复，不能发虚假成功。
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            actor = repository.LoadCharacter(owner, actor.Slot);
            var grant = InventoryRewardGrantService.TryGrantBatch(actor,
                [new(101040543, 1), new(10308622, 2)], items.Items, items.GoldLimits, items.EpicPieces);
            That(grant.Accepted, "准备晚期回滚原件");
            actor = repository.CommitInventory(owner, actor, grant.Plan!);
            original = actor.Inventory.Items.Single(item => item.Core.ItemId == 101040543);
        }
        fixture.Sql("CREATE TRIGGER victory_abort AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'victory late rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(actor.Slot);
            string before = Snapshot();
            Send(peer, 2);
            peer.Closed();
            That(Snapshot() == before, "晚期SQL失败恢复书/材料/原UID装备并无成功ACK");
        }
        fixture.Sql("DROP TRIGGER victory_abort");
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(actor.Slot);
            Send(peer, 2);
            peer.Expect(1, 271, [1, 0, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, original.Position.Index, -1, 0);
            var finalDelta = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(finalDelta, original.Position.Index, 101040551, original.Core.Value);
            BoosterTcpTests.Entry(finalDelta, book.Position.Index, -1, 0);
            var finalMaterial = actor.Inventory.Items.Single(item => item.Core.ItemId == 10308622);
            BoosterTcpTests.Entry(finalDelta, finalMaterial.Position.Index, -1, 0);
            peer.Barrier();
            using var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var current = repository.LoadCharacter(reader, actor.Slot);
            That(current.Inventory.Items.Count(item => item.Core.ItemId == 101040551) == 2 &&
                !current.Inventory.Items.Any(item => item.Core.ItemId is 10308623 or 10308622 or 101040543),
                "服务重建重连后回滚原件可转换且不丢失先前产物");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "转换事务数据库完整");

        string Snapshot() => fixture.Snapshot() +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM inventory_audit_log ORDER BY rowid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM sqlite_sequence ORDER BY name"));

        void Send(TcpPeer peer, byte quality, int targetId = 101040543) => peer.Send(271, Build(writer =>
        {
            writer.Write((ushort)book.Position.Index); writer.Write(10308623);
            writer.Write((ushort)original.Position.Index); writer.Write(targetId);
            writer.Write(quality); writer.Write(-1); writer.Write((ushort)0);
        }));
    }
}
