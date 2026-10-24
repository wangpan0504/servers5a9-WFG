using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using System.Text.Json;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class InventoryExpansionTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        InventoryExpansionTests.RunCore();
        InventoryExpansionTests.RunResources(resources.ItemResources.Items);
        const int ticket = 10152158;
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "inventory-expansion"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        Character original;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var weapon = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)).Items.Single(
                item => item.Position == new InventorySlot(InventoryListType.Equipment, 12));
            weapon = weapon with { Core = weapon.Core with { Attr = 8, EnchantCardId = 123,
                EnchantUpgrade = 2, AmplifyType = 1, AmplifyValue = 22 } };
            original = repository.Create(owner, new("ExpandTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([weapon, .. Enumerable.Range(65, 4).Select(slot => new InventoryItem(0,
                    new(InventoryListType.Main, (short)slot), new(ticket, ItemKind.Consumable, 1) { ExpireTime = checked((int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400)) }))], 0, 1234)
            });
            repository.Create(owner, new("ExpandNew", resources.Towns.Room, resources.Towns.InitialReturn)
            { AcceptedStoryLevel = 1, Inventory = new([new(0, new(InventoryListType.Main, 65), original.Inventory.Find(new(InventoryListType.Main, 65))!.Core)]) });
        }
        var otherItems = JsonSerializer.Serialize(original.Inventory.Items.Where(item => item.Core.ItemId != ticket));
        var owners = new AccountOwners();
        var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.UtcNow);
        var service = new InventoryExpansionService(resources.ItemResources.Items, clock);
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            string baseline = State();
            var expiring = service.Plan(original, 65).Plan!;
            clock.Advance(TimeSpan.FromSeconds(original.Inventory.Find(new(InventoryListType.Main, 65))!.ExpireTime -
                clock.GetUtcNow().ToUnixTimeSeconds()));
            Reject<InventoryQualificationExpiredException>(() => repository.CommitInventory(owner, original, expiring),
                "规划后到截止瞬间提交仍拒绝，不能消费过期券");
            That(State() == baseline, "提交到期不改券、容量、revision及审计");
            service = new InventoryExpansionService(resources.ItemResources.Items);
            var revisionPlan = service.Plan(original, 65).Plan!;
            var current = repository.CommitInventory(owner, original,
                new(original.Inventory, original.Inventory with { Gold = original.Inventory.Gold + 1 }, "expansion_fixture_revision"));
            current = repository.CommitInventory(owner, current,
                new(current.Inventory, current.Inventory with { Gold = original.Inventory.Gold }, "expansion_fixture_restore"));
            That(JsonSerializer.Serialize(current.Inventory) == JsonSerializer.Serialize(original.Inventory),
                "旧计划测试恢复完整库存，仅保留revision差异");
            baseline = State();
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, original, revisionPlan),
                "库存相同但revision已变仍拒绝旧扩容计划");
            var session = new GameSession(repository, resources.Towns, owner);
            session.Select(original.Slot);
            That(!session.PlanAndCommitInventory(c => service.Plan(c, 65), 0).Accepted,
                "未Ready不执行扩容");
            session.MarkReady();
            var switched = session.PlanAndCommitInventory(c =>
            {
                var result = service.Plan(c, 65);
                session.ReturnToRoster(); session.Select(original.Slot); session.MarkReady();
                return result;
            }, 0);
            That(!switched.Accepted && State() == baseline,
                "锁外规划期间重选同角色使旧epoch失效，全部资产不变");
            var ownerPlan = service.Plan(current, 65).Plan!;
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, current, ownerPlan),
                "接管后旧owner/generation扩容计划不能提交");
            That(State() == baseline, "旧owner拒绝不改审计或资产");
        }
        await fixture.StartAsync();
        foreach (byte[] malformed in new byte[][] { [], [65], [65, 0, 0], [65, 0, 8, 0] })
        {
            using var peer = Open(fixture);
            peer.Select(); peer.Barrier();
            string baseline = State();
            peer.Send(430, malformed); peer.Closed();
            That(State() == baseline, "畸形430正文保持现有协议断连合同且全部资产不变");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = State();
            peer.Send(430, [255, 255]); peer.Expect(1, 430, [0, 0, 0]);
            peer.Send(430, [69, 0]); peer.Expect(1, 430, [0, 0, 0]); peer.Barrier();
            That(State() == before, "未知购买哨兵和空槽有完整失败ACK且不修改资产");
            fixture.Sql("CREATE TRIGGER fail_expansion_tcp AFTER INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='inventory_expansion_coupon' BEGIN SELECT RAISE(ABORT,'expansion rollback'); END");
            peer.Send(430, [65, 0]); peer.Closed();
            That(State() == before, "SQL失败不发布成功，消费、容量、revision整笔回滚");
            fixture.Sql("DROP TRIGGER fail_expansion_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            for (byte slot = 65; slot < 68; slot++)
            {
                long revision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", original.Id)[0][0]);
                peer.Send(430, [slot, 0]); peer.Expect(1, 430, [1]);
                var full = peer.Expect(0, 13).Body;
                That(full[0] == 0 && U16(full, 1) == (slot - 64) * 8,
                    "成功ACK后完整Main列表携带已提交的新档位");
                peer.Barrier();
                That(fixture.Query("SELECT revision FROM characters WHERE id=$p0", original.Id) is [[var saved]] &&
                    Equals(saved, revision + 1), "每次成功消费和扩容只提交一个revision");
                string before = State();
                peer.Send(430, [slot, 0]); peer.Expect(1, 430, [0, 0, 0]); peer.Barrier();
                That(State() == before, "已耗尽的源槽重发不会升档或扣其它槽券");
            }
            string maximum = State();
            peer.Send(430, [68, 0]); peer.Expect(1, 430, [0, 0, 0]); peer.Barrier();
            That(State() == maximum, "满扩展拒绝保留第四张券");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1); peer.Barrier();
            string full = State();
            peer.Send(430, [65, 0]); peer.Expect(1, 430, [0, 0, 0]); peer.Barrier();
            That(State() == full, "新角色默认24满档实际请求拒绝且保留其源券");
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var loaded = repository.LoadCharacter(owner, original.Slot);
            That(JsonSerializer.Serialize(loaded.Inventory.Items.Where(item => item.Core.ItemId != ticket)) == otherItems,
                "三次扩容保留全部非券实例的UID、强化、附魔、增幅与完整核心");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart", isolatedProcess: true);
        using (var peer = Open(fixture))
        {
            var packets = peer.Select();
            That(packets.Any(packet => packet.Kind == 0 && packet.Opcode == 13 && packet.Body[0] == 0 && U16(packet.Body, 1) == 24),
                "独立服务进程重启后的完整库存恢复扩展24");
            peer.Barrier();
        }
        That(fixture.Query("SELECT inventory_expansion,gold FROM characters ORDER BY slot") is [[24L, 1234L], [24L, 0L]] &&
            fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='inventory_expansion_coupon'") is [[3L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "只发生三次原子升档，金币及另一个角色默认容量不变，数据库完整");

        string State() => fixture.Snapshot("SELECT id,revision,inventory_expansion,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
    }
}
