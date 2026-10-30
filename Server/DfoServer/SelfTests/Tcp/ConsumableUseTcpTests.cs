using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class ConsumableUseTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(new(DateTimeOffset.UtcNow.Date, TimeSpan.Zero));
        int[] fallbackPotionIds = [490012146, 490012453, 490012695, 490704250, 490704357, 490704395];
        int[] mappedPotionIds = [10309619, 490011948, 490013034, 490013050, 490013290,
            490013596, 490014083, 490705069, 490705106, 490705137];
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "consumable-use"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        ushort activeId;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("UsePotion", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
            var learning = SkillPointLedger.Synchronize(0, 0, 20, candidate.InitialLearning with
            { Experience = growth.GetLevelStartExperience(20) }, resources.Learning);
            var created = repository.Create(owner, candidate with { Level = 20, InitialLearning = learning,
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                    .Add(new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1108), 3)))
                    .Add(new(0, new(InventoryListType.Main, 66), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1113), 2)))
                    .Add(new(0, new(InventoryListType.Main, 67), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1016), 2)))
                    .Add(new(0, new(InventoryListType.Main, 68), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(6001), 2)))
                    .Add(new(0, new(InventoryListType.Main, 69), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1073), 2)))
                    .Add(new(0, new(InventoryListType.Main, 70), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(2600021), 2))) } });
            activeId = created.Id;
            repository.Create(owner, candidate with { Name = "DungeonDose", Level = 20, InitialLearning = learning,
                Inventory = candidate.Inventory with { Gold = checked((uint)resources.ItemResources.DevilContracts.RecoveryPrices[19]), Items = candidate.Inventory.Items
                    .Add(new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1113), 2)))
                    .Add(new(0, new(InventoryListType.Main, 68), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(6001), 2)))
                    .Add(new(0, new(InventoryListType.Main, 69), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1073), 2))) } });
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("BossPotion", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
            foreach (var (id, name) in new[] { (490705256, "BossPotion"), (490012682, "StrongBoss"), (490705059, "OneBoss") }
                .Concat(fallbackPotionIds.Select((id, index) => (id, "BossExtra" + index)))
                .Concat(mappedPotionIds.Select((id, index) => (id, "BossMapped" + index))))
            {
                bool detailed = fallbackPotionIds.Contains(id) || mappedPotionIds.Contains(id);
                var core = InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(id),
                    detailed ? 2 : id == 490705256 ? 3 : 1, clock.GetUtcNow());
                if (detailed) core = core with { Marker16 = 44, SortLockFlag = 1 };
                repository.Create(owner, candidate with { Name = name, Level = 95, InitialLearning = SkillPointLedger.Synchronize(0, 0, 95,
                candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(95) }, resources.Learning),
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), core)) } });
            }
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            Use(peer, 65, 0, 490705256); Accepted(peer, 65, 0, 490705256); peer.Barrier();
            That(fixture.Query("SELECT json_extract(state_json,'$.Remaining') FROM character_boss_selection_buff WHERE character_id=$p0", peer.Id) is [[5L]],
                "截图药水通过真实CMD44分派和完整ACK生效，五次效果已提交");
            Use(peer, 65, 1, 490705256); Refused(peer, 1, 490705256); peer.Barrier();
            That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.character_id=$p0 AND list_type=0 AND slot_index=65", peer.Id) is [[2L]],
                "一分钟冷却拒绝保留剩余两瓶，后续连接仍正常");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(2); peer.Barrier();
            That(fixture.Query("SELECT json_extract(state_json,'$.Remaining') FROM character_boss_selection_buff WHERE character_id=$p0", peer.Id) is [[5L]],
                "断线重连保留实际五次药效，不重复消费或补发");
        }
        foreach (var (slot, id, count, buff) in new[] { (3, 490012682, 5, 1275), (4, 490705059, 1, 1282) }
            .Concat(fallbackPotionIds.Select((id, index) => (index + 5, id, 5, 300)))
            .Concat(mappedPotionIds.Select((id, index) => (index + 11, id, 5, id == 490705137 ? 1275 : 300))))
        {
            Character? installed = null;
            using (var peer = Open(fixture))
            {
                peer.Select((ushort)slot, inventoryNotices: fallbackPotionIds.Contains(id) || mappedPotionIds.Contains(id) ? [(1, 713)] : null); peer.Barrier();
                var beforeUse = fallbackPotionIds.Contains(id) || mappedPotionIds.Contains(id) ? Read(peer.Id) : null;
                Use(peer, 65, 0, id); Accepted(peer, 65, 0, id); peer.Barrier();
                That(fixture.Query("SELECT json_extract(state_json,'$.Remaining'),json_extract(state_json,'$.Definition.BuffId') FROM character_boss_selection_buff WHERE character_id=$p0", peer.Id)
                    is [[var remaining, var kind]] && Equals(remaining, (long)count) && Equals(kind, (long)buff),
                    "原三款、六款兼容与十款原映射来源均经真实44消费，保存各自资源效果 " + id);
                if (beforeUse is not null)
                {
                    var source = beforeUse.Inventory.Find(new(InventoryListType.Main, 65))!;
                    // 固定时钟为UTC零点（北京时间08:00），下个06:00独立换算为本日UTC22:00。
                    long deadline = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date.AddHours(22), TimeSpan.Zero).ToUnixTimeMilliseconds();
                    That(source.Uid > 0 && source.Core.ItemId == id && source.Core is
                        { Kind: ItemKind.Consumable, Count: 2, Marker16: 44, SortLockFlag: 1 } &&
                        source.ExpireTime == (id is 490704395 or 490705069 ? deadline / 1000 : 0),
                        "补齐来源保留真实日删差异与非零完整核心，不抹去期限 " + id);
                    var expectedBuff = new BossSelectionBuff(id, count, deadline,
                        new(buff, 7191, count, [new(92689, buff == 1275 ? 0 : 40),
                            new(92690, buff == 1275 ? 0 : 40), new(92697, buff == 1275 ? 100 : 20)]));
                    var expectedInventory = beforeUse.Inventory with
                    {
                        Items = beforeUse.Inventory.Items.Select(row => row.Uid == source.Uid
                            ? row with { Core = row.Core with { Value = 1 } } : row).ToImmutableArray(),
                        BossSelectionBuff = expectedBuff
                    };
                    installed = Read(peer.Id);
                    That(installed.Revision == beforeUse.Revision + 1 &&
                        BossSelectionBuff.Same(installed.Inventory.BossSelectionBuff, expectedBuff) &&
                        JsonSerializer.Serialize(installed.Inventory) == JsonSerializer.Serialize(expectedInventory),
                        "补齐来源真实44同事务只扣原UID一瓶，完整核心和原期限不变，完整权重/次数/日截止准确且只提交一版 " + id);
                }
            }
            using (var peer = Open(fixture))
            {
                peer.Select((ushort)slot, inventoryNotices: fallbackPotionIds.Contains(id) || mappedPotionIds.Contains(id) ? [(1, 713)] : null); peer.Barrier();
                That(fixture.Query("SELECT json_extract(state_json,'$.Remaining') FROM character_boss_selection_buff WHERE character_id=$p0", peer.Id)
                    is [[var remaining]] && Equals(remaining, (long)count), "新药型重登保持真实次数，不赠药或续期");
                if (installed is not null)
                {
                    var restored = Read(peer.Id);
                    That(restored.Revision == installed.Revision &&
                        BossSelectionBuff.Same(restored.Inventory.BossSelectionBuff, installed.Inventory.BossSelectionBuff) &&
                        JsonSerializer.Serialize(restored.Inventory) == JsonSerializer.Serialize(installed.Inventory),
                        "补齐来源经新连接选角及新Repository完整恢复源UID/核心/期限、原权重/次数和日截止，不续期或补药 " + id);
                }
            }
        }
        using (var peer = Open(fixture))
        {
            string before = Assets();
            Use(peer, 65, 0, 1108); Refused(peer, 0, 1108);
            peer.Send(692, Name("OtherName")); peer.Expect(1, 692, [1]);
            That(Assets() == before, "未选角完整失败ACK收尾后连接继续，数量/审计不变");
            peer.Select();
            foreach (var item in new[] { (Slot: (ushort)68,Id: 6001), (Slot: (ushort)69,Id: 1073) })
            {
                before = Assets(); Use(peer,item.Slot,0,item.Id); Refused(peer,0,item.Id); peer.Barrier();
                That(Assets() == before,"城镇投掷及放置请求不能冒充已创建战斗效果，不扣源或推进冷却");
            }
            foreach (var invalid in new[] { (Slot: (ushort)65, Id: 1113, Check: 7u, Arg: 0), (Slot: (ushort)67, Id: 1047, Check: 8u, Arg: 0),
                (Slot: (ushort)65, Id: 1108, Check: 32768u, Arg: 0), (Slot: (ushort)65, Id: 1108, Check: 9u, Arg: 1) })
            {
                before = Assets(); Use(peer, invalid.Slot, invalid.Check, invalid.Id, invalid.Arg); Refused(peer, invalid.Check, invalid.Id);
                peer.Barrier(); That(Assets() == before, "错误源ID、高check和非零参数完整拒绝，不扣资源");
            }
            Use(peer, 65, 0, 1108); Accepted(peer, 65, 0, 1108); peer.Barrier();
            That(Count(65) == 2, "成功44原子扣指定源1件，ACK后只有合法屏障，无先行源14或伪造HP包");
            Use(peer, 65, 1, 1108); Refused(peer, 1, 1108); peer.Barrier();
            That(Count(65) == 2, "同组冷却拒绝保持连接及数量");
            Use(peer, 70, 0, 2600021); Accepted(peer, 70, 0, 2600021); peer.Barrier();
            That(Count(70) == 1, "精神刺激灵药经真实44应答并提交扣同UID一件");
            Use(peer, 70, 1, 2600021); Refused(peer, 1, 2600021); peer.Barrier();
            That(Count(70) == 1, "精神刺激原30分钟冷却生效，拒绝不扣件且连接可继续使用其它组物品");
            Use(peer, 66, 0, 1113); Accepted(peer, 66, 0, 1113); peer.Barrier();
            That(Count(66) == 1, "现场1113正常扣1件，组1不与组0别名，不因练习规则漏扣普通使用");
            Use(peer,67,0,1016); Accepted(peer,67,0,1016); peer.Barrier();
            That(Count(67) == 1,"附加属性效果普通waste物品同样扣源1件，不验证效果数值或伪造属性包");
            Use(peer, 66, 0, 1113); peer.Closed();
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
            Use(peer, 66, 4, 1113); Accepted(peer, 66, 4, 1113); peer.Barrier();
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
            string before = Assets(); Use(peer, 65, 6, 1113); Refused(peer, 6, 1113); peer.Barrier();
            That(Assets() == before, "正式Loading阶段拒绝药剂，完整失败收尾后仍可加载");
            FormalDungeonTcpTests.Load(peer);
            foreach (var item in new[] { (Slot: (ushort)68,Id: 6001), (Slot: (ushort)69,Id: 1073) })
            {
                Use(peer,item.Slot,0,item.Id); Accepted(peer,item.Slot,0,item.Id); peer.Barrier();
                That(Count((short)item.Slot) == 1,"普通副本throw/set完整ACK后实际扣1件，不额外扣除或发送源行造成重复消费");
                Use(peer,item.Slot,1,item.Id); Refused(peer,1,item.Id); peer.Barrier();
                That(Count((short)item.Slot) == 1,"投掷和放置仍执行各自PVF冷却，拒绝不改变实际数量");
            }
            Use(peer, 65, 7, 1113); Accepted(peer, 65, 7, 1113); peer.Barrier();
            That(Count(65) == 1, "现场1113在普通副本真实扣1件，ACK先于后继合法请求且无源14/伪血蓝包");
            ushort monster = U16(map, 25);
            peer.Send(39, FormalDungeonTcpTests.Death(monster, peer.Id, map[31]));
            peer.Expect(0, 38); peer.Expect(0, 37); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", peer.Id) is [[1L]],
                "药剂成功后当前房间真实死亡事务和完整回应可继续，不借切房重置恢复连接");
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0, 24); peer.Expect(0, 21); peer.Expect(0, 356);
            peer.Expect(0, 4, [10]); peer.Barrier();
            That(fixture.Query("SELECT stamina_until > 0 FROM characters WHERE id=$p0", peer.Id) is [[1L]], "撤退回城持久化虚弱并同步10%状态");
            peer.Send(9, [1, 0]); peer.Expect(1, 9, [0, 19, 0]); peer.Barrier();
            peer.Send(9, [0, 0]); peer.Expect(1, 9, [1,0,0,0,0]);
            peer.Expect(0, 14, [0,1,0,..new byte[123]]);
            peer.Expect(0, 4, [100]); peer.Barrier();
            That(fixture.Query("SELECT stamina_until,gold FROM characters WHERE id=$p0", peer.Id) is [[0L,0L]],
                "CMD9恢复清除持久虚弱并实扣当前等级价格，不用免费契约兜底扣费");
        }

        Character Read(ushort id)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return new CharacterRepository(fixture.Database).List(owner).Single(row => row.Id == id);
        }

        string Assets() => fixture.Snapshot() + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        int Count(short slot) => fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.list_type=0 AND s.slot_index=$p0 AND s.character_id=$p1", slot, activeId)
            is [var row] ? System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)row[0])!.Count : 0;
    }

    static void Use(TcpPeer peer, ushort slot, uint check, int id, int argument = 0) => peer.Send(44, Build(w =>
        { w.Write(slot); w.Write((byte)0); w.Write(check); w.Write(id); w.Write(argument); }));
    static void Accepted(TcpPeer peer, ushort slot, uint check, int id) => peer.Expect(1, 44, Build(w =>
        { w.Write((byte)1); w.Write(slot); w.Write((byte)0); w.Write(check); w.Write(id); }));
    static void Refused(TcpPeer peer, uint check, int id) => peer.Expect(1, 44, Build(w =>
        { w.Write((byte)0); w.Write((ushort)123); w.Write((byte)0); w.Write(check); w.Write(id); }));
}
