using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CollectBoxTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        var box = itemResources.CollectBoxes.Entries.FirstOrDefault(entry =>
            entry.BaseExpiration is { } baseline &&
            (entry.MaximumExpiration is null || entry.MaximumExpiration > baseline) &&
            entry.Slots.Any(slot => slot.ItemId > 0 &&
                entry.Extensions.Any(extension => extension.ItemId != slot.ItemId)))
            ?? throw new InvalidDataException("当前PVF没有可执行收集与续期TCP回归的宝珠箱");
        var slot = box.Slots.First(row => row.ItemId > 0 &&
            box.Extensions.Any(extension => extension.ItemId != row.ItemId));
        var extension = box.Extensions.First(row => row.ItemId != slot.ItemId);
        var collectedTemplate = itemResources.Items.Get(slot.ItemId);
        var extensionTemplate = itemResources.Items.Get(extension.ItemId);
        var spring = itemResources.CollectBoxes.Get(8);
        var springSlot = spring.Slots.Single(row => row.PhysicalSlot == 0 && row.ItemId == 490705309 && row.Metadata == 6);
        var springTemplate = itemResources.Items.Get(springSlot.ItemId);
        long springHistoricalExpiry = spring.MaximumExpiration!.Value - 1;
        var now = DateTimeOffset.FromUnixTimeSeconds(Math.Max(1, box.BaseExpiration!.Value - 3600));
        var clock = new FixedClock(now);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "collectbox"), resources: resources, clock: clock);

        var draft = new Character(1, 0, "CollectTcpDraft", 0, resources.Towns.Room, resources.Towns.InitialReturn);
        draft = Grant(draft, collectedTemplate, Math.Min(2, collectedTemplate.StackLimit), now, itemResources.EpicPieces);
        draft = Grant(draft, extensionTemplate, extension.RequiredCount, now, itemResources.EpicPieces);
        draft = Grant(draft, springTemplate, Math.Min(2, springTemplate.StackLimit), now, itemResources.EpicPieces);
        var repository = new CharacterRepository(fixture.Database);
        Character created;
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            created = repository.Create(owner, new("CollectTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Inventory = draft.Inventory,
                AcceptedStoryLevel = 1
            });
        fixture.Sql("INSERT INTO character_collectbox_expirations(character_id,account,box_index,expires_at) VALUES($p0,$p1,$p2,$p3)",
            created.Id, fixture.Options.DevelopmentAccount, spring.Index, springHistoricalExpiry);
        var springItem = created.Inventory.Items.Single(item => item.Core.ItemId == springSlot.ItemId);
        ushort springSourceSlot = checked((ushort)springItem.Position.Index);
        int springCount = springItem.Core.Count;
        var sourceItem = created.Inventory.Items.Single(item => item.Core.ItemId == slot.ItemId);
        var ticket = created.Inventory.Items.Single(item => item.Core.ItemId == extension.ItemId);
        ushort sourceSlot = checked((ushort)sourceItem.Position.Index);
        ushort ticketSlot = checked((ushort)ticket.Position.Index);
        int sourceCount = sourceItem.Core.Count;
        long expectedExpiry = Math.Min(box.BaseExpiration.Value + 86400, box.MaximumExpiration ?? long.MaxValue);
        long historicalDeadline = box.MaximumExpiration ?? box.BaseExpiration.Value;

        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(1023, [box.Index]);
            peer.Expect(1, 1023, [0, 0x5F, 0]);
            peer.Select();
            var initial = Box(peer, box.Index);
            That(U32(initial.Body, 2) == 3600 && initial.Body[7] == 0,
                "进城按PVF基础期限初始化全部空箱");

            peer.Send(1024, Add(box.Index, sourceSlot, slot.ItemId, reserved: 1));
            peer.Expect(1, 1024, [0, 0x12, 0]);
            peer.Send(1023, [box.Index]);
            peer.Expect(1, 1023, [box.Index, box.Index]);
            That(fixture.Query("SELECT selected_box FROM character_collectbox_state WHERE character_id=$p0", created.Id)
                is [[long selected]] && selected == box.Index, "SELECT提交角色级选中箱");

            clock.Now = DateTimeOffset.FromUnixTimeSeconds(checked(historicalDeadline + 1));
            peer.ExpectUnusedDailyBoosterReset();
            peer.Send(1024, Add(box.Index, sourceSlot, slot.ItemId));
            Delta(peer, sourceSlot, slot.ItemId, sourceCount - 1);
            var state = peer.Expect(0, 1030);
            ValidateState(state, box.Index, slot.ItemId, selected: true);
            That(U32(state.Body, 2) == uint.MaxValue, "历史活动期限保持过期显示但不阻断宝珠放入");
            peer.Expect(1, 1024, [1]);
            // 收集箱请求尾部完成后才回拨，已公布的新服务日不因回拨再次清零。
            peer.Barrier();
            clock.Now = now;
            peer.Send(1024, Add(box.Index, sourceSlot, slot.ItemId));
            peer.Expect(1, 1024, [0, 0x17, 0]);
            peer.Send(1025, Remove(box.Index, int.MaxValue));
            peer.Expect(1, 1025, [0, 4, 0]);
            peer.Barrier();

            peer.Send(1025, Remove(box.Index, slot.ItemId));
            Delta(peer, sourceSlot, slot.ItemId, sourceCount);
            state = peer.Expect(0, 1030);
            That(state.Body[7] == 0, "REMOVE通知清除已收藏模板");
            peer.Expect(1, 1025, [1]);

            peer.Send(1024, Add(box.Index, sourceSlot, slot.ItemId));
            Delta(peer, sourceSlot, slot.ItemId, sourceCount - 1);
            ValidateState(peer.Expect(0, 1030), box.Index, slot.ItemId, selected: true);
            peer.Expect(1, 1024, [1]);

            peer.Send(1026, Add(box.Index, ticketSlot, extension.ItemId));
            Delta(peer, ticketSlot, -1, 0);
            state = peer.Expect(0, 1030);
            ValidateState(state, box.Index, slot.ItemId, selected: true);
            That(U32(state.Body, 2) == expectedExpiry - now.ToUnixTimeSeconds(),
                "EXTEND通知使用提交后的绝对期限换算剩余秒");
            peer.Expect(1, 1026, Build(writer =>
            {
                writer.Write((byte)1);
                writer.Write(box.Index);
                writer.Write(extension.ItemId);
            }));
            peer.Send(1026, [box.Index, 0]);
            peer.Expect(1, 1026, [0, 0x12, 0]);
            peer.Barrier();
        }

        using (var peer = Open(fixture))
        {
            peer.Select();
            var restored = Box(peer, box.Index);
            ValidateState(restored, box.Index, slot.ItemId, selected: true);
            That(U32(restored.Body, 2) == expectedExpiry - now.ToUnixTimeSeconds() &&
                fixture.Query("SELECT expires_at FROM character_collectbox_expirations WHERE character_id=$p0 AND box_index=$p1",
                    created.Id, box.Index) is [[long expiry]] && expiry == expectedExpiry,
                "重连恢复选择、收藏模板和v52期限");

            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_collectbox_tcp BEFORE INSERT ON character_collectbox_state BEGIN SELECT RAISE(ABORT,'collectbox tcp rollback'); END");
            peer.Send(1025, Remove(box.Index, slot.ItemId));
            peer.Closed();
            That(Snapshot() == before, "晚期收集箱写失败不先成功，背包、审计、revision与模板槽整笔回滚");
            fixture.Sql("DROP TRIGGER fail_collectbox_tcp");
        }

        using (var peer = Open(fixture))
        {
            peer.Select();
            ValidateState(Box(peer, box.Index), box.Index, slot.ItemId, selected: true);
            peer.Send(1025, Remove(box.Index, slot.ItemId));
            Delta(peer, sourceSlot, slot.ItemId, sourceCount);
            var state = peer.Expect(0, 1030);
            That(state.Body[7] == 0 && U32(state.Body, 2) == expectedExpiry - now.ToUnixTimeSeconds(),
                "失败后重连仍可取出，期限与模板槽独立");
            peer.Expect(1, 1025, [1]);
            peer.Barrier();
        }
        // 同一角色和数据库保留原续期箱覆盖，再核历史过期的原2020春节九珠箱。
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(Math.Max(now.ToUnixTimeSeconds(), spring.MaximumExpiration.Value + 1));
        byte[] springReplay;
        using (var peer = Open(fixture))
        {
            peer.Select();
            var initialSpring = Box(peer, spring.Index);
            That(initialSpring.Body.Length == 8 && initialSpring.Body[1] == 0 && initialSpring.Body[6] == 1 &&
                initialSpring.Body[7] == 0 && U32(initialSpring.Body, 2) == 0 &&
                fixture.Query("SELECT expires_at FROM character_collectbox_expirations WHERE character_id=$p0 AND box_index=$p1",
                    created.Id, spring.Index) is [[long savedSpringExpiry]] && savedSpringExpiry == springHistoricalExpiry,
                "九珠历史过期库进城仍发永久位1剩余0，历史记录不被重写");

            peer.Send(1024, Add(spring.Index, springSourceSlot, springSlot.ItemId));
            Delta(peer, springSourceSlot, springCount == 1 ? -1 : springSlot.ItemId, springCount - 1);
            ValidateState(peer.Expect(0, 1030), spring.Index, springSlot.ItemId, selected: false, permanent: true);
            peer.Expect(1, 1024, [1]);
            peer.Send(1025, Remove(spring.Index, springSlot.ItemId));
            Delta(peer, springSourceSlot, springSlot.ItemId, springCount);
            var removedSpring = peer.Expect(0, 1030);
            That(removedSpring.Body.Length == 8 && removedSpring.Body[7] == 0 && removedSpring.Body[6] == 1 &&
                U32(removedSpring.Body, 2) == 0, "九珠取下发布空列表并保持常驻期限");
            peer.Expect(1, 1025, [1]);
            peer.Send(1024, Add(spring.Index, springSourceSlot, springSlot.ItemId));
            Delta(peer, springSourceSlot, springCount == 1 ? -1 : springSlot.ItemId, springCount - 1);
            var readdedSpring = peer.Expect(0, 1030);
            ValidateState(readdedSpring, spring.Index, springSlot.ItemId, selected: false, permanent: true);
            springReplay = readdedSpring.Body;
            peer.Expect(1, 1024, [1]);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Bytes(Box(peer, spring.Index).Body, springReplay, "九珠正常重连从真实库回放相同永久位和珠列表");
            peer.Send(1025, Remove(spring.Index, springSlot.ItemId));
            Delta(peer, springSourceSlot, springSlot.ItemId, springCount);
            var removedSpring = peer.Expect(0, 1030);
            That(removedSpring.Body[7] == 0 && removedSpring.Body[6] == 1 && U32(removedSpring.Body, 2) == 0 &&
                fixture.Query("SELECT expires_at FROM character_collectbox_expirations WHERE character_id=$p0 AND box_index=$p1",
                    created.Id, spring.Index) is [[long savedSpringExpiry]] && savedSpringExpiry == springHistoricalExpiry,
                "九珠重连后正常取回，历史期限仍独立保存");
            peer.Expect(1, 1025, [1]);
            peer.Barrier();
        }
        That(fixture.Query("SELECT count(*) FROM character_collectbox_slots WHERE character_id=$p0", created.Id) is [[0L]] &&
            fixture.Query("SELECT selected_box FROM character_collectbox_state WHERE character_id=$p0", created.Id)
                is [[long persistedSelection]] && persistedSelection == box.Index &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "完整链路最终状态、选择和数据库外键一致");

        string Snapshot() =>
            fixture.Snapshot("SELECT id,revision FROM characters WHERE id=$p0", created.Id) +
            fixture.Snapshot("SELECT * FROM character_collectbox_state WHERE character_id=$p0", created.Id) +
            fixture.Snapshot("SELECT * FROM character_collectbox_slots WHERE character_id=$p0 ORDER BY box_index,slot_index", created.Id) +
            fixture.Snapshot("SELECT * FROM character_collectbox_expirations WHERE character_id=$p0 ORDER BY box_index", created.Id) +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", created.Id) +
            fixture.Snapshot("SELECT list_type,slot_index,item_uid FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", created.Id) +
            fixture.Snapshot("SELECT action_name,list_type,slot_index,count_before,count_after FROM inventory_audit_log WHERE character_id=$p0 ORDER BY audit_id", created.Id);
    }

    static WirePacket Box(TcpPeer peer, byte box) => peer.CollectBoxes.Single(packet => packet.Body[0] == box);

    static void ValidateState(WirePacket packet, byte box, int itemId, bool selected, bool permanent = false)
    {
        That(packet.Kind == 0 && packet.Opcode == 1030 && packet.Body.Length == 12 &&
            packet.Body[0] == box && packet.Body[1] == (selected ? 1 : 0) && packet.Body[6] == (permanent ? 1 : 0) &&
            (!permanent || U32(packet.Body, 2) == 0) && packet.Body[7] == 1 && BinaryPrimitives.ReadInt32LittleEndian(packet.Body.AsSpan(8)) == itemId,
            "COLLECT_BOX变更通知包含精确箱、选择、期限与物品序列");
    }

    static void Delta(TcpPeer peer, ushort slot, int itemId, int count)
    {
        var body = peer.Expect(0, 14).Body;
        That(body.Length == 126 && body[0] == 0 && U16(body, 1) == 1 && U16(body, 3) == slot &&
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(5)) == itemId &&
            (itemId == -1 || BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(9)) == count),
            "收集箱只发布真实主背包差量");
    }

    static byte[] Add(byte box, ushort slot, int itemId, byte reserved = 0) => Build(writer =>
    {
        writer.Write(box);
        writer.Write(reserved);
        writer.Write(slot);
        writer.Write(itemId);
    });

    static byte[] Remove(byte box, int itemId) => Build(writer =>
    {
        writer.Write(box);
        writer.Write(itemId);
    });

    static Character Grant(Character character, ItemTemplate template, int count, DateTimeOffset now, EpicPieceCatalog pieces)
    {
        var result = InventoryRewardGrantService.TryGrantCore(character,
            InventoryRewardGrantService.Create(template, count, now, shopExpiration: true), template, pieces, now);
        if (!result.Accepted)
            throw new InvalidDataException("TCP收集箱物品无法进入普通背包: " + template.Id);
        return character with { Inventory = result.Plan!.After };
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
