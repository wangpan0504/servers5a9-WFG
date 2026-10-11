using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class ConsumableUseTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "consumable-use"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        ushort activeId;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("UsePotion", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var growth = CharacterGrowthCatalog.Load(resources.Archive, resources.Learning);
            var learning = SkillPointLedger.Synchronize(0, 0, 16, candidate.InitialLearning with
            { Experience = growth.GetLevelStartExperience(16) }, resources.Learning);
            var created = repository.Create(owner, candidate with { Level = 16, InitialLearning = learning,
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                    .Add(new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1108), 3)))
                    .Add(new(0, new(InventoryListType.Main, 66), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1114), 2)))
                    .Add(new(0, new(InventoryListType.Main, 67), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1016), 2))) } });
            activeId = created.Id;
            repository.Create(owner, candidate with { Name = "DungeonDose", Level = 16, InitialLearning = learning,
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65),
                    InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1108), 2))) } });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string before = Assets();
            Use(peer, 65, 0, 1108); Refused(peer, 0, 1108);
            peer.Send(692, Name("OtherName")); peer.Expect(1, 692, [1]);
            That(Assets() == before, "未选角完整失败ACK收尾后连接继续，数量/审计不变");
            peer.Select();
            foreach (var invalid in new[] { (Slot: (ushort)65, Id: 1114, Check: 7u, Arg: 0), (Slot: (ushort)67, Id: 1016, Check: 8u, Arg: 0),
                (Slot: (ushort)65, Id: 1108, Check: 32768u, Arg: 0), (Slot: (ushort)65, Id: 1108, Check: 9u, Arg: 1) })
            {
                before = Assets(); Use(peer, invalid.Slot, invalid.Check, invalid.Id, invalid.Arg); Refused(peer, invalid.Check, invalid.Id);
                peer.Barrier(); That(Assets() == before, "错误源ID、附加效果药、高check和非零参数完整拒绝，不扣资源");
            }
            Use(peer, 65, 0, 1108); Accepted(peer, 65, 0, 1108); peer.Barrier();
            That(Count(65) == 2, "成功44原子扣指定源1件，ACK后只有合法屏障，无先行源14或伪造HP包");
            Use(peer, 65, 1, 1108); Refused(peer, 1, 1108); peer.Barrier();
            That(Count(65) == 2, "同组冷却拒绝保持连接及数量");
            Use(peer, 66, 0, 1114); Accepted(peer, 66, 0, 1114); peer.Barrier();
            That(Count(66) == 1, "组1不与组0别名，两药按独立本服组间隔消费");
            Use(peer, 66, 0, 1114); peer.Closed();
            That(Count(66) == 1, "冷却窗同完整请求关原连接，无重复ACK或二次扣件");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); Use(peer, 65, 0, 1108); Accepted(peer, 65, 0, 1108); peer.Barrier();
            That(Count(65) == 1, "重连读取实际数量并重置单会话冷却，token0可新生成复用");
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2); peer.Select();
            Use(peer, 65, 0, 1108); Accepted(peer, 65, 0, 1108); peer.Barrier();
            That(Count(65) == 0 && fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE list_type=0 AND slot_index=65 AND character_id=$p0", activeId) is [[0L]],
                "最后一件清除持久源槽和实例，完整ACK完成本地单次消费合同");
            Use(peer, 65, 3, 1108); Refused(peer, 3, 1108); peer.Barrier();
            Use(peer, 66, 4, 1114); Accepted(peer, 66, 4, 1114); peer.Barrier();
            That(Count(66) == 0, "空源拒绝后另一合法组继续使用至空");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(Count(65) == 0 && Count(66) == 0 && fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]],
                "重登只恢复已消费库存，不补药、不复发已消费ACK，数据库外键完整");
            string before = Assets(); peer.Send(44, new byte[10]); peer.Closed();
            That(Assets() == before, "特殊10B未知请求不截成普通成功，不改变库存");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1); activeId = peer.Id;
            FormalDungeonTcpTests.EntryTown(peer);
            byte[] map = FormalDungeonTcpTests.Enter(peer);
            string before = Assets(); Use(peer, 65, 6, 1108); Refused(peer, 6, 1108); peer.Barrier();
            That(Assets() == before, "正式Loading阶段拒绝药剂，完整失败收尾后仍可加载");
            FormalDungeonTcpTests.Load(peer);
            Use(peer, 65, 7, 1108); Accepted(peer, 65, 7, 1108); peer.Barrier();
            That(Count(65) == 1, "真实正式Dungeon注册44成功消费，ACK先于后继合法请求且无源14/伪血蓝包");
            ushort monster = U16(map, 25);
            peer.Send(39, FormalDungeonTcpTests.Death(monster, peer.Id, map[31]));
            peer.Expect(0, 38); peer.Expect(0, 37); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id) is [[1L]],
                "药剂成功后当前房间真实死亡事务和完整回应可继续，不借切房重置恢复连接");
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 24); peer.Expect(0, 21); peer.Barrier();
        }

        string Assets() => fixture.Snapshot() + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        int Count(short slot) => fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.list_type=0 AND s.slot_index=$p0 AND s.character_id=$p1", slot, activeId)
            is [var row] ? System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)row[0])!.Count : 0;
        static void Use(TcpPeer peer, ushort slot, uint check, int id, int argument = 0) => peer.Send(44, Build(w =>
        { w.Write(slot); w.Write((byte)0); w.Write(check); w.Write(id); w.Write(argument); }));
        static void Accepted(TcpPeer peer, ushort slot, uint check, int id) => peer.Expect(1, 44, Build(w =>
        { w.Write((byte)1); w.Write(slot); w.Write((byte)0); w.Write(check); w.Write(id); }));
        static void Refused(TcpPeer peer, uint check, int id) => peer.Expect(1, 44, Build(w =>
        { w.Write((byte)0); w.Write((ushort)123); w.Write((byte)0); w.Write(check); w.Write(id); }));
    }
}
