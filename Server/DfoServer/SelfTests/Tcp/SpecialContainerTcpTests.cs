using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class SpecialContainerTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        var characters = resources.Characters;
        var towns = resources.Towns;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "special-containers"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("SpecialTcp", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills });
            var service = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            InventoryService.Commit(repo, owner, role, service.Grant(role, [new(101550507, 2), new(63000, 1), new(2680000, 1), new(24, 20)]));
        }
        string identities = JsonSerializer.Serialize(fixture.Query("SELECT item_uid,json_remove(item_core,'$.SealFlag') FROM character_inventory_items ORDER BY item_uid"));
        byte[]? wornAvatar = null, wornPet = null;
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            var details = peer.SpecialContainers[0].Body;
            That(details.Length == 17 && details[0] == 1 && details[5] == 100 && details[11] == 1, "default pet detail arrives before Player with full fields");
            int petUid = checked((int)U32(details, 1));
            That(peer.SpecialContainers[1].Body.Length == 335 && U16(peer.SpecialContainers[1].Body, 3) == 2 &&
                peer.SpecialContainers[2].Body.Length == 372, "both dedicated lists initialized including artifacts and feed");
            peer.Send(19, Move(1, 0, 101550507, 3, 0, 0));
            peer.Expect(1, 19, Ack(1, 0, 3, 0));
            var clear = peer.Expect(0, 14).Body;
            That(clear.Length == 168 && clear[0] == 1 && U32(clear, 5) == uint.MaxValue && U32(clear, 126) == 30, "avatar unequipped-list clear keeps RAW suffix");
            wornAvatar = peer.Expect(0, 14).Body;
            That(wornAvatar.Length == 168 && wornAvatar[0] == 3 && U32(wornAvatar, 5) == 101550507, "equipped avatar delta includes socket and dye blocks");
            peer.Expect(0, 2); peer.Expect(0, 2);
            peer.Send(19, Move(7, 0, 63000, 3, 26, 0));
            peer.Expect(1, 19, Ack(7, 0, 3, 26));
            peer.Expect(0, 105, details);
            wornPet = peer.Expect(0, 14).Body;
            That(wornPet.Length == 126 && wornPet[0] == 3 && U32(wornPet, 9) == petUid, "pet equip sends linked identity before Player");
            peer.Expect(0, 14); peer.Expect(0, 2); peer.Expect(0, 2);
            peer.Send(19, Move(7, 140, 2680000, 3, 28, 0));
            peer.Expect(1, 19, Ack(7, 140, 3, 28));
            That(peer.Expect(0, 14).Body.Length == 126, "pet artifact uses ordinary core at Client slot28");
            peer.Expect(0, 14); peer.Expect(0, 2); peer.Expect(0, 2);
            peer.Barrier();
            That(JsonSerializer.Serialize(fixture.Query("SELECT item_uid,json_remove(item_core,'$.SealFlag') FROM character_inventory_items ORDER BY item_uid")) == identities,
                "TCP equip preserves UID and core fields other than artifact unsealing");
        }
        using (var peer = Open(fixture))
        {
            var packets = peer.Select();
            That(peer.SpecialContainers[0].Body[0] == 1 && U16(peer.SpecialContainers[1].Body, 3) == 1 && U16(peer.SpecialContainers[2].Body, 1) == 1,
                "reconnect retains worn special items without duplicating bag entries");
            That(packets[3].Body[126] == 3, "reconnect Player includes avatar, pet and pet equipment together");
            peer.Send(19, Move(1, 2, 0, 3, 0, 101550507));
            peer.Expect(1, 19, Ack(1, 2, 3, 0));
            var avatar = peer.Expect(0, 14).Body;
            Bytes(avatar.AsSpan(5), wornAvatar!.AsSpan(5), "reverse unequip keeps avatar extra data");
            That(peer.Expect(0, 14).Body.Length == 126, "empty equipped avatar slot has ordinary clear width");
            peer.Expect(0, 2); peer.Expect(0, 2);
            peer.Send(19, Move(7, 5, 0, 3, 26, 63000));
            peer.Expect(1, 19, Ack(7, 5, 3, 26));
            peer.Expect(0, 105); peer.Expect(0, 14);
            var pet = peer.Expect(0, 14).Body;
            Bytes(pet.AsSpan(5), wornPet!.AsSpan(5), "reverse unequip retains complete pet core");
            peer.Expect(0, 2); peer.Expect(0, 2);
            peer.Barrier();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_special_tcp BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'special failure'); END");
            peer.Send(19, Move(1, 2, 101550507, 3, 0, 0));
            peer.Closed();
            That(Snapshot() == before, "late TCP failure publishes no success and rolls back avatar core, detail and position");
            fixture.Sql("DROP TRIGGER fail_special_tcp");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(U16(peer.SpecialContainers[1].Body, 3) == 2 && U16(peer.SpecialContainers[2].Body, 1) == 2, "failed move remains rolled back after reconnect");
            peer.Barrier();
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "special-container TCP fixture has no orphan details or foreign-key violations");

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM inventory_avatar_details ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM inventory_creature_details ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters"));
    }

    static byte[] Move(byte fromList, short fromSlot, int fromId, byte toList, short toSlot, int toId) => Build(writer =>
    {
        writer.Write(fromList); writer.Write(fromSlot); writer.Write(fromId); writer.Write(1);
        writer.Write(toList); writer.Write(toSlot); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
    });
    static byte[] Ack(byte fromList, short fromSlot, byte toList, short toSlot) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(fromList); writer.Write(fromSlot); writer.Write(1); writer.Write(toList); writer.Write(toSlot);
    });
}
