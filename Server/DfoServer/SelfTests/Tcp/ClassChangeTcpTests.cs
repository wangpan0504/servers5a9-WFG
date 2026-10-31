using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ClassChangeTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        int checks = Count;
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "class-change"), resources: resources);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        foreach (int id in new[] { 2660701, 10099314, 10099788 })
            That(resources.ItemResources.Items.ClassChangeTicket(id) is { MinimumLevel: 15, MaximumLevel: 50 },
                "同时声明新手服与普通服的真实互转券保留等级限制 " + id);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = ClassChangeTests.CharacterFixture(resources);
            repository.Create(owner, candidate with { Inventory = candidate.Inventory with
            {
                Items = candidate.Inventory.Items.Select(item => item.Position.Index == 65
                    ? item with { Core = item.Core with { ItemId = 2660701 } } : item).ToImmutableArray()
            } });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = Snapshot();
            foreach (var (body, error) in new (byte[], ushort)[]
            {
                ([65,0,1,0],3), ([65,0,0,0],1), ([65,0,6,0],1), ([100,0,2,0],17), ([65,0,2],1)
            })
            {
                peer.Send(502, body); peer.Expect(1, 502, [0,(byte)error,(byte)(error >> 8)]); peer.Barrier();
                That(Snapshot() == before, "502拒绝原分支、无效分支、空槽及普通券缺字段，连接可继续");
            }
            fixture.Sql("CREATE TRIGGER fail_class_change_late BEFORE INSERT ON character_skill_bindings BEGIN SELECT RAISE(ABORT,'class change rollback'); END");
            peer.Send(502, [65,0,2,0]); peer.Closed();
            That(Snapshot() == before, "真实502晚期SQL失败不提前发成功，职业、两树、材料、UID及revision完整回滚");
            fixture.Sql("DROP TRIGGER fail_class_change_late");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            long revision = (long)fixture.Query("SELECT revision FROM characters WHERE name='ClassHero'")[0][0];
            string remaining = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE item_uid IN " +
                "(SELECT item_uid FROM character_inventory_slots WHERE slot_index IN (66,67)) ORDER BY item_uid");
            peer.Send(502, [65,0,2,0]); ReadSuccess(peer, 65, 0x12);
            That(fixture.Query("SELECT grow_type,revision FROM characters WHERE name='ClassHero'") is [[18L,var next]] && (long)next == revision + 1 &&
                fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE list_type=0 AND slot_index=65") is [[0L]] &&
                remaining == fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE item_uid IN " +
                    "(SELECT item_uid FROM character_inventory_slots WHERE slot_index IN (66,67)) ORDER BY item_uid"),
                "一觉职业1转2保留阶段，只消耗原65槽实例且不改其他券UID");
            string once = Snapshot();
            peer.Send(502, [66,0,2,0]); peer.Expect(1, 502, [0,3,0]); peer.Barrier();
            That(Snapshot() == once, "成功后的相同目标请求不消费下一张券或再次返还点数");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true); peer.Barrier();
            That(fixture.Query("SELECT grow_type FROM characters WHERE name='ClassHero'") is [[18L]], "重连回放已提交的一觉新分支");
            peer.Send(502, [66,0,5,1]); ReadSuccess(peer, 66, 0x15);
            That(fixture.Query("SELECT grow_type FROM characters WHERE name='ClassHero'") is [[21L]], "1到5互转不被首次转职门禁挡住，确认标记1不伪造免费模式");
            peer.Send(502, [68,0,1]); ReadSuccess(peer, 68, 0x11);
            That(fixture.Query("SELECT grow_type FROM characters WHERE name='ClassHero'") is [[17L]], "固定目标回归之书的3B真实请求回到指定分支，保留一觉");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true); peer.Barrier();
            string before = Snapshot(); peer.Send(502, [67,0,1,0,0]); peer.Closed();
            That(before == Snapshot(), "畸形502不进入资产事务或返回通用成功");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "职业、双树、完成位和库存的文件库完整");
        Console.WriteLine($"PASS {Count - checks} 职业互转502、完整包序、SQL回滚、重连及重复TCP断言");

        void ReadSuccess(TcpPeer peer, ushort slot, byte grow)
        {
            byte[] actor = peer.Expect(0, 2).Body;
            int nameLength = checked((int)U32(actor, 123));
            That(actor[0] == 0 && U16(actor, 121) == peer.Id && actor[127 + nameLength] == 0 && actor[128 + nameLength] == grow,
                "ACK502前先以真实Actor安装基础职业及保留觉醒阶段的新分支");
            var trees = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body);
            That(trees.Length == 2 && trees.All(tree => tree.Sp == resources.Learning.TotalSp(50) + 137 &&
                tree.Tp == resources.Learning.TotalTp(50) + 11 && tree.Skills.All(skill => skill.Command.Length == 0)),
                "完整19同步两树退点和新免费技能，旧指令不残留");
            That(peer.Expect(0, 2).Body[0] == 1, "新职业后同步原Player基础属性而不借用Player刷新职业");
            byte[] item = peer.Expect(0, 1681).Body;
            That(item[0] == 0 && U16(item, 1) == 1 && U16(item, 3) == slot, "无获得提示地清除已提交的实际源槽");
            peer.Expect(0, 21); peer.Expect(0, 356); peer.Expect(1, 502, [1]); peer.Barrier();
        }

        string Snapshot() => fixture.Snapshot("SELECT * FROM characters") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_skill_bindings ORDER BY character_id,context,slot") +
            fixture.Snapshot("SELECT * FROM character_skill_points ORDER BY character_id,context") +
            fixture.Snapshot("SELECT * FROM character_completed_quests ORDER BY character_id,quest_id");
    }
}
