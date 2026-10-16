using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CharacterSlotsTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "character-slots"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var product = r.CashShop.Product(103660)!;
        That(r.Items.Get(product.ItemId).CharacterSlotMaximum == 62 && product.CeraPrice > 0, "当前PVF角色栏上限与价格");
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.Create(owner, new("SlotsTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
            InventoryService.Commit(repository, owner, role, new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Cera, 1_000_000));
            for (int i = 1; i < 32; i++) repository.Create(owner, new("Fill" + i, resources.Towns.Room, resources.Towns.InitialReturn));
            Reject<InvalidDataException>(() => repository.Create(owner, new("Denied33", resources.Towns.Room, resources.Towns.InitialReturn)), "默认32栏不越界建角");
        }
        // 真正恢复v46缺列与旧CHECK；不只修改user_version伪造旧库。
        fixture.Sql("ALTER TABLE characters DROP COLUMN devil_usage; ALTER TABLE characters DROP COLUMN stamina_until");
        fixture.Sql("ALTER TABLE account_wallets DROP COLUMN character_slots");
        fixture.Sql("PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'CHECK(slot BETWEEN 0 AND 61)','CHECK(slot BETWEEN 0 AND 31)') WHERE name='characters'; PRAGMA writable_schema=OFF; PRAGMA schema_version=9000; PRAGMA user_version=46");
        await fixture.StartAsync();
        That(fixture.Query("SELECT character_slots FROM account_wallets") is [[32L]] &&
            fixture.Query("SELECT count(*) FROM characters") is [[32L]], "旧库升级默认32且保留所有角色");
        fixture.Sql("CREATE TRIGGER fail_slot_purchase BEFORE UPDATE OF character_slots ON account_wallets WHEN NEW.character_slots>OLD.character_slots BEGIN SELECT RAISE(ABORT,'slots rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); string before = State(); peer.Send(64, Purchase()); peer.Closed();
            That(State() == before, "容量保存失败不扣点券或推进revision");
        }
        fixture.Sql("DROP TRIGGER fail_slot_purchase");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Send(64, Purchase());
            That(peer.Expect(1, 64).Body[0] == 1, "角色栏购买成功");
            That(U32(peer.Expect(0, 53).Body, 1) == 1_000_000 - product.CeraPrice, "按当前PVF价格扣款");
            peer.Barrier();
        }
        await fixture.StopAsync(); await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            That(U16(peer.Roster, 3) == 33 && U16(peer.Roster, 5) == 33, "重启登录名册两个容量字段均为33");
            peer.Select(); peer.Barrier();
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            That(repository.Create(owner, new("Slot33", resources.Towns.Room, resources.Towns.InitialReturn)).Slot == 32, "第33角色可真实保存");
            Reject<InvalidDataException>(() => repository.Create(owner, new("Denied34", resources.Towns.Room, resources.Towns.InitialReturn)), "未买第34栏时拒绝建角");
            var service = new InventoryCashShopService(r.Items, r.CashShop, r.GoldLimits, r.EpicPieces, contracts: r.Contracts);
            var role = repository.LoadCharacter(owner, 0);
            for (int i = 33; i < 62; i++)
            {
                var result = service.Buy(role, new(0, [new(103660, 0)]), out _);
                That(result.Accepted, "角色栏可逐次扩到62");
                role = InventoryService.Commit(repository, owner, role, result);
            }
            That(!service.Buy(role, new(0, [new(103660, 0)]), out _).Accepted, "满62栏拒绝继续扣款");
            for (int i = 33; i < 62; i++) repository.Create(owner, new("Fill" + i, resources.Towns.Room, resources.Towns.InitialReturn));
            That(repository.ListRoster(owner).Last().Slot == 61, "最后可用槽61真实落库");
            Reject<InvalidDataException>(() => repository.Create(owner, new("Denied63", resources.Towns.Room, resources.Towns.InitialReturn)), "63角色拒绝");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "扩展保留结构完整性与外键");
        string State() => fixture.Snapshot("SELECT revision FROM characters ORDER BY id") + fixture.Snapshot("SELECT * FROM account_wallets") + fixture.Snapshot("SELECT * FROM account_currency_balances");
    }

    static byte[] Purchase()
    {
        byte[] body = new byte[19]; body[2] = 1; BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), 103660); return body;
    }
}
