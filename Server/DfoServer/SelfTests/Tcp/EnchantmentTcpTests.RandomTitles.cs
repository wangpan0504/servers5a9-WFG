using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class EnchantmentTcpTests
{
    static async Task RunRandomTitlesAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int beadId = 490703772, titleId = 400330167;
        var items = resources.ItemResources.Items;
        var pool = resources.ItemResources.Enchantments.Get(beadId)!.RandomTitle!;
        var title = InventoryRewardGrantService.Create(items.Get(titleId)) with
        { Attr = 7, EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711, SortLockFlag = 1 };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "random-title-enchantment"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("TitleBead", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 25, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([
                    new(0, new(InventoryListType.Main, 82), InventoryRewardGrantService.Create(items.Get(beadId), 2)),
                    new(0, new(InventoryListType.Main, 9), title),
                    new(0, new(InventoryListType.Main, 10), InventoryRewardGrantService.Create(items.Get(27308))),
                    new(0, new(InventoryListType.Equipment, 13), title)], Gold: 1234)
            });
        string uids = Uids();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1,713),(1,714)]);
            string before = Snapshot();
            Send(peer, 0, 10); peer.Expect(1, 272, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == before, "随机称号宝珠拒绝白名单外装备，不扣源或写扩展");
            fixture.Sql("CREATE TRIGGER fail_random_title BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='enchant_by_bead' BEGIN SELECT RAISE(ABORT,'title rollback'); END");
            Send(peer, 0, 9); peer.Closed();
            That(Snapshot() == before, "随机结果与扣源在审计失败时一起回滚，不先回成功");
            fixture.Sql("DROP TRIGGER fail_random_title");
        }
        var saved = new Dictionary<byte, byte[]>();
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1,713),(1,714)]);
            foreach (var (list, slot) in new (byte, short)[] { (0,9), (3,13) })
            {
                Send(peer, list, slot);
                var sourceDelta = peer.Expect(0, 14).Body;
                That(U32(Row(sourceDelta, 82), 2) == (list == 0 ? beadId : uint.MaxValue) &&
                    (list != 0 || U32(Row(sourceDelta, 82), 6) == 1), "随机称号每次只扣一颗，最后一颗清槽");
                var delta = list == 0 ? sourceDelta : peer.Expect(0, 14).Body;
                saved[list] = Row(delta, slot);
                uint effect = U32(saved[list], 97);
                That((ushort)effect == 10015 && pool.Entries.Any(entry => entry.Index == (effect >> 16)) &&
                    U32(saved[list], 14) == 3175 && saved[list][18] == 2,
                    "真实从零宝珠安装正权重技能索引并保留普通卡片附魔");
                if (list == 3)
                    That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1, "穿戴称号同步Actor/Player");
                peer.Expect(1, list == 3 ? (ushort)714 : (ushort)713, [1,list,(byte)slot,0]);
                peer.Expect(1, 272, [1,list,(byte)slot,0]); peer.Barrier();
            }
            string before = Snapshot();
            Send(peer, 0, 9); peer.Expect(1, 272, [0,23,0]); peer.Barrier();
            That(Snapshot() == before, "耗尽源重发不再抽奖或写库");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(1,713),(1,714)]);
            Bytes(Row(entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body, 9, true),
                saved[0], "重连恢复背包称号完整随机附魔行，穿戴核心另由仓储读取核对");
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var inventory = new CharacterRepository(fixture.Database).List(owner).Single().Inventory;
            foreach (var (list, slot) in new (byte, short)[] { (0,9), (3,13) })
                That(inventory.Find(new((InventoryListType)list, slot))!.Core == title with { Extension97 = U32(saved[list], 97) },
                    "保存只修改随机称号索引，完整核心与UID保留");
            That(inventory.Gold == 1234 && Uids() == uids, "随机附魔不扣金币、不替换实例UID");
        }

        string Uids() => fixture.Snapshot("SELECT item_uid FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')<>$p0 ORDER BY item_uid", beadId);
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        static void Send(TcpPeer peer, byte list, short slot) => peer.Send(272, Build(writer =>
        { writer.Write((byte)0); writer.Write((short)82); writer.Write(list); writer.Write(slot); writer.Write(uint.MaxValue); }));
        static byte[] Row(byte[] body, short slot, bool full = false)
        {
            int head = full ? 5 : 3, count = U16(body, full ? 3 : 1);
            That(body.Length == head + count * 123, "随机称号复用完整123B物品行");
            int at = Enumerable.Range(0, count).Select(index => head + index * 123).Single(offset => U16(body, offset) == slot);
            return body.AsSpan(at, 123).ToArray();
        }
    }
}
