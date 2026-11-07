using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildMedalTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-medals"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        long legacyUid;
        long cappedUid;
        string cappedCore;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("MedalTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills
            });
            var service = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            role = InventoryService.Commit(repo, owner, role, service.Grant(role, [new(100380044, 1), new(100380044, 1, 20), new(90000, 2), new(90001, 1), new(90032, 2),
                new(490011648, 100)]));
            var capped = role.Inventory.Find(new(InventoryListType.GuildMedal, 1))!;
            cappedUid = capped.Uid;
            cappedCore = JsonSerializer.Serialize(capped.Core);
            var coin = role.Inventory.Find(new(InventoryListType.Main, 177))!;
            legacyUid = coin.Uid;
            var legacy = coin with { Position = new(InventoryListType.Main, 121), Core = coin.Core with { Kind = ItemKind.Material } };
            InventoryService.Commit(repo, owner, role, new(true, 0, new(role.Inventory,
                role.Inventory with { Items = role.Inventory.Items.Replace(coin, legacy) }, "legacy-guild-coin-fixture", new(role))));
        }
        int cost = itemResources.MedalUpgrades.Rules.CoinCost(itemResources.Items.Get(100380044).Rarity, 1);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            var list = peer.SpecialContainers[3].Body;
            That(list.Length == 618 && list[0] == 38 && U16(list, 1) == 5, "nonempty medal/gem list has 123B entries");
            That(fixture.Query("SELECT i.item_uid,json_extract(i.item_core,'$.Kind'),json_extract(i.item_core,'$.Value') " +
                "FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=177", peer.Id)
                is [[long uid, long kind, 100L]] && uid == legacyUid && kind == (int)ItemKind.Quest,
                "选角通过真实库存事务归一旧材料硬币，保留原UID和100枚数量");
            peer.Send(19, Move(0, 177, 490011648, 0, 178, 0));
            peer.Expect(1, 19, MoveAck(0, 177, 0, 178)); peer.Expect(0, 13);
            peer.Send(19, Move(0, 178, 490011648, 0, 177, 490011648));
            peer.Expect(1, 19, MoveAck(0, 178, 0, 177)); peer.Expect(0, 14); peer.Expect(0, 13); peer.Barrier();
            That(fixture.Query("SELECT i.item_uid,json_extract(i.item_core,'$.Value') FROM character_inventory_items i " +
                "JOIN character_inventory_slots s USING(item_uid) WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=177", peer.Id)
                is [[long movedUid, 100L]] && movedUid == legacyUid, "任务页拆分再合堆保留原硬币UID和总数");
            string beforeCap = Snapshot();
            peer.Send(80, EnhancementTests.Request(0, 38, 1, 100380044, 177, []));
            peer.Expect(1, 80, [0, 23, 0]);
            peer.Barrier();
            That(Snapshot() == beforeCap, "真实CMD80拒绝强化20勋章，不扣公会硬币、不改UID或审计，连接继续可用");
            peer.Send(80, EnhancementTests.Request(0, 38, 0, 100380044, 177, []));
            var upgrade = peer.Expect(1, 80).Body;
            That(upgrade.Length == 27 && upgrade[0] == 1 && upgrade[1] == 0 && U16(upgrade, 2) == 177 &&
                U32(upgrade, 4) == 100 - cost && U16(upgrade, 8) == ushort.MaxValue && upgrade[10] == 0 && upgrade[11] == 0 &&
                upgrade[12] == 0 && upgrade[13] == 1 && upgrade[14] == 38 && U16(upgrade, 15) == 0 &&
                U16(upgrade, 17) == ushort.MaxValue && U16(upgrade, 19) == ushort.MaxValue && U32(upgrade, 21) == 0 &&
                upgrade[25] == 0 && upgrade[26] == 0, "CMD80勋章+0必成回包带回列表38槽位与公会硬币绝对余量");
            peer.Barrier();
            peer.Send(80, EnhancementTests.Request(0, 38, 0, 100380045, 177, []));
            peer.Expect(1, 80, [0, 23, 0]);
            peer.Barrier();
            peer.Send(19, Move(38, 1, 100380044, 0, 3, 0));
            peer.Expect(1, 19, MoveAck(38, 1, 0, 3));
            peer.Expect(0, 14); peer.Expect(0, 14);
            peer.Send(19, Move(0, 3, 100380044, 38, 1, 0));
            peer.Expect(1, 19, MoveAck(0, 3, 38, 1));
            peer.Expect(0, 14); peer.Expect(0, 14);
            peer.Send(19, Move(38, 0, 100380044, 3, 32, 0));
            peer.Expect(1, 19, MoveAck(38, 0, 3, 32));
            var sourceClear = peer.Expect(0, 14).Body;
            That(sourceClear[0] == 38 && U16(sourceClear, 3) == 0 && U32(sourceClear, 5) == uint.MaxValue,
                "medal ACK moves Client slot32 before committed source-container clear");
            peer.Expect(0, 2);
            That(peer.Expect(0, 2).Body[126] == 1, "Player retains worn medal without generic Equipment delta");
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
            fixture.Sql("CREATE TRIGGER fail_gem_tcp BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'gem failure'); END");
            peer.Send(831, Gem(51, 90032, 1));
            peer.Closed();
            That(Snapshot() == before, "gem SQL fault closes without success ACK and leaves all persisted assets intact");
            fixture.Sql("DROP TRIGGER fail_gem_tcp");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            That(entry[3].Body[126] == 1 && peer.SpecialContainers[3].Body.Length == 372, "reconnect restores worn medal and three remaining bag entries");
            peer.Send(831, Gem(51, 90032, 1));
            SuccessfulGem(peer, 2UL | 33UL << 16, 51, 1);
            peer.Send(19, Move(38, 48, 0, 3, 32, 100380044));
            peer.Expect(1, 19, MoveAck(38, 48, 3, 32));
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
        That(fixture.Query("SELECT json_extract(i.item_core,'$.Attr') FROM character_inventory_items i " +
            "JOIN character_inventory_slots s USING(item_uid) WHERE s.list_type=38 AND s.slot_index=48") is [[long attr]] && attr == 1 &&
            fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items " +
            "WHERE json_extract(item_core,'$.ItemId')=490011648") is [[long coins]] && coins == 100 - cost,
            "reconnect keeps committed medal upgrade level and guild coin deduction");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "medal TCP fixture remains consistent after rejection, faults and reconnect");
        That(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items WHERE item_uid=$p0", cappedUid)
            is [[long restoredUid, string restoredCore]] && restoredUid == cappedUid && restoredCore == cappedCore,
            "强化20勋章经快捷栏往返和重连仍保留同一UID及完整核心");

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters"));
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
