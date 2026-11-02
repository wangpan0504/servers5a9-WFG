using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.EpicWish;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EpicWishTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        EpicWishTests.RunResources(resources.ItemResources);
        var catalog = new EpicWishCatalog(resources.ItemResources.EpicPieces, resources.ItemResources.Items);
        int[] equipment = resources.ItemResources.EpicPieces.Entries.Select(row => row.EquipmentId).Where(catalog.Contains).Take(2).ToArray();
        var wishes = new EpicWishList([equipment[0], 0, equipment[1], 0, 0]);
        var cleared = new EpicWishList([0, 0, 0, 0, 0]);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "epic-wish"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("WishTcp", resources.Towns.Room, resources.Towns.InitialReturn)
                { AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
            repository.Create(owner, new("WishSibling", resources.Towns.Room, resources.Towns.InitialReturn)
                { AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(1354, Body(wishes));
            peer.Expect(1, 1354, [0, 0, 0]);
            Initialize(peer, EpicWishList.Empty);
            string assets = Assets();
            Save(peer, wishes);
            Save(peer, wishes);
            That(Assets() == assets, "TCP duplicate wish saves preserve revisions, currencies and inventory");
            foreach (byte[] invalid in new byte[][] { [1], [0, 0], [1, 255, 255, 255, 255],
                [2, 1, 0, 0, 0, 1, 0, 0, 0], [1, 255, 255, 255, 127] })
            {
                peer.Send(1354, invalid);
                peer.Expect(1, 1354, [0, 0, 0]);
                peer.Barrier();
            }
            That(Assets() == assets && Stored() == JsonSerializer.Serialize(wishes.Items),
                "TCP malformed or unknown wish never overwrites saved list or assets");
        }
        using (var peer = Open(fixture))
        {
            Initialize(peer, EpicWishList.Empty, 1);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            Initialize(peer, wishes);
            string assets = Assets();
            fixture.Sql("CREATE TRIGGER fail_wish_tcp BEFORE UPDATE ON character_epic_wishes BEGIN SELECT RAISE(ABORT,'wish failure'); END");
            peer.Send(1354, Body(cleared));
            peer.Closed();
            That(Stored() == JsonSerializer.Serialize(wishes.Items) && Assets() == assets,
                "TCP SQL failure closes without success and preserves committed wish and assets");
            fixture.Sql("DROP TRIGGER fail_wish_tcp");
        }
        using (var peer = Open(fixture))
        {
            Initialize(peer, wishes);
            Save(peer, cleared);
        }
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Initialize(peer, cleared);
            Save(peer, EpicWishList.Empty);
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "TCP wish save, restart and clear preserve database integrity");

        void Initialize(TcpPeer peer, EpicWishList expected, ushort slot = 0)
        {
            peer.Select(slot);
            Bytes(peer.EpicWishInformation, Body(expected), "login restores only selected character wish slots including zero holes");
        }
        void Save(TcpPeer peer, EpicWishList expected)
        {
            peer.Send(1354, Body(expected));
            peer.Expect(0, 1292, Body(expected));
            peer.Expect(1, 1354, [1]);
            peer.Barrier();
        }
        string Stored() => (string)fixture.Query("SELECT item_ids FROM character_epic_wishes ORDER BY character_id")[0][0];
        string Assets() => JsonSerializer.Serialize(fixture.Query("SELECT * FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_wallets ORDER BY account")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind"));
    }

    static byte[] Body(EpicWishList wishes) => Build(writer =>
    {
        writer.Write((byte)wishes.Items.Length);
        foreach (int item in wishes.Items) writer.Write(item);
    });
}
