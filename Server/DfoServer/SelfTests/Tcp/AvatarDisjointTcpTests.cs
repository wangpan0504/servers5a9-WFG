using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AvatarDisjointTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        var template = Inventory.AvatarDisjointTests.FindEligibleAvatar(itemResources);
        int[] rewardIds = itemResources.AvatarDisjoint.Get(template.Grade)!.Pools
            .SelectMany(pool => pool.Rewards).Select(reward => reward.ItemId).Distinct().ToArray();
        var sources = Enumerable.Range(0, 3).Select(slot =>
            InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, (short)slot),
                InventoryRewardGrantService.Create(template))).ToArray();
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "avatar-disjoint"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("AvDisjTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = new([.. sources])
            });

        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string unselected = Snapshot();
            peer.Send(202, Request(0, template.Id));
            peer.Expect(1, 202, [0, 19, 0]);
            That(Snapshot() == unselected, "未选角的CMD202失败ACK不改变时装、详情或revision");
            peer.Select();
            peer.Barrier();

            string unchanged = Snapshot();
            peer.Send(449, [0, 0]);
            peer.Expect(1, 449, [0, 19, 0]);
            peer.Send(202, Request(0, template.Id + 1));
            peer.Expect(1, 202, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == unchanged, "畸形CMD449与错误期望编号均明确拒绝且连接可继续");

            var before = Counts();
            peer.Send(202, Request(0, template.Id));
            var standard = ReceiveStandard(peer, 0);
            peer.Barrier();
            AssertCounts(before, standard, "CMD202");
            string committed = Snapshot();
            peer.Send(202, Request(0, template.Id));
            peer.Expect(1, 202, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == committed, "CMD202已删除源槽重放不重复发奖");

            before = Counts();
            peer.Send(449, Request(1, template.Id));
            var randomReward = ReceiveRandomReward(peer, 1);
            peer.Barrier();
            AssertCounts(before, randomReward, "CMD449");
            committed = Snapshot();
            peer.Send(449, Request(1, template.Id));
            peer.Expect(1, 449, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == committed, "CMD449已删除源槽重放不重复发奖");

            string rollback = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tcp_avatar_disjoint BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='avatar_disjoint' BEGIN SELECT RAISE(ABORT,'avatar disjoint failure'); END");
            peer.Send(449, Request(2, template.Id));
            peer.Closed();
            That(Snapshot() == rollback, "时装分解审计失败回滚源详情、全部徽章、revision与审计且不提前发ACK");
            fixture.Sql("DROP TRIGGER fail_tcp_avatar_disjoint");
        }

        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            var before = Counts();
            peer.Send(449, Request(2, template.Id));
            var rewards = ReceiveRandomReward(peer, 2);
            peer.Barrier();
            AssertCounts(before, rewards, "回滚后CMD449重试");
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT count(*) FROM inventory_avatar_details") is [[0L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "两种时装分解提交后无孤立详情且数据库外键完整");

        Dictionary<int, int> ReceiveStandard(TcpPeer peer, short sourceSlot)
        {
            byte[] ack = peer.Expect(1, 202).Body;
            That(ack.Length >= 5 && ack[0] == 1 && U16(ack, 1) == sourceSlot &&
                ack.Length == 5 + U16(ack, 3) * 10, "CMD202 ACK先到达且使用u16行数和10字节槽位增量");
            var rows = Enumerable.Range(0, U16(ack, 3)).Select(index => 5 + index * 10).ToArray();
            var increments = new Dictionary<int, int>();
            byte[] main = peer.Expect(0, 14).Body;
            foreach (int at in rows)
            {
                short slot = (short)U16(ack, at);
                int itemId = checked((int)U32(ack, at + 2));
                int increment = checked((int)U32(ack, at + 6));
                int delta = DeltaEntry(main, InventoryListType.Main, slot);
                That(increment > 0 && U32(main, delta + 2) == (uint)itemId &&
                    U32(main, delta + 6) >= (uint)increment, "CMD202每行目标槽和增量对应随后权威Main差量");
                increments[itemId] = checked(increments.GetValueOrDefault(itemId) + increment);
            }
            byte[] avatar = peer.Expect(0, 14).Body;
            int source = DeltaEntry(avatar, InventoryListType.Avatar, sourceSlot);
            That(U32(avatar, source + 2) == uint.MaxValue, "CMD202 ACK之后才清除源时装槽");
            return increments;
        }

        Dictionary<int, int> ReceiveRandomReward(TcpPeer peer, short sourceSlot)
        {
            byte[] ack = peer.Expect(1, 449).Body;
            That(ack.Length >= 4 && ack[0] == 1 && U16(ack, 1) == sourceSlot &&
                ack.Length == 4 + ack[3] * 8, "CMD449 ACK先到达且使用u8行数和无槽位8字节增量");
            var increments = new Dictionary<int, int>();
            for (int index = 0; index < ack[3]; index++)
            {
                int at = 4 + index * 8;
                int itemId = checked((int)U32(ack, at));
                int increment = checked((int)U32(ack, at + 4));
                That(increment > 0, "CMD449奖励增量为正");
                increments[itemId] = checked(increments.GetValueOrDefault(itemId) + increment);
            }
            byte[] main = peer.Expect(0, 14).Body;
            var changedItems = DeltaEntries(main, InventoryListType.Main)
                .Select(at => checked((int)U32(main, at + 2))).ToHashSet();
            That(increments.Keys.All(changedItems.Contains), "CMD449无槽位ACK后的Main差量包含全部实际奖励物品");
            byte[] avatar = peer.Expect(0, 14).Body;
            int source = DeltaEntry(avatar, InventoryListType.Avatar, sourceSlot);
            That(U32(avatar, source + 2) == uint.MaxValue, "CMD449 ACK之后通过NOTI14清除源时装槽");
            return increments;
        }

        void AssertCounts(IReadOnlyDictionary<int, long> before, IReadOnlyDictionary<int, int> increments, string mode)
        {
            var after = Counts();
            foreach (int itemId in rewardIds)
                That(after[itemId] - before[itemId] == increments.GetValueOrDefault(itemId),
                    mode + " ACK增量、权威差量与数据库徽章总量一致");
        }

        Dictionary<int, long> Counts()
        {
            var result = rewardIds.ToDictionary(itemId => itemId, _ => 0L);
            object[][] rows = fixture.Query("SELECT json_extract(i.item_core,'$.ItemId'),sum(json_extract(i.item_core,'$.Value')) " +
                "FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid " +
                "WHERE s.list_type=0 GROUP BY json_extract(i.item_core,'$.ItemId')");
            foreach (object[] row in rows)
            {
                int itemId = Convert.ToInt32(row[0], System.Globalization.CultureInfo.InvariantCulture);
                if (result.ContainsKey(itemId))
                    result[itemId] = Convert.ToInt64(row[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            return result;
        }
        string Snapshot() => fixture.Snapshot("SELECT id,revision FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static byte[] Request(short slot, int itemId) => Build(writer =>
    {
        writer.Write(slot);
        writer.Write(itemId);
    });

    static int DeltaEntry(byte[] body, InventoryListType list, short slot) =>
        DeltaEntries(body, list).Single(at => U16(body, at) == slot);

    static IEnumerable<int> DeltaEntries(byte[] body, InventoryListType list)
    {
        int count = U16(body, 1);
        int width = list == InventoryListType.Avatar ? 165 : 123;
        That(body[0] == (byte)list && body.Length == 3 + count * width,
            "NOTI14保留容器编号、u16行数及Main123/Avatar165字节条目宽度");
        return Enumerable.Range(0, count).Select(index => 3 + index * width);
    }
}
