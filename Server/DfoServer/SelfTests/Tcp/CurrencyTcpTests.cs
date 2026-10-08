using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CurrencyTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "currency"), resources: resources);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var owners = new AccountOwners();
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, new NewCharacter("WalletTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            var inventory = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            current = InventoryService.Commit(repo, owner, current, inventory.Grant(current,
                [new(0, 20_000_000), new(1, 9), new(2, 10), new(3037, 88), new(10158124, 6), new(2681762, 22), new(2681917, 33)]));
            current = InventoryService.Commit(repo, owner, current, new CurrencyService(itemResources.GoldLimits).Grant(current, CurrencyKind.Cera, 11));
            repo.Create(owner, new("WalletTwin", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills });
        }
        await fixture.StartAsync();
        byte[] cera = Convert.FromHexString("010B0000001600000021000000");
        using (var peer = Open(fixture))
        {
            peer.Send(63);
            peer.Expect(0, 53, cera);
            var packets = peer.Select();
            Bytes(packets[8].Body, [0], "base gold cap stage initialized");
            Bytes(packets[9].Body, cera, "account wallet initialized on selection");
            var main = packets[4].Body;
            That(Balance(main, 0, 0) == 20_000_000 && Balance(main, 1, 1) == 9 && Balance(main, 2, 2) == 10 &&
                Balance(main, 358, 3037) == 88 && Balance(main, 365, 10158124) == 6, "TCP virtual quantities and epic soul mapping");
            byte[] upgrade = new byte[14]; upgrade[13] = 1;
            peer.Send(980, upgrade);
            peer.Expect(0, 950, [1]);
            peer.Expect(1, 980, [1, 1, 0, 0, 0, 0]);
            var gold = peer.Expect(0, 14).Body;
            That(gold.Length == 126 && gold[0] == 0 && U16(gold, 1) == 1 && U16(gold, 3) == 0 && U32(gold, 5) == 0 && U32(gold, 9) == 15_000_000,
                "successful cap upgrade sends committed gold-only delta");
            string before = string.Join(',', fixture.Query("SELECT revision,gold,gold_carry_limit,auction_gold_limit FROM characters WHERE slot=0")[0]);
            peer.Send(980, upgrade);
            peer.Expect(0, 950, [1]);
            peer.Barrier();
            That(string.Join(',', fixture.Query("SELECT revision,gold,gold_carry_limit,auction_gold_limit FROM characters WHERE slot=0")[0]) == before,
                "replayed stage cannot double-charge or send false success");
            peer.Send(63);
            peer.Expect(0, 53, cera);
            peer.Send(63, [0]);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var packets = peer.Select();
            Bytes(packets[8].Body, [1], "reconnect restores expanded cap stage");
            That(Balance(packets[4].Body, 0, 0) == 15_000_000, "reconnect restores exact charged gold");
        }
        using (var peer = Open(fixture))
        {
            var packets = peer.Select(1);
            Bytes(packets[8].Body, [0], "sibling keeps independent cap stage");
            Bytes(packets[9].Body, cera, "sibling shares account cash");
            That(Balance(packets[4].Body, 0, 0) == 0 && Balance(packets[4].Body, 1, 1) == 0 && Balance(packets[4].Body, 358, 3037) == 88,
                "sibling shares crystals but not gold or revival coins");
            byte[] upgrade = new byte[14]; upgrade[13] = 1;
            peer.Send(980, upgrade);
            peer.Expect(0, 950, [0]);
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "currency and item storage retain SQLite integrity");
    }
    static int Balance(byte[] main, ushort slot, int itemId)
    {
        int count = U16(main, 3);
        That(main.Length == 5 + count * 123, "independent virtual-item entry widths");
        for (int i = 0; i < count; i++)
        {
            int offset = 5 + i * 123;
            if (U16(main, offset) != slot) continue;
            That(U32(main, offset + 2) == itemId, "virtual item ID at slot " + slot);
            return checked((int)U32(main, offset + 6));
        }
        throw new InvalidOperationException("Missing virtual balance slot " + slot);
    }
}
