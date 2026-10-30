using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class MonsterCardUpgradeTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        Inventory.MonsterCardUpgradeTests.RunResources(resources.ItemResources);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "monster-card-upgrade"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            Create("UpgradeTcp", 10310789, 4, 1);
            Create("StackTcp", 3620, 2, 3);
            void Create(string name, int itemId, int slots, int count)
            {
                var template = resources.ItemResources.Items.Get(itemId);
                var inventory = Enumerable.Range(233, slots).Select(slot =>
                    InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, (short)slot),
                        InventoryRewardGrantService.Create(template, count))).ToImmutableArray();
                repository.Create(owner, new(name, resources.Towns.Room, resources.Towns.InitialReturn)
                {
                    Job = 0, Level = 95, AcceptedStoryLevel = 1,
                    Skills = resources.Characters.Get(0).Skills, Inventory = new(inventory)
                });
            }
        }
        await fixture.StartAsync();
        byte[] request = Convert.FromHexString("00E9000300EA0000EB0000EC00");
        string original = Snapshot("UpgradeTcp");
        using (var peer = Open(fixture))
        {
            peer.Send(603, request);
            peer.Expect(1, 603, [0, 22, 0]);
            That(Snapshot("UpgradeTcp") == original, "未选角603不消费材料、不改变revision");
            peer.Select();
            peer.Barrier();
            original = Snapshot("UpgradeTcp");
            peer.Send(603, request[..^1]);
            peer.Expect(1, 603, [0, 22, 0]);
            peer.Barrier();
            That(Snapshot("UpgradeTcp") == original, "畸形603回失败且后续连接继续、资产不变");
            fixture.Sql("CREATE TRIGGER fail_monster_card_upgrade BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='monster_card_upgrade' BEGIN SELECT RAISE(ABORT,'card upgrade rollback'); END");
            peer.Send(603, request);
            peer.Closed();
            That(Snapshot("UpgradeTcp") == original, "晚期审计失败回滚等级、三张材料、UID与revision，无提前成功");
            fixture.Sql("DROP TRIGGER fail_monster_card_upgrade");
        }
        string committed;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            peer.Send(603, request);
            peer.Expect(1, 603, Convert.FromHexString("010185549D0001"));
            var delta = peer.Expect(0, 14).Body;
            That(delta.Length == 495 && delta[0] == 0 && U16(delta, 1) == 4 && U16(delta, 3) == 233 &&
                U32(delta, 5) == 10310789 && U32(delta, 9) == 1 && delta[21] == 1,
                "603结果ACK后以NOTI14完整物品行同步目标真实等级与材料四槽变化");
            for (int index = 1; index < 4; index++)
                That(U16(delta, 3 + index * 123) == 233 + index && U32(delta, 5 + index * 123) == uint.MaxValue,
                    "每个消耗完的材料槽显式清空，不靠结果窗口猜测扣除");
            peer.Barrier();
            committed = Snapshot("UpgradeTcp");
            That(committed != original && Rows("UpgradeTcp") is [[long _, long _, long _, long itemId, long count, long level]] &&
                itemId == 10310789 && count == 1 && level == 1,
                "三张材料必成：数据库只保留原卡一张一级，真实事务提交");
            peer.Send(603, request);
            peer.Expect(1, 603, [0, 22, 0]);
            peer.Barrier();
            That(Snapshot("UpgradeTcp") == committed, "满级或耗尽材料后重发拒绝，不重复提交");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(Snapshot("UpgradeTcp") == committed, "重连恢复相同卡片等级、UID和材料余量");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            peer.Barrier();
            peer.Send(603, Convert.FromHexString("00E9000100EA00"));
            peer.Expect(1, 603, Convert.FromHexString("0101240E000001"));
            peer.Expect(0, 14);
            peer.Barrier();
            var rows = Rows("StackTcp");
            That(rows.Length == 3 && rows.Count(row => Convert.ToInt64(row[5]) == 1) == 1 &&
                rows.Sum(row => Convert.ToInt64(row[4])) == 5 &&
                rows.Any(row => Convert.ToInt64(row[1]) == 233 && Convert.ToInt64(row[4]) == 2 && Convert.ToInt64(row[5]) == 0) &&
                rows.All(row => Convert.ToInt64(row[2]) > 0),
                "普通卡整堆三张仅一张升级，材料只扣一张，新实例分配真实UID，剩余五张不丢失");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "卡片升级及回滚后数据库和外键完整");

        object[][] Rows(string name) => fixture.Query("""
            SELECT c.revision,s.slot_index,i.item_uid,json_extract(i.item_core,'$.ItemId'),
                json_extract(i.item_core,'$.Value'),json_extract(i.item_core,'$.EnchantUpgrade')
            FROM characters c JOIN character_inventory_slots s ON s.character_id=c.id
            JOIN character_inventory_items i ON i.item_uid=s.item_uid
            WHERE c.name=$p0 ORDER BY s.list_type,s.slot_index
            """, name);
        string Snapshot(string name) => System.Text.Json.JsonSerializer.Serialize(Rows(name));
    }
}
