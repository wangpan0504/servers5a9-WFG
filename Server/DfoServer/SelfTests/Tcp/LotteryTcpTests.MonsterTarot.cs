using System.Buffers.Binary;
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
    static void MonsterTarot(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repo, AccountOwners owners)
    {
        const int tarot = 10159707;
        var items = resources.ItemResources;
        var rewards = items.Boosters.Get(tarot).Pools.Single().Rewards;
        var granted = new Dictionary<int, int>();
        Character paid, poor;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            paid = repo.Create(owner, Role("TarotPaid", 200000));
            poor = repo.Create(owner, Role("TarotPoor", 99999));
        }
        using (var peer = Open(fixture))
        {
            peer.Select(poor.Slot); peer.Barrier();
            string before = State();
            Send(peer, 117); peer.Expect(1, 27, [0, 19, 0]); peer.Barrier();
            That(State() == before, "塔罗金币差一CMD27拒绝且完整资产不变");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(paid.Slot); peer.Barrier();
            string before = State();
            fixture.Sql("CREATE TRIGGER fail_tarot BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'tarot rollback'); END");
            Send(peer, 117); peer.Closed();
            That(State() == before, "塔罗晚SQL失败原子回滚源、金币、奖励、冷却、revision、UID和审计");
            fixture.Sql("DROP TRIGGER fail_tarot");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(paid.Slot); peer.Barrier();
            for (int opened = 1; opened <= 2; opened++)
            {
                // 该真实牌没有冷却声明；不推进时间掩盖重复开启的行为。
                Send(peer, 117);
                byte[] ack = peer.Expect(1, 27).Body;
                That(ack.Length == 130 && ack[0] == 1 && U16(ack, 1) == 117 && U32(ack, 126) == 0,
                    "塔罗成功沿原生ACK27完整130B合同，金币奖励尾段为零");
                var reward = rewards.Single(row => row.ItemId == U32(ack, 5));
                short slot = (short)U16(ack, 3);
                var core = Core(reward.ItemId, reward.Count);
                That(U32(ack, 9) == core.Value, "塔罗ACK保留装备实例品级或本次堆叠份数，包括250根源");
                byte[] delta = peer.Expect(0, 14).Body;
                BoosterTcpTests.Entry(delta, 117, opened == 1 ? tarot : -1, opened == 1 ? 1 : 0);
                BoosterTcpTests.Entry(delta, 0, 0, 200000 - opened * 100000);
                int at = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123)
                    .Single(index => U16(delta, index) == slot);
                byte[] expected = delta.AsSpan(at, 123).ToArray();
                if (core.IsStackable)
                    BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(6), reward.Count);
                That(ack.AsSpan(3, 123).SequenceEqual(expected), "塔罗ACK除增量外与完整权威奖励差量逐字节相同");
                peer.Barrier();
                var saved = Read();
                granted[reward.ItemId] = granted.GetValueOrDefault(reward.ItemId) + reward.Count;
                var actual = saved.Inventory.Items.Where(item => rewards.Any(row => row.ItemId == item.Core.ItemId))
                    .GroupBy(item => item.Core.ItemId).ToDictionary(group => group.Key, group => group.Sum(item => item.Core.Count));
                That(saved.Inventory.Gold == 200000 - opened * 100000 &&
                    saved.Inventory.Find(Bag(66)) == paid.Inventory.Find(Bag(66)) &&
                    saved.Inventory.Find(Bag(117))?.Core.Count == (opened == 1 ? 1 : (int?)null) &&
                    saved.Inventory.Items.All(item => item.Core.ItemId is tarot or 1106 || rewards.Any(row => row.ItemId == item.Core.ItemId)) &&
                    actual.Count == granted.Count && granted.All(pair => actual.GetValueOrDefault(pair.Key) == pair.Value),
                    "塔罗扣费源和完整实际奖励共同保存，不影响哨兵或混入池外奖励");
            }
            string exhausted = State();
            Send(peer, 117); peer.Expect(1, 27, [0, 19, 0]); peer.Barrier();
            That(State() == exhausted, "塔罗源耗尽重放不重复扣费或发奖");
        }
        string committed = State();
        using (var peer = Open(fixture))
        {
            peer.Select(paid.Slot); peer.Barrier();
            That(State() == committed && Read().Inventory.Gold == 0 && Read().Inventory.Find(Bag(117)) is null,
                "塔罗重连保留已提交完整产物、UID、零金币和源耗尽事实");
        }
        That(fixture.Events().Count(row => row.GetProperty("Kind").GetString() == "closed" &&
            row.GetProperty("Detail").GetProperty("Reason").GetString() == "SqliteException" &&
            row.GetProperty("Detail").GetProperty("Message").GetString() == "SQLite Error 19: 'tarot rollback'.") == 1,
            "塔罗回滚仅来自指定末段SQL故障，不以其他断线冒充");

        NewCharacter Role(string name, uint gold) => new(name, resources.Towns.Room, resources.Towns.InitialReturn)
        {
            Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
            Inventory = new([new(0, Bag(117), Core(tarot, 2)), new(0, Bag(66), Core(1106, 7))], Gold: gold)
        };
        ItemCore Core(int id, int count) => InventoryRewardGrantService.Create(items.Items.Get(id), count, utcNow: fixture.UtcNow);
        Character Read()
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.LoadCharacter(owner, paid.Slot);
        }
        string State() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }
}
