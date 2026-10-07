using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildMedalTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        Inventory.GuildMedalTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-medals"));
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("MedalTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills
            });
            var service = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            InventoryService.Commit(repo, owner, role, service.Grant(role, [new(100380044, 2), new(90000, 2), new(90001, 1), new(90032, 2)]));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            var list = peer.SpecialContainers[3].Body;
            That(list.Length == 618 && list[0] == 38 && U16(list, 1) == 5, "nonempty medal/gem list has 123B entries");
            peer.Send(19, Move(38, 1, 100380044, 0, 3, 0));
            peer.Expect(1, 19, MoveAck(38, 1, 0, 3));
            peer.Expect(0, 14); peer.Expect(0, 14);
            peer.Send(19, Move(0, 3, 100380044, 38, 1, 0));
            peer.Expect(1, 19, MoveAck(0, 3, 38, 1));
            peer.Expect(0, 14); peer.Expect(0, 14);
            peer.Send(19, Move(38, 0, 100380044, 3, 32, 0));
            peer.Expect(1, 19, MoveAck(38, 0, 3, 32));
            var equipment = peer.Expect(0, 14).Body;
            That(equipment[0] == 3 && U16(equipment, 3) == 32 && U32(equipment, 5) == 100380044 && equipment.Length == 126,
                "medal equip uses Client slot32 and ordinary full core width");
            peer.Expect(0, 14); peer.Expect(0, 2); peer.Expect(0, 2);
            peer.Send(831, Gem(49, 90000, 0));
            SuccessfulGem(peer, 1, 49, 1);
            peer.Send(831, Gem(50, 90001, 1));
            peer.Expect(1, 831, [0, 0x12, 0]);
            peer.Barrier();
            peer.Send(831, Gem(50, 90001, 0));
            SuccessfulGem(peer, 2, 50, null);
            peer.Send(831, Gem(49, 90000, 4));
            peer.Expect(1, 831, [0, 4, 0]);
            peer.Barrier();
            string before = Snapshot();
            Sql("CREATE TRIGGER fail_gem_tcp BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'gem failure'); END");
            peer.Send(831, Gem(51, 90032, 1));
            peer.Closed();
            That(Snapshot() == before, "gem SQL fault closes without success ACK and leaves all persisted assets intact");
            Sql("DROP TRIGGER fail_gem_tcp");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restarted");
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            That(entry[3].Body[126] == 1 && peer.SpecialContainers[3].Body.Length == 372, "restart restores worn medal and three remaining bag entries");
            peer.Send(831, Gem(51, 90032, 1));
            SuccessfulGem(peer, 2UL | 33UL << 16, 51, 1);
            peer.Send(19, Move(38, 48, 0, 3, 32, 100380044));
            peer.Expect(1, 19, MoveAck(38, 48, 3, 32));
            var clear = peer.Expect(0, 14).Body;
            That(clear[0] == 3 && U32(clear, 5) == uint.MaxValue, "reverse unequip clears body slot32");
            var returned = peer.Expect(0, 14).Body;
            That(returned[0] == 38 && U16(returned, 3) == 48 && BinaryPrimitives.ReadUInt64LittleEndian(returned.AsSpan(104)) == (2UL | 33UL << 16),
                "unequip retains all embedded gems at end of medal page");
            peer.Expect(0, 2); peer.Expect(0, 2); peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(831, new byte[10]);
            peer.Closed();
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "medal TCP fixture remains consistent after rejection, faults and restart");

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters"));
        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }

    static void SuccessfulGem(TcpPeer peer, ulong keys, short material, int? remaining)
    {
        peer.Expect(1, 831, [1]);
        var medal = peer.Expect(0, 14).Body;
        That(medal[0] == 3 && U16(medal, 3) == 32 && BinaryPrimitives.ReadUInt64LittleEndian(medal.AsSpan(104)) == keys,
            "gem ACK followed by committed equipment keys");
        var count = peer.Expect(0, 14).Body;
        That(count[0] == 38 && U16(count, 3) == material && (remaining is null ? U32(count, 5) == uint.MaxValue : U32(count, 9) == remaining),
            "gem material delta is committed absolute count or clear");
        peer.Expect(0, 2); peer.Expect(0, 2); peer.Barrier();
    }
    static byte[] Gem(short slot, int id, byte socket) => Build(writer =>
    {
        writer.Write(100380044); writer.Write(slot); writer.Write(id); writer.Write(socket);
    });
    static byte[] Move(byte fromList, short fromSlot, int fromId, byte toList, short toSlot, int toId) => Build(writer =>
    {
        writer.Write(fromList); writer.Write(fromSlot); writer.Write(fromId); writer.Write(1);
        writer.Write(toList); writer.Write(toSlot); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
    });
    static byte[] MoveAck(byte fromList, short fromSlot, byte toList, short toSlot) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(fromList); writer.Write(fromSlot); writer.Write(1); writer.Write(toList); writer.Write(toSlot);
    });
}
