using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class LotteryTcpTests
{
    static void LegacyPaid(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repo, AccountOwners owners)
    {
        const int pot = 490009330, child = 490009184;
        Character paid, poor;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            paid = repo.Create(owner, Role("LegacyPaid", 200000));
            poor = repo.Create(owner, Role("LegacyPoor", 99999));
        }
        using (var peer = Open(fixture))
        {
            peer.Select(poor.Slot); peer.Barrier();
            string before = State();
            Send(peer, 117); peer.Expect(1, 27, [0, 19, 0]); peer.Barrier();
            That(State() == before, "普通legacy罐金币差一真实CMD27拒绝且完整资产不变");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(paid.Slot); peer.Barrier();
            string before = State();
            fixture.Sql("CREATE TRIGGER fail_legacy_paid BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'legacy paid rollback'); END");
            Send(peer, 117); peer.Closed();
            That(State() == before, "普通legacy罐晚SQL失败保全源、金币、原奖励堆叠、哨兵、UID、revision和审计");
            fixture.Sql("DROP TRIGGER fail_legacy_paid");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(paid.Slot); peer.Barrier();
            for (int opened = 1; opened <= 2; opened++)
            {
                Send(peer, 117);
                byte[] ack = peer.Expect(1, 27).Body;
                That(ack.Length == 130 && ack[0] == 1 && U16(ack, 1) == 117 && U16(ack, 3) == 65 &&
                    U32(ack, 5) == child && U32(ack, 9) == 1 && U32(ack, 126) == 0,
                    "普通legacy成功ACK27完整130B，欧气子盒按本次一只增量回包");
                byte[] delta = peer.Expect(0, 14).Body;
                That(U16(delta, 1) == 3, "扣源、金币和子盒堆叠三个完整差量，无代开奖叶或购买材料");
                BoosterTcpTests.Entry(delta, 117, opened == 1 ? pot : -1, opened == 1 ? 1 : 0);
                BoosterTcpTests.Entry(delta, 0, 0, 200000 - opened * 100000);
                BoosterTcpTests.Entry(delta, 65, child, 3 + opened);
                int at = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123).Single(index => U16(delta, index) == 65);
                byte[] expected = delta.AsSpan(at, 123).ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(6), 1);
                That(ack.AsSpan(3, 123).SequenceEqual(expected), "ACK完整实例除本次数量外逐字节等于提交差量");
                peer.Barrier();
                var saved = Read();
                That(saved.Inventory.Gold == 200000 - opened * 100000 &&
                    saved.Inventory.Find(Bag(65))!.Core == Core(child, 3 + opened) &&
                    saved.Inventory.Find(Bag(65))!.Uid == paid.Inventory.Find(Bag(65))!.Uid &&
                    saved.Inventory.Find(Bag(66)) == paid.Inventory.Find(Bag(66)) &&
                    saved.Inventory.Items.All(item => item.Core.ItemId is pot or child or 1106),
                    "每次只扣十万与一源，既有子盒UID和哨兵不变，不自动发子盒内容");
            }
            string exhausted = State();
            Send(peer, 117); peer.Expect(1, 27, [0, 19, 0]); peer.Barrier();
            That(State() == exhausted, "普通legacy源耗尽重放不扣费或重复发奖");
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(paid.Slot)[4].Body;
            BoosterTcpTests.Entry(full, 65, child, 5, true);
            BoosterTcpTests.Entry(full, 66, 1106, 7, true);
            peer.Barrier();
            That(Read().Inventory.Gold == 0 && Read().Inventory.Find(Bag(117)) is null,
                "重登恢复五只完整欧气罐与零金币，源不复活");
        }
        That(fixture.Events().Count(row => row.GetProperty("Kind").GetString() == "closed" &&
            row.GetProperty("Detail").GetProperty("Reason").GetString() == "SqliteException" &&
            row.GetProperty("Detail").GetProperty("Message").GetString() == "SQLite Error 19: 'legacy paid rollback'.") == 1,
            "普通legacy失败来自唯一末段SQL故障注入，不以其它断线冒充回滚");

        NewCharacter Role(string name, uint gold) => new(name, resources.Towns.Room, resources.Towns.InitialReturn)
        {
            AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
            Inventory = new([new(0, Bag(117), Core(pot, 2)), new(0, Bag(65), Core(child, 3)), new(0, Bag(66), Core(1106, 7))], Gold: gold)
        };
        ItemCore Core(int id, int count) => InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(id), count, utcNow: fixture.UtcNow);
        Character Read()
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.LoadCharacter(owner, paid.Slot);
        }
        string State() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }
}
