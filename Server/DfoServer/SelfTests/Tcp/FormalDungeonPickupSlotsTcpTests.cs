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
        var consumable = InventoryRewardGrantService.Create(items.Get(1013), 3);
        var titleTemplate = items.Get(26059);
        That(titleTemplate.EquipmentSlot == 13 && titleTemplate.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) &&
            !titleTemplate.CannotBeCloneTitle, "来源回归使用真实可封装且允许借用动画的26059称号");
        var titleCore = InventoryRewardGrantService.Create(titleTemplate) with
            { SealFlag = 1, ExpireTime = checked((int)DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()) };
        var medal = InventoryRewardGrantService.Create(items.Get(100380045)) with { Attr = 3, GuardianGemKeys = 2, Extension97 = 77 };
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
                    .Concat(Enumerable.Range(1, 48).Select(slot => new InventoryItem(0, new(InventoryListType.GuildMedal, (short)slot), medal)))
                    .Concat(Enumerable.Range(3, 6).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot),
                        slot == 4 ? gem : slot == 5 ? consumable : slot == 6 ? titleCore : gear)))
                    .Append(new(0, new(InventoryListType.Equipment, 12), gear))
                    .Append(new(0, new(InventoryListType.Equipment, 32), medal))
                    .Concat(Enumerable.Range(65, 56).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot),
                        InventoryRewardGrantService.Create(items.Get(1000), 1)))).ToImmutableArray();
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
            uint droppedMedal = Discard(peer, InventoryListType.Equipment, 32, 1);
            Pickup(peer, droppedMedal, 0, medal, InventoryListType.GuildMedal);
            uint food = Discard(peer, InventoryListType.Main, 5, 3);
            string fullBag = Assets() + fixture.Snapshot("SELECT revision FROM characters WHERE id=$p0", id);
            peer.Send(43, PickupRequest(food));
            peer.Expect(1, 43, [0, 22, 0]);
            peer.Barrier();
            That(Assets() + fixture.Snapshot("SELECT revision FROM characters WHERE id=$p0", id) == fullBag,
                "肉干1013没有兼容堆且消耗品页满，空快捷槽也不能绕容量或消费地面");
            Discard(peer, InventoryListType.Main, 65, 1);
            Pickup(peer, food, 65, consumable);
            peer.Send(568, Build(writer => writer.Write(26059u)));
            peer.Expect(1, 568, Build(writer => { writer.Write((byte)1); writer.Write(26059u); }));
            TitleAppearance(peer, 26059);
            peer.Barrier();
            long titleSourceUid = Convert.ToInt64(fixture.Query(
                "SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=6", id).Single()[0]);
            uint titleGround = Discard(peer, InventoryListType.Main, 6, 1, cloneChanged: true);
            That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", id) is [[0L]],
                "真实CMD47清最后动画来源时同事务清零持久选择并刷新本人外观");
            // 装备页仍满；前序消耗品从快捷5回到所属页65，回捡按快捷升序首空落5，不记忆原槽6。
            Pickup(peer, titleGround, 5, titleCore);
            That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", id) is [[0L]] &&
                fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=6", id).Length == 0 &&
                fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=5", id)
                    is [[long pickedUid]] && pickedUid != titleSourceUid,
                "同款称号回捡保留完整核心但整件重新分配UID，原槽6空、实际落5且不恢复已解除的动画选择");
            saved = Assets();
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            byte[] inventory = entry.Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            int count = U16(inventory, 3);
            That(inventory.Length == 5 + count * 123, "重登Main完整物品行保持独立123B合同");
            foreach (var (slot, core) in new (short Slot, ItemCore Core)[] { (3, gear), (4, gem), (5, titleCore), (65, consumable) })
            {
                int offset = Enumerable.Range(0, count).Select(index => 5 + index * 123).Single(at => U16(inventory, at) == slot);
                That(U32(inventory, offset + 2) == core.ItemId && U32(inventory, offset + 6) == core.Value,
                    "重登仍投影本次实际目标槽及原装备品级或STK数量: " + slot);
            }
            That(!Enumerable.Range(0, count).Any(index => U16(inventory, 5 + index * 123) == 6) &&
                fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", id) is [[0L]] &&
                U32(peer.PlayerBody, 127 + peer.PlayerBody[126] * 88) == 0,
                "重登Main不恢复原快捷6，持久化和本人Player均不恢复已清除的称号动画");
            peer.Barrier();
            That(Assets() == saved, "重连重读文件库后全部实际UID、槽位与完整Core保持，不重复拾取或重建物品");
        }
        Console.WriteLine($"PASS {Count - before} 拾取满页回退、消耗品入背包、专用STK、拒绝后重试及重登TCP断言（未操作客户端）");

        uint Discard(TcpPeer peer, InventoryListType list, short slot, uint count, bool cloneChanged = false)
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
            if (cloneChanged) TitleAppearance(peer, 0);
            else if (list == InventoryListType.Equipment) peer.Expect(0, 2);
            peer.Barrier();
            return U32(notice, 6);
        }

        static void TitleAppearance(TcpPeer peer, uint expected)
        {
            byte[] actor = peer.Expect(0, 2).Body;
            int count = checked(133 + (int)U32(actor, 123));
            That(actor[0] == 0 && actor[count - 1] == 1 && U32(actor, count + 1 + actor[count] * 25) == expected,
                "来源变化使用既有Actor动画字段，保留正式副本场景状态");
            byte[] player = peer.Expect(0, 2).Body;
            That(player[0] == 1 && U32(player, 127 + player[126] * 88) == expected,
                "随后本人Player使用同一已提交动画值，不依赖重新进入副本");
        }

        void Pickup(TcpPeer peer, uint ground, short slot, ItemCore core, InventoryListType list = InventoryListType.Main)
        {
            byte[] request = PickupRequest(ground);
            peer.Send(43, request);
            byte[] notice = peer.Expect(0, 39).Body;
            That(notice.Length == 19 && U32(notice, 0) == ground && U16(notice, 16) == slot, "拾取通知使用实际目标槽");
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta.Length == 126 && delta[0] == (byte)list && U16(delta, 1) == 1 && U16(delta, 3) == slot &&
                U32(delta, 5) == core.ItemId && U32(delta, 9) == core.Value, "库存差量仍保留完整行、原品级或完整数量");
            peer.Expect(1, 43, [1]);
            peer.Barrier();
            var row = fixture.Query("""
                SELECT i.item_uid,i.item_core FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                WHERE s.character_id=$p0 AND s.list_type=$p1 AND s.slot_index=$p2
                """, peer.Id, (byte)list, slot).Single();
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
