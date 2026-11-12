using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class MagicBoxTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        Inventory.MagicBoxTests.RunRewardRowCore();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "magic-box"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var created = repository.Create(owner, new("SeriaBox", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, Level = 43, AcceptedStoryLevel = 43, Skills = resources.Characters.Get(15).Skills,
                Inventory = VideoInventory() with { AccountWallet = AccountWallet.Empty }
            });
            repository.CommitInventory(owner, created, new(created.Inventory,
                created.Inventory with { AccountWallet = VideoInventory().AccountWallet }, "magic_box_fixture"));
        }
        await fixture.StartAsync();
        // 独立历史客户端捕获：list4、槽70、2682272、无材料双哨兵、十连。
        byte[] request = Convert.FromHexString("044600A0ED2800FFFFFFFFFFFF0A00");
        InventorySnapshot committed;
        bool restoreExpertContract;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            peer.Send(713, [0, 121, 0]); peer.Expect(1, 713, [1, 0, 121, 0]);
            peer.Send(713, [0, 70, 0]); peer.Expect(1, 713, [1, 0, 70, 0]);
            peer.Send(1130, request);
            byte[] ack = peer.Expect(1, 1130).Body;
            That(ack.Length >= 15 && ack[0] == 4 && ack[1] == 4 && U16(ack, 3) == 10 &&
                U16(ack, 5) == 70 && U16(ack, 7) == ushort.MaxValue, "真实十连使用批开双列表及无材料哨兵");
            int count = U16(ack, 9), separator = 11 + 123 * count;
            That(count > 0 && separator + 4 <= ack.Length && U16(ack, separator) == 0 &&
                ack.Length == separator + 4 + U16(ack, separator + 2) * 123,
                "双栏奖励完整使用123字节条目且无截断或额外尾部");
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var saved = repository.List(owner).Single();
            committed = saved.Inventory;
            restoreExpertContract = saved.Learning.ExpertContractExpiresAt > 0;
            That(saved.Inventory.Find(new(InventoryListType.Main, 70)) is { Core.Count: 989 } &&
                saved.Inventory.AccountWallet.SeriaLuck == 1 && saved.Inventory.Items.All(item => item.Uid > 0),
                "Lv43空包、999源与已有余额下十连只扣10件，真实UID及幸运值一次保存");
            // 成功应答前事务已提交；空宠物夹具的实际奖励决定是否先发布105，不能跳过未知通知。
            if (committed.Items.Any(item => item.Creature is not null))
                CreatureDetails(peer.Expect(0, 105).Body, committed);
            foreach (byte list in new byte[] { 0, 2, 12, 1, 7, 38, 46 })
                That(peer.Expect(0, 13).Body[0] == list, "提交后重建实际物品容器：" + list);
            var restoredLocks = new HashSet<ushort>();
            for (int i = 0; i < 2; i++)
            {
                var locked = peer.Expect(1, 713).Body;
                That(locked.Length == 4 && locked[0] == 1 && locked[1] == 0, "开箱整列载入后恢复真实Main排序锁");
                restoredLocks.Add(U16(locked, 2));
            }
            That(restoredLocks.SetEquals(new ushort[] { 70, 121 }), "源数量变化与未变化的锁定物品均恢复蓝锁");
            ReadEffects(peer);
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(expertContract: restoreExpertContract, inventoryNotices: [(1, 713), (1, 713)])[4].Body;
            CreatureDetails(peer.SpecialContainers[0].Body, committed);
            peer.Barrier();
            Bytes(peer.SeriaLuckInfo, [5, 5], "重新选角向客户端恢复已提交的幸运值阶段1");
            var main = committed.Items.Where(item => item.Position.List == InventoryListType.Main).ToArray();
            That(full[0] == 0 && U16(full, 3) == main.Length + 15 && full.Length == 5 + U16(full, 3) * 123,
                "重新选角完整背包仅含已提交实体及15个钱包虚拟槽");
            foreach (var item in main)
                BoosterTcpTests.Entry(full, item.Position.Index, item.Core.ItemId, item.Core.Value, full: true);
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var restored = new CharacterRepository(fixture.Database).List(owner).Single();
            That(restored.Inventory.Find(new(InventoryListType.Main, 70)) is { Core.Count: 989 } &&
                restored.Inventory.AccountWallet.SeriaLuck == 1, "重新选角后持久化十连源数量及幸运值保持一致");
        }
        DungeonBox(fixture, resources, repository, restoreExpertContract);
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "随机盒真实TCP后的存档完整性");
        await RunPotAsync(source, root, resources);
    }

    static void DungeonBox(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repository, bool expertContract)
    {
        var places = resources.ItemResources.MagicBoxes.UsePlaces(2682272);
        That(places.Allows(new(StackableUsePlace.Dungeon)), "真实赛丽亚幸运盒地点允许正式副本");
        var candidate = CharacterCreation.Prepare("DungeonLucky", resources.Characters.Get(0), resources.Towns.Room,
            resources.Towns.InitialReturn, resources.Learning.Get);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            created = repository.Create(owner, candidate with
            {
                Level = 20, AcceptedStoryLevel = 1,
                InitialLearning = DfoServer.Game.Skills.SkillPointLedger.Synchronize(0, 0, 20,
                    candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(20) }, resources.Learning),
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(new(0,
                    new(InventoryListType.Main, 70), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(2682272), 2))) }
            });
        using var peer = Open(fixture);
        peer.Select(created.Slot, expertContract: expertContract);
        FormalDungeonTcpTests.EntryTown(peer);
        FormalDungeonTcpTests.Enter(peer);
        string before = fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
        peer.Send(208, [4, 70, 0]);
        peer.Expect(1, 208, [0]);
        peer.Barrier();
        That(before == fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid"), "加载阶段拒绝且不扣源");
        FormalDungeonTcpTests.Load(peer, fatigue: false);
        peer.Expect(0, 36);
        peer.Send(208, [4, 70, 0]);
        var ack = peer.Expect(1, 208).Body;
        That(ack.Length >= 9 && ack[0] == 1 && ack[1] == 4 && U16(ack, 3) == 70,
            "副本内单开返回真实成功结果");
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = repository.List(owner).Single(role => role.Id == created.Id);
            That(saved.Inventory.Find(new(InventoryListType.Main, 70)) is { Core.Count: 1 } &&
                saved.Inventory.AccountWallet.SeriaLuck == 2, "副本开盒提交源及账号幸运值");
        }
    }

    static async Task RunPotAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "magic-pot"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources.Items;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("MagicPot", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, 94), InventoryRewardGrantService.Create(items.Get(2681819), 2)),
                    new(0, new(InventoryListType.Main, 150), InventoryRewardGrantService.Create(items.Get(2681820), 1))])
            });
        await fixture.StartAsync();
        // 原生02760130发送类型3、魔罐Main槽、圣水Main槽；类型值不是库存域。
        byte[] request = [3, 94, 0, 150, 0];
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = State();
            foreach (byte[] invalid in new byte[][] { [3, 94, 0], [3, 150, 0, 94, 0], [3, 94, 0, 149, 0] })
            {
                peer.Send(208, invalid); peer.Expect(1, 208, [0]); peer.Barrier();
                That(State() == before, "魔罐短包、错误源槽及错误材料槽均不扣源、不发奖、不写审计");
            }
            fixture.Sql("CREATE TRIGGER fail_magic_pot BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'magic pot rollback'); END");
            peer.Send(208, request); peer.Closed();
            That(State() == before, "魔罐真实提交失败回滚源、圣水、奖励及UID，不提前成功");
            fixture.Sql("DROP TRIGGER fail_magic_pot");
        }
        InventorySnapshot committed;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            peer.Send(208, request);
            byte[] ack = peer.Expect(1, 208).Body;
            That(ack.Length == 132 && ack[0] == 1 && ack[1] == 3 && ack[2] == 0 && U16(ack, 3) == 94 &&
                U16(ack, 5) == 150 && U16(ack, 7) == 1 && U32(ack, 15) == 1,
                "魔罐ACK208回显类型3及两Main槽，单奖使用完整123B行和本次数量");
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
                committed = repository.List(owner).Single().Inventory;
            That(committed.Find(new(InventoryListType.Main, 94)) is { Core.Count: 1 } &&
                committed.Find(new(InventoryListType.Main, 150)) is null,
                "魔罐成功应答时一罐一圣水已原子持久化");
            short slot = (short)U16(ack, 9);
            int reward = (int)U32(ack, 11);
            That(resources.ItemResources.MagicBoxes.Get(2681819).Pools.Single().Rewards.Any(row => row.ItemId == reward) &&
                committed.Find(new(InventoryListType.Main, slot)) is { Core.Count: 1 } granted && granted.Core.ItemId == reward,
                "魔罐应答奖品与真实PVF及已提交Main槽一致");
            foreach (byte list in new byte[] { 0, 2, 12, 1, 7, 38, 46 })
                That(peer.Expect(0, 13).Body[0] == list, "魔罐提交后刷新实际物品容器：" + list);
            peer.Barrier();
            string after = State();
            peer.Send(208, request); peer.Expect(1, 208, [0]); peer.Barrier();
            That(State() == after, "圣水耗尽后的重复请求不再次扣罐或发奖");
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select()[4].Body;
            foreach (var item in committed.Items)
                BoosterTcpTests.Entry(full, item.Position.Index, item.Core.ItemId, item.Core.Value, full: true);
            That(!Enumerable.Range(0, U16(full, 3)).Any(index => U16(full, 5 + index * 123) == 150),
                "重新选角仅恢复已提交源与奖品，不恢复已耗尽圣水");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "魔罐拒绝、SQL回滚、成功及重连后的存档完整性");

        string State() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static void CreatureDetails(byte[] body, InventorySnapshot inventory)
    {
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream);
        var creatures = inventory.Items.Where(item => item.Creature is not null).OrderBy(item => item.Uid).ToArray();
        That(reader.ReadByte() == creatures.Length, "宠物明细数量与十连已提交奖励一致");
        foreach (var item in creatures)
        {
            var detail = item.Creature!;
            That(reader.ReadInt32() == item.Uid && reader.ReadByte() == detail.Satiety &&
                reader.ReadByte() == detail.Mode && reader.ReadInt32() == detail.Experience,
                "宠物真实UID、饱食度、模式及经验与落库详情一致");
            if (detail.Mode == 1)
                That(reader.ReadByte() == detail.Mode1Field0 && reader.ReadByte() == detail.Mode1Field1,
                    "宠物模式1的两个单字节字段完整");
            That(reader.ReadByte() == detail.Level, "宠物等级与落库一致");
            byte[] name = Encoding.GetEncoding(936).GetBytes(detail.Name);
            That(reader.ReadInt32() == name.Length, "宠物名称使用四字节CP936字节长度");
            Bytes(reader.ReadBytes(name.Length), name, "宠物名称与落库一致");
            That(reader.ReadByte() == detail.TailFlag, "宠物尾标志与落库一致");
        }
        That(stream.Position == stream.Length, "宠物明细无截断或额外尾部");
    }

    static InventorySnapshot VideoInventory() => new([new(0, new(InventoryListType.Main, 70),
        new(2682272, ItemKind.Consumable, 999)),
        new(0, new(InventoryListType.Main, 121), new(3299, ItemKind.Material, 2))])
    {
        Gold = 364755, ReviveCoins = 129,
        AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 99881120)
    };

    static void ReadEffects(TcpPeer peer)
    {
        peer.SendBarrier();
        bool gauge = false;
        while (true)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 })
            {
                Bytes(packet.Body, [1], "十连刷新后连接继续处理同连接请求");
                break;
            }
            That(packet.Kind == 0 && packet.Opcode is 53 or 412 or 1760 or 66 or 901 or 2 or 950 or 99 or 86,
                "十连仅发布已提交钱包、幸运值、契约、邮件及极品广播的真实通知");
            if (packet.Opcode == 86)
            {
                That(packet.Body.Length == 18 && packet.Body[0] == 18 && packet.Body[1] == 8 &&
                    Encoding.GetEncoding(936).GetString(packet.Body, 2, 8) == "SeriaBox" &&
                    U32(packet.Body, 10) == 2682272 && U32(packet.Body, 14) > 0,
                    "十连命中极品候选时广播NOTI 86子类型18");
            }
            if (packet.Opcode == 412) { Bytes(packet.Body, [0, 5], "十连0→1幸运值投影"); gauge = true; }
        }
        That(gauge, "十连提交后发布幸运值");
    }

}
