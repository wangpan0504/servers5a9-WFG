using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class LotteryTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        LotteryTests.Run(resources);
        var items = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "lottery"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("PotTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new([new(0, Bag(117), Core(LotteryTests.SourceId, 2)),
                    .. Enumerable.Range(0, 6).Select(i => new InventoryItem(0, Bag((short)(65 + i)), Core(490705157 + i, 5)))], Gold: 12345)
            });
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        using (var peer = Open(fixture))
        {
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Select();
            string before = Snapshot();
            foreach (byte[] malformed in new[] { Array.Empty<byte>(), new byte[3], new byte[5], Convert.FromHexString("01007500"), Convert.FromHexString("0000FFFF") })
            {
                peer.Send(27, malformed);
                peer.Expect(1, 27, [0, 19, 0]);
            }
            peer.Barrier();
            That(before == Snapshot(), "畸形与未知模式不改变源、奖励、审计或UID");
            for (int opened = 1; opened <= 2; opened++)
            {
                Send(peer, 117);
                byte[] ack = peer.Expect(1, 27).Body;
                That(ack.Length == 130 && ack[0] == 1 && U16(ack, 1) == 117 && U32(ack, 9) == 1 && U32(ack, 126) == 0,
                    "ACK27完整130B，奖励数量为增量而非已有堆叠总数");
                int id = (int)U32(ack, 5);
                short slot = (short)U16(ack, 3);
                That(id is >= 490705157 and <= 490705162 && slot == 65 + id - 490705157, "应答引用实际既有奖励槽");
                byte[] delta = peer.Expect(0, 14).Body;
                BoosterTcpTests.Entry(delta, 117, opened == 1 ? LotteryTests.SourceId : -1, opened == 1 ? 1 : 0);
                int at = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123).Single(index => U16(delta, index) == slot);
                int total = (int)U32(delta, at + 6);
                That(total is 6 or 7, "提交后差量携带合并总数");
                var authoritative = delta.AsSpan(at, 123).ToArray();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(authoritative.AsSpan(6), 1);
                That(ack.AsSpan(3, 123).SequenceEqual(authoritative), "ACK普通记录除增量数量外与权威差量逐字节一致");
            }
            peer.Barrier();
            before = Snapshot();
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Barrier();
            That(before == Snapshot(), "罐子耗尽后重放不重复发奖");
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                [new(LotteryTests.SourceId, 1)], items.Items, items.GoldLimits, items.EpicPieces, shopExpiration: true));
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            short slot = SourceSlot();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_lottery BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'lottery rollback'); END");
            Send(peer, slot);
            peer.Closed();
            That(before == Snapshot(), "晚期SQL失败不回成功并原子回滚源、奖励、审计和UID");
            fixture.Sql("DROP TRIGGER fail_lottery");
        }
        using (var peer = Open(fixture))
        {
            short slot = SourceSlot();
            BoosterTcpTests.Entry(peer.Select()[4].Body, slot, LotteryTests.SourceId, 1, true);
            Send(peer, slot);
            That(peer.Expect(1, 27).Body.Length == 130, "失败后重连仍可正常打开");
            peer.Expect(0, 14);
            peer.Barrier();
        }
        string committed = Snapshot();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(committed == Snapshot(), "重启后源和全部奖励及UID保持提交结果");
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT gold FROM characters") is [[12345L]], "资源未声明费用时不额外扣金币");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "袖珍罐数据库完整性");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Items.Get(id), count, shopExpiration: true);
        short SourceSlot()
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.List(owner).Single().Inventory.Items.Single(item => item.Core.ItemId == LotteryTests.SourceId).Position.Index;
        }
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static void Send(TcpPeer peer, short slot) => peer.Send(27, Build(writer => { writer.Write((ushort)0); writer.Write(slot); }));
}
