using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunPickupSlotsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "dungeon-pickup-slots"), resources: resources);
        await fixture.StartAsync();
        var items = resources.ItemResources.Items;
        var gear = InventoryRewardGrantService.Create(items.Get(101010912)) with { Value = 0 };
        var gem = InventoryRewardGrantService.Create(items.Get(90000), 2);
        var filler = InventoryRewardGrantService.Create(items.Get(90032));
        var consumable = InventoryRewardGrantService.Create(items.Get(1000), 3);
        ushort id = 0;
        string saved;
        using (var peer = Open(fixture))
        {
            peer.Create("PickupSlots");
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repository = new CharacterRepository(fixture.Database);
                var role = repository.LoadCharacter(owner, 0);
                var contents = role.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment && item.Position.Index != 12)
                    .Concat(Enumerable.Range(9, 56).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot), gear)))
                    .Concat(Enumerable.Range(49, 49).Select(slot => new InventoryItem(0, new(InventoryListType.GuildMedal, (short)slot), filler)))
                    .Concat(Enumerable.Range(3, 6).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot), slot == 4 ? gem : gear)))
                    .Append(new(0, new(InventoryListType.Equipment, 12), gear))
                    .Append(new(0, new(InventoryListType.Main, 65), consumable)).ToImmutableArray();
                InventoryService.Commit(repository, owner, role,
                    new(true, 0, new(role.Inventory, role.Inventory with { Items = contents }, "pickup-slot-fixture", new(role))));
            }
            peer.Select();
            id = peer.Id;
            EntryTown(peer);
            Enter(peer);
            Load(peer);
            peer.Barrier();

            uint ground = Discard(peer, InventoryListType.Equipment, 12, 1);
            string unchanged = Assets() + fixture.Snapshot("SELECT revision FROM characters WHERE id=$p0", id);
            peer.Send(43, PickupRequest(ground));
            peer.Expect(1, 43, [0, 22, 0]);
            peer.Barrier();
            That(Assets() + fixture.Snapshot("SELECT revision FROM characters WHERE id=$p0", id) == unchanged,
                "所属页和快捷栏都满时拒绝，不清地面对象、扣物品或推进资产版本");

            Discard(peer, InventoryListType.Main, 3, 1);
            Pickup(peer, ground, 3, gear);
            Pickup(peer, Discard(peer, InventoryListType.Main, 4, 2), 4, gem);
            Discard(peer, InventoryListType.Main, 5, 1);
            Pickup(peer, Discard(peer, InventoryListType.Main, 65, 3), 5, consumable);
            saved = Assets();
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            byte[] inventory = entry.Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            int count = U16(inventory, 3);
            That(inventory.Length == 5 + count * 123, "重登Main完整物品行保持独立123B合同");
            foreach (var (slot, core) in new (short Slot, ItemCore Core)[] { (3, gear), (4, gem), (5, consumable) })
            {
                int offset = Enumerable.Range(0, count).Select(index => 5 + index * 123).Single(at => U16(inventory, at) == slot);
                That(U32(inventory, offset + 2) == core.ItemId && U32(inventory, offset + 6) == core.Value,
                    "重登仍投影本次实际快捷槽及原装备品级或STK数量: " + slot);
            }
            peer.Barrier();
            That(Assets() == saved, "重连重读文件库后全部实际UID、槽位与完整Core保持，不重复拾取或重建物品");
        }
        Console.WriteLine($"PASS {Count - before} 拾取满页回退、消耗品优先、专用STK、拒绝后重试及重登TCP断言（未操作客户端）");

        uint Discard(TcpPeer peer, InventoryListType list, short slot, uint count)
        {
            byte[] request = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(request, 400);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), 250);
            request[4] = (byte)list;
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(5), checked((ushort)slot));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(7), count);
            peer.Send(47, request);
            peer.Expect(1, 47, [1, (byte)list, (byte)slot, (byte)(slot >> 8), (byte)count, 0, 0, 0]);
            byte[] notice = peer.Expect(0, 40).Body;
            That(notice.Length == 136 && U16(notice, 0) == peer.Id, "使用生产丢弃路径获得独立真实地面key，不注入网络拾取结果");
            var delta = peer.Expect(0, 14).Body;
            That(delta[0] == (byte)list && U16(delta, 3) == slot, "丢弃后只清实际源列表和槽位");
            if (list == InventoryListType.Equipment) peer.Expect(0, 2);
            peer.Barrier();
            return U32(notice, 6);
        }

        void Pickup(TcpPeer peer, uint ground, short slot, ItemCore core)
        {
            byte[] request = PickupRequest(ground);
            peer.Send(43, request);
            byte[] notice = peer.Expect(0, 39).Body;
            That(notice.Length == 19 && U32(notice, 0) == ground && U16(notice, 16) == slot, "拾取通知使用实际快捷目标槽");
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta.Length == 126 && delta[0] == 0 && U16(delta, 1) == 1 && U16(delta, 3) == slot &&
                U32(delta, 5) == core.ItemId && U32(delta, 9) == core.Value, "库存差量仍保留完整行、原品级或完整数量");
            peer.Expect(1, 43, [1]);
            peer.Barrier();
            var row = fixture.Query("""
                SELECT i.item_uid,i.item_core FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=$p1
                """, peer.Id, slot).Single();
            That(Convert.ToInt64(row[0]) > 0 && JsonSerializer.Deserialize<ItemCore>((string)row[1]) == core,
                "独立读库核对实际入包UID和完整核心，不以成功ACK代替保存证据");
            string old = Assets();
            peer.Send(43, request);
            peer.Expect(1, 43, [0, 19, 0]);
            peer.Barrier();
            That(Assets() == old, "同一地面key只入包一次");
        }

        static byte[] PickupRequest(uint ground)
        {
            byte[] request = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(request, ground);
            return request;
        }

        string Assets() => fixture.Snapshot("""
            SELECT s.list_type,s.slot_index,i.item_uid,i.item_core FROM character_inventory_slots s
            JOIN character_inventory_items i USING(item_uid) WHERE s.character_id=$p0 ORDER BY s.list_type,s.slot_index
            """, id);
    }
}
