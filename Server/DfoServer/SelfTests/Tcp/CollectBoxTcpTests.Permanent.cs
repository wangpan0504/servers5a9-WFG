using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.CollectBox;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CollectBoxTcpTests
{
    static async Task RunPermanentAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var boxes = items.CollectBoxes.Entries;
        That(boxes.Length == 8 && boxes.Sum(box => box.Slots.Count(slot => slot.ItemId > 0)) == 84 &&
            boxes.All(box => box.BaseExpiration is null && box.MaximumExpiration is null),
            "当前PVF八箱84格均完整永久，不以缺少限时箱为缺失资源");
        var box = boxes.First(row => !row.Extensions.IsEmpty);
        var slot = box.Slots.First(row => row.ItemId > 0);
        var extension = box.Extensions.First(row => row.ItemId != slot.ItemId);
        var spring = items.CollectBoxes.Get(8);
        var springSlot = spring.Slots.First(row => row.ItemId > 0);
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-01T04:00:00Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "collectbox-permanent"), resources: resources, clock: clock);
        var draft = new Character(1, 0, "PermanentDraft", 0, resources.Towns.Room, resources.Towns.InitialReturn);
        foreach (var (id, count) in new[] { (slot.ItemId, 2), (extension.ItemId, extension.RequiredCount), (springSlot.ItemId, 2) })
        {
            var template = items.Items.Get(id);
            draft = Grant(draft, template, Math.Min(count, template.StackLimit), clock.Now, items.EpicPieces);
        }
        var repository = new CharacterRepository(fixture.Database);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            created = repository.Create(owner, new("PermanentBox", resources.Towns.Room, resources.Towns.InitialReturn)
                { Inventory = draft.Inventory, AcceptedStoryLevel = 1 });
        foreach (byte id in new[] { box.Index, spring.Index })
            fixture.Sql("INSERT INTO character_collectbox_expirations(character_id,account,box_index,expires_at) VALUES($p0,$p1,$p2,1)",
                created.Id, fixture.Options.DevelopmentAccount, id);
        var bead = created.Inventory.Items.Single(item => item.Core.ItemId == slot.ItemId);
        var ticket = created.Inventory.Items.Single(item => item.Core.ItemId == extension.ItemId);
        var springBead = created.Inventory.Items.Single(item => item.Core.ItemId == springSlot.ItemId);
        ushort sourceSlot = checked((ushort)bead.Position.Index), ticketSlot = checked((ushort)ticket.Position.Index);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            foreach (var entry in boxes)
            {
                var initial = Box(peer, entry.Index).Body;
                That(initial.Length == 8 && initial[6] == 1 && U32(initial, 2) == 0,
                    "八个真实箱均发布永久位与零剩余秒，历史过期值不阻断");
            }
            peer.Send(1023, [box.Index]); peer.Expect(1, 1023, [box.Index, box.Index]);
            peer.Send(1024, Add(box.Index, sourceSlot, slot.ItemId));
            Delta(peer, sourceSlot, bead.Core.Count == 1 ? -1 : slot.ItemId, bead.Core.Count - 1);
            ValidateState(peer.Expect(0, 1030), box.Index, slot.ItemId, selected: true, permanent: true);
            peer.Expect(1, 1024, [1]);
            peer.Send(1024, Add(box.Index, sourceSlot, slot.ItemId)); peer.Expect(1, 1024, [0, 0x17, 0]);
            string before = Snapshot();
            peer.Send(1026, Add(box.Index, ticketSlot, extension.ItemId));
            peer.Expect(1, 1026, Build(writer => { writer.Write((byte)0); writer.Write(CollectBoxService.ErrorMissing); }));
            peer.Barrier();
            That(Snapshot() == before, "永久箱拒绝无意义续期，券、期限、审计及revision完全不变");
            fixture.Sql("CREATE TRIGGER fail_permanent_box BEFORE INSERT ON character_collectbox_state BEGIN SELECT RAISE(ABORT,'permanent box rollback'); END");
            peer.Send(1025, Remove(box.Index, slot.ItemId)); peer.Closed();
            That(Snapshot() == before, "永久箱取回晚段失败不提前发奖，背包与收藏整笔回滚");
            fixture.Sql("DROP TRIGGER fail_permanent_box");
        }
        clock.Now = clock.Now.AddYears(1);
        using (var peer = Open(fixture))
        {
            peer.Select();
            ValidateState(Box(peer, box.Index), box.Index, slot.ItemId, selected: true, permanent: true);
            peer.Send(1025, Remove(box.Index, slot.ItemId));
            Delta(peer, sourceSlot, slot.ItemId, bead.Core.Count);
            var emptied = peer.Expect(0, 1030).Body;
            That(emptied.Length == 8 && emptied[6] == 1 && U32(emptied, 2) == 0, "跨年重连后取回仍永久且清空精确物品槽");
            peer.Expect(1, 1025, [1]);
            ushort springSource = checked((ushort)springBead.Position.Index);
            peer.Send(1024, Add(spring.Index, springSource, springSlot.ItemId));
            Delta(peer, springSource, springBead.Core.Count == 1 ? -1 : springSlot.ItemId, springBead.Core.Count - 1);
            ValidateState(peer.Expect(0, 1030), spring.Index, springSlot.ItemId, selected: false, permanent: true);
            peer.Expect(1, 1024, [1]);
            peer.Send(1025, Remove(spring.Index, springSlot.ItemId));
            Delta(peer, springSource, springSlot.ItemId, springBead.Core.Count);
            peer.Expect(0, 1030); peer.Expect(1, 1025, [1]); peer.Barrier();
        }
        That(fixture.Query("SELECT expires_at FROM character_collectbox_expirations WHERE character_id=$p0 ORDER BY box_index", created.Id)
                is [[1L], [1L]] && fixture.Query("SELECT count(*) FROM character_collectbox_slots WHERE character_id=$p0", created.Id) is [[0L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "两箱真实存取后无收藏残留，不重写历史期限且数据库一致");

        string Snapshot() => fixture.Snapshot("SELECT revision FROM characters WHERE id=$p0", created.Id) +
            fixture.Snapshot("SELECT * FROM character_collectbox_state WHERE character_id=$p0", created.Id) +
            fixture.Snapshot("SELECT * FROM character_collectbox_slots WHERE character_id=$p0 ORDER BY box_index,slot_index", created.Id) +
            fixture.Snapshot("SELECT * FROM character_collectbox_expirations WHERE character_id=$p0 ORDER BY box_index", created.Id) +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", created.Id) +
            fixture.Snapshot("SELECT * FROM inventory_audit_log WHERE character_id=$p0 ORDER BY audit_id", created.Id);
    }
}
