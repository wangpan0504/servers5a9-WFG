using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CargoPurchaseTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cargo-purchase"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.Create(owner, new("CargoBuy", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            role = InventoryService.Commit(repository, owner, role, new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Cera, 1000));
            role = InventoryService.Commit(repository, owner, role,
                new InventoryService(r.Items, r.GoldLimits, r.EpicPieces).Grant(role, [new(3299, 2)]));
            InventoryService.Commit(repository, owner, role, new(true, 0,
                new(role.Inventory, role.Inventory with { AccountCargo = new(Capacity: 8) }, "account_cargo_fixture")));
        }
        long materialUid = Convert.ToInt64(fixture.Query("SELECT item_uid FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=3299")[0][0]);
        string materialCore = fixture.Snapshot("SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", materialUid);
        That(r.CashShop.Product(100063) is { ItemId: 50, CeraPrice: 60 }, "当前资源金库工具商品及价格");
        await fixture.StartAsync();
        string before;
        fixture.Sql("CREATE TRIGGER fail_cargo_purchase BEFORE UPDATE OF cargo_capacity ON characters WHEN NEW.cargo_capacity>OLD.cargo_capacity BEGIN SELECT RAISE(ABORT,'cargo rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); before = State(); peer.Send(64, Purchase()); peer.Closed();
        }
        That(State() == before, "容量写入失败时扣款、物品与容量一起回滚");
        fixture.Sql("DROP TRIGGER fail_cargo_purchase");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Send(64, Purchase());
            That(peer.Expect(1, 64).Body[0] == 1, "金库商城直扩购买成功");
            That(U32(peer.Expect(0, 53).Body, 1) == 940, "只扣当前资源价格60");
            peer.Expect(0, 66, [1, 0, 24, 0]);
            var cargo = peer.Expect(0, 13).Body;
            That(cargo[0] == 2 && U16(cargo, 1) == 24, "先通知打开的窗口扩容，再同步24格权威列表");
            peer.Barrier();
            That(fixture.Query("SELECT cargo_capacity FROM characters") is [[24L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=50") is [[0L]],
                "只扩容，不发旧工具实体");
            CargoTcpTests.Transfer(peer, 0, 121, 3299, 2, 2, 23, 23, 2);
            That(U32(peer.Expect(0, 14).Body, 5) == uint.MaxValue, "扩容后存入新格清空真实源");
            var stored = peer.Expect(0, 14).Body;
            That(stored[0] == 2 && U16(stored, 3) == 23 && U32(stored, 5) == 3299 && U32(stored, 9) == 2,
                "不关窗或重选即可存入新开放的末格");
            peer.Barrier();
            string committed = State();
            peer.Send(64, Purchase()); peer.Expect(1, 64, [0, 0, 0]);
            peer.Barrier(packet => That(packet.Opcode is not (66 or 13), "重复购买拒绝不重发容量或窗口刷新"));
            That(State() == committed, "已达该档容量拒绝重复扣款");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            That(entry[6].Body[0] == 2 && U16(entry[6].Body, 1) == 24 && U16(entry[6].Body, 3) == 1 && U16(entry[6].Body, 5) == 23,
                "重启及选角恢复24格个人金库及新增格物品");
            peer.Barrier();
            CargoTcpTests.Transfer(peer, 2, 23, 23, 2, 0, 121, 0, 2);
            That(U32(peer.Expect(0, 14).Body, 5) == 3299 && U32(peer.Expect(0, 14).Body, 5) == uint.MaxValue,
                "新增格中的物品可以正常取回");
            peer.Barrier();
            That(fixture.Snapshot("SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", materialUid) == materialCore &&
                fixture.Query("SELECT list_type,slot_index FROM character_inventory_slots WHERE item_uid=$p0", materialUid) is [[0L,121L]],
                "扩容存取及重启保持同一UID与完整物品核心");
        }
        await fixture.StopAsync();
        await AcquisitionMailAsync(fixture, repository, resources);
        await AccountPurchaseAsync(source, root, resources);
        string State() => fixture.Snapshot("SELECT revision,cargo_capacity FROM characters") +
            fixture.Snapshot("SELECT * FROM account_currency_balances") + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
    }

    static async Task AcquisitionMailAsync(TcpTestFixture fixture, CharacterRepository repository, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        var mail = new MailboxService(repository, r.Items, r.GoldLimits, r.EpicPieces);
        var recipient = mail.FindRecipient("CargoBuy")!;
        var deliveries = new[] { 110, 2681921 }.Select(id =>
        {
            var template = r.Items.Get(id);
            var item = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), InventoryRewardGrantService.Create(template));
            return mail.Deliver(new("cargo-acquire:" + id, recipient, "系统", "扩容测试", 0, [item], DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }).ToArray();
        That(deliveries.All(row => row.Accepted), "个人和账号扩容工具可作为邮件托管，领取时再生效");
        uint[] ids = deliveries.Select(row => row.MailId).ToArray();
        uint[] claims;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var page = mail.Inspect(owner, recipient.Id, DateTimeOffset.UtcNow);
            claims = ids.Select(id => page.Entries.Single(row => row.Id == id).Attachments.Single().ClaimId).ToArray();
        }
        await fixture.StartAsync("acquisition");
        fixture.Sql("CREATE TRIGGER reject_acquired_cargo BEFORE UPDATE OF capacity ON account_cargo WHEN NEW.capacity>OLD.capacity BEGIN SELECT RAISE(ABORT,'acquired cargo rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = State();
            Extract(peer); peer.Closed();
            That(State() == before, "领取晚期失败同时回滚个人容量、账号容量和邮件资格");
        }
        fixture.Sql("DROP TRIGGER reject_acquired_cargo");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); Extract(peer);
            peer.Expect(1, 95, Build(w =>
            {
                w.Write((byte)1); w.Write(2);
                for (int i = 0; i < ids.Length; i++) { w.Write(ids[i]); w.Write(claims[i]); w.Write(0); }
            }));
            var notices = new List<string>();
            peer.Barrier(packet =>
            {
                That(packet.Opcode != 14, "扩容资格领取不发放或清除虚构STK槽");
                if (packet.Opcode == 66) { Bytes(packet.Body, [1, 0, 40, 0], "先刷新个人金库窗口"); notices.Add("66"); }
                if (packet.Opcode == 13) notices.Add("13:" + packet.Body[0]);
            });
            That(notices.SequenceEqual(new[] { "66", "13:2", "13:12" }) &&
                fixture.Query("SELECT cargo_capacity FROM characters") is [[40L]] && fixture.Query("SELECT capacity FROM account_cargo") is [[16L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId') IN (110,2681921)") is [[0L]] &&
                fixture.Query("SELECT count(*) FROM mailbox_attachments WHERE claimed=1") is [[2L]],
                "邮件资格与双仓扩容同事务，不保留STK且通知顺序正确");
        }
        await fixture.StopAsync();
        void Extract(TcpPeer peer) => peer.Send(95, Build(w => { w.Write(claims.Length); foreach (uint id in claims) w.Write(id); }));
        string State() => string.Join('\n', new[] { "characters", "account_cargo", "character_inventory_items", "mailbox_messages", "mailbox_attachments" }
            .Select(table => fixture.Snapshot("SELECT * FROM " + table)));
    }

    static async Task AccountPurchaseAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "account-cargo-purchase"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.Create(owner, new("AccountBuy", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            role = InventoryService.Commit(repository, owner, role, new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Cera, 10000));
            InventoryService.Commit(repository, owner, role, new(true, 0,
                new(role.Inventory, role.Inventory with { AccountCargo = role.Inventory.AccountCargo with { Capacity = 8 } }, "fixture")));
        }
        await fixture.StartAsync();
        Rollback("paid");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Send(64, Purchase(100534));
            That(peer.Expect(1, 64).Body[0] == 1, "账号仓库商城购买成功");
            That(fixture.Query("SELECT capacity FROM account_cargo") is [[16L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=2681921") is [[0L]],
                "真实CMD64购买直接扩账号金库至16格，不发升级工具");
            That(U32(peer.Expect(0, 53).Body, 1) == 8000, "账号扩容只扣一次2000点券");
            var cargo = peer.Expect(0, 13).Body;
            That(cargo[0] == 12 && U16(cargo, 1) == 16, "购买后投影账号容量");
            peer.Barrier();
        }
        await fixture.StopAsync();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.LoadCharacter(owner, 0);
            InventoryService.Commit(repository, owner, role, new(true, 0,
                new(role.Inventory, role.Inventory with { AccountCargo = role.Inventory.AccountCargo with { Capacity = 32 },
                    Items = role.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), new(2682968, GameWorld.Items.ItemKind.Consumable, 2))) }, "legacy_cargo_tool_fixture")));
            repository.Create(owner, new("AccountTwin", resources.Towns.Room, resources.Towns.InitialReturn) { AcceptedStoryLevel = 1 });
        }
        await fixture.StartAsync("history");
        Rollback("history", 102045);
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Send(64, Purchase(102045));
            That(peer.Expect(1, 64).Body[0] == 1, "再次点击购买可兑历史账号工具");
            var item = peer.Expect(0, 14).Body;
            That(item[0] == 0 && U32(item, 5) == 2682968 && U32(item, 9) == 1, "历史券只消费一张并投影剩余数量");
            var cargo = peer.Expect(0, 13).Body;
            That(cargo[0] == 12 && U16(cargo, 1) == 40, "历史券兑换投影40格");
            peer.Barrier();
            That(fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=15") is [[8000L]], "历史券兑换无重复点券支出");
            string committed = State();
            peer.Send(64, Purchase(102045)); peer.Expect(1, 64, [0, 0, 0]);
            peer.Send(306); peer.Expect(1, 306, [0, 20, 0]); peer.Barrier();
            That(State() == committed, "重放64及跟随裸306不扣款、不消耗旧工具或越档");
            peer.Send(64, Purchase(102108));
            That(peer.Expect(1, 64).Body[0] == 1 && U32(peer.Expect(0, 53).Body, 1) == 6000, "下一付费商品准确扣款一次");
            cargo = peer.Expect(0, 13).Body;
            That(cargo[0] == 12 && U16(cargo, 1) == 48, "连续付费档发布48格");
            peer.Barrier(); committed = State();
            peer.Send(306); peer.Expect(1, 306, [0, 20, 0]); peer.Barrier();
            That(State() == committed, "付费64成功后裸306不额外推进下一付费档");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(1);
            That(entry[7].Body[0] == 12 && U16(entry[7].Body, 1) == 48, "重启与换角恢复账号共享48格");
            peer.Barrier();
            That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=2682968") is [[1L]],
                "重启保留未消费的旧档工具");
        }
        await fixture.StopAsync();

        string State() => fixture.Snapshot("SELECT revision FROM characters ORDER BY id") + fixture.Snapshot("SELECT * FROM account_cargo") +
            fixture.Snapshot("SELECT * FROM account_currency_balances") + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM cargo_audit_log") + fixture.Snapshot("SELECT * FROM currency_audit_log") + fixture.Snapshot("SELECT * FROM inventory_audit_log");
        void Rollback(string name, int commodity = 100534)
        {
            fixture.Sql("CREATE TRIGGER fail_account_purchase BEFORE UPDATE OF capacity ON account_cargo WHEN NEW.capacity>OLD.capacity BEGIN SELECT RAISE(ABORT,'account cargo rollback'); END");
            string before = State();
            using (var peer = Open(fixture))
            {
                peer.Select(); peer.Barrier(); before = State(); peer.Send(64, Purchase(commodity)); peer.Closed();
            }
            That(State() == before, name + " 账号容量写入失败将点券、历史券、revision和审计同事务回滚");
            fixture.Sql("DROP TRIGGER fail_account_purchase");
        }
    }

    internal static byte[] Purchase(int commodity = 100063)
    {
        byte[] body = new byte[19]; body[2] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), commodity);
        return body;
    }
}
