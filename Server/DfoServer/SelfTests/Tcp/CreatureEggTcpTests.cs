using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CreatureEggTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        CreatureEggTests.RunResources(resources.ItemResources.CreatureEggs, resources.ItemResources.Items);
        const int eggId = 63022, outputId = 63023;
        var detail = new CreatureDetail { Name = "保留", Satiety = 73, Experience = 55, Level = 2, TailFlag = 1, ExpireTime = int.MaxValue };
        var core = new ItemCore(eggId, ItemKind.Creature, 0)
        { Attr = 3, SealFlag = 0, Marker16 = 77, Extension27 = 123, SortLockFlag = 1, Random0 = new(1, 2, 3) };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "creature-egg"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        Character original;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("EggTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 25, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([
                    new(0, Pet(0), core) { Creature = detail },
                    new(0, Pet(1), core) { Creature = detail with { Name = "Second", Satiety = 100, Experience = 0, Level = 1, TailFlag = 0 } },
                    new(0, Pet(2), core with { ItemId = outputId }) { Creature = detail with { Name = "Adult" } }], Gold: 4321)
            });
            original = repository.List(owner).Single();
        }
        string originalUids = Uids(), originalDetails = Details();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (1, 713), (1, 713)]);
            string before = Snapshot();
            foreach (byte[] invalid in new byte[][] { [7, 2, 0], [7, 3, 0], [3, 26, 0], [7, 0, 1], [7, 0], [7, 0, 0, 0] })
            {
                peer.Send(102, invalid);
                peer.Expect(1, 102, [0, 0, 0]);
            }
            peer.Barrier();
            That(Snapshot() == before, "成宠、空槽、穿戴源和畸形102拒绝且资产、详情、版本不变，连接继续");
            fixture.Sql("CREATE TRIGGER fail_hatch_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='hatch_creature_egg' AND NEW.slot_index=1 BEGIN SELECT RAISE(ABORT,'hatch rollback'); END");
            peer.Send(102, [7, 1, 0]);
            peer.Closed();
            That(Snapshot() == before, "完整蛋核心、原详情及位置已写后审计失败整笔回滚，不先发送成功");
            fixture.Sql("DROP TRIGGER fail_hatch_late");
        }
        var savedRows = new Dictionary<short, byte[]>();
        byte[]? savedDetails = null;
        using (var peer = Open(fixture))
        {
            long beforeSelect = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            peer.Select(inventoryNotices: [(1, 713), (1, 713), (1, 713)]);
            byte[] full = peer.SpecialContainers.Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 7).Body;
            long afterSelect = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (short slot in new short[] { 0, 1, 2 })
            {
                uint remaining = U32(Row(full, slot), 22);
                That(remaining >= ItemExpiration.MaximumUnixTime - afterSelect &&
                    remaining <= ItemExpiration.MaximumUnixTime - beforeSelect,
                    "旧2038宠物实例在真实NOTI13中钳到2037截止，持久详情和UID不改写");
            }
            savedDetails = peer.SpecialContainers.Single(packet => packet is { Kind: 0, Opcode: 105 }).Body;
            foreach (short slot in new short[] { 1, 0 })
            {
                peer.Send(102, [7, (byte)slot, 0]);
                peer.Expect(1, 102, [1]);
                var clear = peer.Expect(0, 14).Body;
                var empty = new byte[123];
                BinaryPrimitives.WriteInt16LittleEndian(empty, slot);
                Array.Fill(empty, (byte)255, 2, 4);
                Bytes(Row(clear, slot), empty, "ACK102后先清原蛋模板，不把宠物UID当消耗品数量减一");
                peer.Expect(0, 105, savedDetails);
                var changed = Row(peer.Expect(0, 14).Body, slot);
                var expected = Row(full, slot);
                BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(2), outputId);
                expected[13] = 0;
                CompareExceptRemaining(changed, expected, "105保留原详情后写同UID产物，123B只改模板/宠物类型及实时剩余秒");
                That(U32(changed, 6) == original.Inventory.Find(Pet(slot))!.Uid && U32(changed, 22) > 0,
                    "实际持久UID及2038实例期限保留，无新UID或永久期限替换");
                savedRows[slot] = changed;
                peer.Expect(1, 713, [1, 7, (byte)slot, 0]);
                peer.Barrier();
                string before = Snapshot();
                peer.Send(102, [7, (byte)slot, 0]);
                peer.Expect(1, 102, [0, 0, 0]);
                peer.Barrier();
                That(Snapshot() == before, "已孵化源再次请求拒绝，不重建详情、再分配UID或额外改版本");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (1, 713), (1, 713)]);
            Bytes(peer.SpecialContainers.Single(packet => packet.Opcode == 105).Body, savedDetails!, "重连完整恢复原UID的同一宠物详情");
            byte[] full = peer.SpecialContainers.Single(packet => packet.Opcode == 13 && packet.Body[0] == 7).Body;
            foreach (var (slot, saved) in savedRows)
                CompareExceptRemaining(Row(full, slot), saved, "重连恢复孵化后的同槽完整产物核心，有限剩余秒继续递减");
            peer.Barrier();
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = repository.List(owner).Single();
            foreach (short slot in new short[] { 0, 1 })
            {
                var previous = original.Inventory.Find(Pet(slot))!;
                That(saved.Inventory.Find(Pet(slot)) == previous with { Core = previous.Core with { ItemId = outputId, SealFlag = 0 } },
                    "重开仓储保留原UID、核心和完整详情，原2038期限不按无期限PVF目标重置");
            }
            That(saved.Inventory.Find(Pet(2)) == original.Inventory.Find(Pet(2)) && saved.Inventory.Gold == original.Inventory.Gold &&
                saved.Inventory.AccountWallet.Same(original.Inventory.AccountWallet), "其它宠物、金币和账号钱包保持原事实");
        }
        That(Uids() == originalUids && Details() == originalDetails &&
            fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='hatch_creature_egg'") is [[2L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "两笔实际孵化各有一笔审计，所有原UID/详情及位置保留，无孤儿或外键损坏");

        string Uids() => fixture.Snapshot("SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid");
        string Details() => fixture.Snapshot("SELECT * FROM inventory_creature_details ORDER BY item_uid");
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") + Uids() + Details() +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") + fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
        static InventorySlot Pet(short slot) => new(InventoryListType.Pet, slot);
        static byte[] Row(byte[] body, short slot)
        {
            That(body[0] == 7 && body.Length == 3 + U16(body, 1) * 123, "Pet全量/差量都是3B头与完整123B条目，没有时装RAW附段");
            int at = Enumerable.Range(0, U16(body, 1)).Select(index => 3 + index * 123).Single(at => U16(body, at) == slot);
            return body.AsSpan(at, 123).ToArray();
        }
        static void CompareExceptRemaining(byte[] actual, byte[] expected, string message)
        {
            That(U32(actual, 22) > 0 && U32(actual, 22) <= U32(expected, 22), "宠物Marker16持续投影原有限期限剩余秒");
            expected = (byte[])expected.Clone();
            actual.AsSpan(22, 4).CopyTo(expected.AsSpan(22, 4));
            Bytes(actual, expected, message);
        }
    }
}
