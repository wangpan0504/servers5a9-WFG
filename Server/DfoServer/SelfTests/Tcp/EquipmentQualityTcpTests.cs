using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EquipmentQualityTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        Inventory.MasterQualityBoxTests.RunGoldRules();
        foreach (var (id, tags) in new[]
        {
            (10006368, new[] { "weapon" }), (10006369, new[] { "coat", "pants", "shoulder", "waist", "shoes" }),
            (10006370, new[] { "amulet", "ring", "wrist" }), (490000155, new[] { "support", "magic stone" }),
            (490000114, new[] { "support", "magic stone" }), (490000115, new[] { "coat", "pants", "shoulder", "waist", "shoes" }),
            (490000652, new[] { "weapon", "coat", "pants", "shoulder", "waist", "shoes", "amulet", "ring", "wrist", "support", "magic stone" }),
            (2683895, new[] { "weapon" }), (2683896, new[] { "coat", "pants", "shoulder", "waist", "shoes" }),
            (2683897, new[] { "amulet", "ring", "wrist" }), (2683898, new[] { "support", "magic stone" }),
            (10007452, new[] { "weapon" }), (10007453, new[] { "coat", "pants", "shoulder", "waist", "shoes" }),
            (10008492, new[] { "weapon" })
        })
        {
            var gold = items.Items.Get(id).QualityBox;
            That(gold is { Fixed: true, MinimumPercent: 100, MaximumPercent: 100 } &&
                gold.EquipmentSlots.SequenceEqual(tags.Select(ItemCatalog.Slot)) &&
                gold.Roll(_ => 0) == 999_999_998 && gold.Roll(max => max - 1) == 999_999_998,
                "真实黄金箱按资源固定最高品级及声明部位，无动作和普通动作均不得降为随机: " + id);
        }
        foreach (int id in new[] { 2683893, 2683894 })
            That(items.Items.Get(id).QualityBox is null, "黄金箱礼包不是直接调整策略: " + id);
        foreach (int id in new[] { 15, 897, 490010725, 490011801, 490011659, 490012245 })
            That(items.Items.Get(id).QualityBox is { Fixed: false }, "普通箱按本版旧ID及PVF动作解析: " + id);
        foreach (int id in new[] { 490012190, 490012048, 490012353, 490013009, 490014203, 490705117, 490704426, 490704958 })
            That(items.Items.Get(id).QualityBox is { MinimumPercent: 80, MaximumPercent: 99 }, "耀银各版本按PVF80..99解析: " + id);
        foreach (var (id, rarity) in new[] { (10150860, 2), (10150861, 3), (10150863, 4), (10150903, 5), (10150862, 6) })
            That(items.Items.Get(id).QualityBox is { Fixed: true, MinimumPercent: 100, MaximumPercent: 100, RequiredRarity: var actual, RejectTitle: true } &&
                actual == rarity, "真实大师级品级箱按稀有度固定最高品级并拒绝称号: " + id);
        var silver = items.Items.Get(490014203).QualityBox!;
        That(silver.Roll(_ => 0) == 0x3B9AC9E5 && silver.Roll(max => max - 1) == 0x3B9AC9F8 &&
            (silver with { MinimumPercent = 100, MaximumPercent = 100 }).Roll(_ => 0) == 999999998,
            "固定品级编码对应当前客户端解码80/99/100，而非把普通随机种子乘百分比");
        foreach (int size in new[] { 0, 8, 9, 11 })
            Reject<InvalidDataException>(() => EquipmentQualityRequests.Read(new byte[size], false), "本版81必须10B");

        var original = InventoryRewardGrantService.Create(items.Items.Get(100060156)) with
        {
            Value = 12345, Attr = 7, EnchantCardId = 3175, EnchantUpgrade = 2, AmplifyType = 1, AmplifyValue = 42,
            Rune = 8, GenuineUpgrade = 3, SealFlag = 1, Extension97 = 0x12345678, GuardianGemKeys = 0x123456789ABCDEF0,
            Random0 = new(70, 12, 13), Random1 = new(180, 20, 21), Random2 = new(130, 30, 31),
            RandomState = 7, RandomChangedIndex = 255
        };
        ItemCore Box(int id, int count) => InventoryRewardGrantService.Create(items.Items.Get(id)) with { Value = count, ExpireTime = 0 };
        var role = new Character(1, 0, "QualityRules", 0, resources.Towns.Room, null)
        { Inventory = new([new(1, Bag(9), original), new(2, Bag(103), Box(15, 1))], Gold: 1234) };
        var firstService = new InventoryRandomOptionService(items.Items, items.RandomOptions, _ => 0);
        var first = firstService.ResetQuality(role, new(9, original.ItemId, 103, 0, false));
        That(first.Accepted && first.Plan!.After.Find(Bag(9))!.Core.Random0 == new RandomOption(70, 0, 0) &&
            first.Plan.After.Find(Bag(9))!.Core.Random1 == original.Random1 && first.Plan.After.Find(Bag(9))!.Core.Random2 == original.Random2 &&
            first.Plan.After.Find(Bag(103)) is null && first.Plan.After.Gold == 1234,
            "普通箱C档边界和特殊/无品级词条排除来自本版PVF，一只箱子不附加金币或商店材料费");
        var last = new InventoryRandomOptionService(items.Items, items.RandomOptions, max => max - 1)
            .ResetQuality(role, new(9, original.ItemId, 103, 0, false));
        That(last.Plan!.After.Find(Bag(9))!.Core.Random0 == new RandomOption(70, 55, 55), "S档上边界不落入下一个区间");
        var title = InventoryRewardGrantService.Create(items.Items.Get(26596)) with { Value = 12, Random0 = original.Random0 };
        var titleRole = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(role.Inventory.Items[0], new(1, Bag(9), title)) } };
        var titleResult = firstService.ResetQuality(titleRole, new(9, title.ItemId, 103, 0, false));
        That(items.Items.Get(title.ItemId).EquipmentSlot == 13 && titleResult.Accepted &&
            titleResult.Plan!.After.Find(Bag(9))!.Core == title with { Value = 999_999_894 }, "称号只调整基础品级，不改附加词条");
        foreach (int id in new[] { 2683896, 490000652 })
        {
            var goldRole = role with { Inventory = role.Inventory with
                { Items = role.Inventory.Items.Replace(role.Inventory.Items[1], new(2, Bag(103), Box(id, 1))) } };
            var goldResult = firstService.ResetQuality(goldRole, new(9, original.ItemId, 103, 0, false));
            That(goldResult.Accepted && goldResult.Plan!.After.Find(Bag(9))!.Core == original with { Value = 999_999_998 } &&
                goldResult.Plan.After.Find(Bag(103)) is null && goldResult.Plan.ChangedSlots.SequenceEqual(new[] { Bag(9), Bag(103) }),
                "两类真实黄金箱从非最高品级均只改Value并消耗一只，完整魔封和其他核心保持: " + id);
        }

        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "equipment-quality"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("QualityTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Skills = resources.Characters.Get(0).Skills, AcceptedStoryLevel = 1, DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0, Bag(9), original), new(0, Bag(10), original with { EquipmentLockId = 1 }),
                    new(0, Bag(11), original with { IsRental = true }), new(0, Bag(12), title),
                    new(0, Bag(103), Box(15, 2)), new(0, Bag(104), Box(490014203, 1)),
                    new(0, Bag(105), Box(15, 1) with { ExpireTime = 1 }),
                    new(0, Bag(106), Box(15, 1) with { EquipmentLockId = 1 }), new(0, Bag(13), Box(10400, 1)),
                    new(0, Bag(109), Box(2683896, 2)), new(0, Bag(110), Box(490000652, 1)),
                    new(0, Bag(111), Box(2683895, 1))], Gold: 1234)
            });
        long uid = Convert.ToInt64(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=9")[0][0]);
        await fixture.StartAsync();
        ItemCore? saved = null;
        using (var peer = Open(fixture))
        {
            peer.Send(81, Request(9, original.ItemId, 103)); peer.Expect(1, 81, [0, 19, 0]);
            peer.Select(inventoryNotices: [(0, 251)]);
            string baseline = Snapshot();
            foreach (var (slot, id, material, error) in new (short, int, short, byte)[]
            { (9, 1, 103, 19), (10, original.ItemId, 103, 0xD5), (11, original.ItemId, 103, 19),
                (9, original.ItemId, 105, 17), (9, original.ItemId, 106, 0xD5), (9, original.ItemId, 13, 17), (9, original.ItemId, 108, 17),
                (9, original.ItemId, 111, 19), (12, title.ItemId, 109, 19),
                (10, original.ItemId, 109, 0xD5), (11, original.ItemId, 109, 19) })
            {
                peer.Send(81, Request(slot, id, material)); peer.Expect(1, 81, [0, error, 0]);
            }
            peer.Send(81, new byte[8]); peer.Expect(1, 81, [0, 19, 0]);
            peer.Send(455, [9, 0, 104, 0, 0, 0]); peer.Expect(1, 455, [0, 19, 0]); peer.Barrier();
            peer.Send(455, [9, 0, 109, 0, 0, 0]); peer.Expect(1, 455, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == baseline, "身份、锁、租赁、过期、空槽、错误材料及耀银魔封入口全部无扣除");
            Apply(peer, 81, Request(9, original.ItemId, 109), 109, 1);
            saved = Read();
            That(items.Items.Get(original.ItemId).EquipmentSlot == ItemCatalog.Slot("coat") &&
                saved == original with { Value = 999_999_998 },
                "无动作黄金防具箱取最高品级，只扣一只并保留魔封及完整装备核心");
            Apply(peer, 81, Request(9, original.ItemId, 110), 110, 0, targetUnchanged: true);
            That(Read() == saved, "带普通动作的黄金箱仍固定最高品级，完整核心不随机改写");
            baseline = Snapshot();
            peer.Send(81, Request(9, original.ItemId, 110)); peer.Expect(1, 81, [0, 17, 0]); peer.Barrier();
            That(Snapshot() == baseline, "黄金箱耗尽重发不重复修改或扣除");
            Apply(peer, 81, Request(9, original.ItemId, 104), 104, 0);
            saved = Read();
            That(saved.Value is >= 999999973 and <= 999999992 && saved with { Value = original.Value } == original,
                "耀银固定最上级，完整保留魔封、强化、附魔、增幅、锻造和扩展核心");
            baseline = Snapshot();
            peer.Send(81, Request(9, original.ItemId, 104)); peer.Expect(1, 81, [0, 17, 0]); peer.Barrier();
            That(Snapshot() == baseline, "最后一只箱子消耗后重发不能再次调整");
            Apply(peer, 81, Request(9, original.ItemId, 103), 103, 1);
            saved = Read();
            That(Displayable(saved.Value) && saved.Random0.Type == 70 && saved.Random0.Value1 < 56 && saved.Random0.Value2 < 56 &&
                saved with { Value = original.Value, Random0 = original.Random0 } == original,
                "普通箱只改基础品级及可变魔封值，品级种子落在客户端可显示区间，UID和其他全部核心字段保持");
            baseline = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_quality AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'quality rollback'); END");
            peer.Send(455, [9, 0, 103, 0, 255, 255]); peer.Closed();
            That(Snapshot() == baseline, "晚期写库失败同时回滚品级和箱子，不提前成功");
            fixture.Sql("DROP TRIGGER fail_quality");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(0, 251)]);
            That(Read() == saved, "断线重连保留已提交品级和词条，回滚结果不污染角色快照");
            string baseline = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_gold_quality AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'gold quality rollback'); END");
            peer.Send(81, Request(9, original.ItemId, 109)); peer.Closed();
            That(Snapshot() == baseline, "黄金箱晚期写库失败同时回滚最高品级、完整装备和材料，不提前成功");
            fixture.Sql("DROP TRIGGER fail_gold_quality");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(0, 251)]);
            That(Read() == saved, "黄金箱回滚后的重连恢复原核心");
            Apply(peer, 81, Request(9, original.ItemId, 109), 109, 0, targetUnchanged: saved!.Value == 999_999_998);
            saved = saved! with { Value = 999_999_998 };
            That(Read() == saved, "解除数据库故障后黄金箱可重新提交最高品级且仅消耗保留的一只");
            Apply(peer, 455, [9, 0, 103, 0, 255, 255], 103, 0);
            saved = Read();
            That(saved with { Value = original.Value, Random0 = original.Random0 } == original &&
                fixture.Query("SELECT gold FROM characters") is [[1234L]], "原生455入口消耗一只普通箱且不扣金币，不将末尾状态误作词条索引");
        }
        await fixture.StopAsync();

        ItemCore Read() => JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", uid).Single()[0]!)!;
        string Snapshot() => fixture.Snapshot() + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT count(*) FROM inventory_audit_log");
        void Apply(TcpPeer peer, ushort opcode, byte[] body, short material, int remaining, bool targetUnchanged = false)
        {
            var before = targetUnchanged ? Read() : null;
            if (targetUnchanged)
                That(opcode == 81 && before!.Value == 999_999_998 && remaining == 0,
                    "不变目标用例明确从最高品级开始，且消耗最后一只黄金箱");
            peer.Send(opcode, body);
            var delta = peer.Expect(0, 14).Body;
            if (targetUnchanged)
                That(delta.Length == 3 + ItemListProtocolWriter.EntrySize && delta[0] == 0 && U16(delta, 1) == 1 &&
                    U16(delta, 3) == material && U32(delta, 5) == uint.MaxValue && delta.AsSpan(9).IndexOfAnyExcept((byte)0) < 0,
                    "最高品级再次用黄金箱只发布实际变化的材料清空槽，目标不伪造差量");
            else
                That(delta[0] == 0 && U16(delta, 1) == 2, "只推目标和材料两格的权威增量");
            if (opcode == 81)
            {
                var ack = peer.Expect(1, 81).Body;
                That(ack.Length == 9 && ack[0] == 1 && U16(ack, 1) == material && U32(ack, 3) == remaining && U16(ack, 7) == 2,
                    "81成功9B供原生确认框读取材料剩余量及结果类型");
            }
            else peer.Expect(1, 455, [1]);
            peer.Barrier();
            if (targetUnchanged)
                That(Read() == before && fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=$p0", material).Length == 0,
                    "最高品级重复使用保留完整目标及UID，黄金箱确实耗尽，ACK仍返回实际剩余量");
        }
    }

    static bool Displayable(int seed) => seed == 999_999_998 || seed is >= 999_999_894 and <= 999_999_992;
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static byte[] Request(short slot, int id, short material)
    {
        byte[] body = new byte[10];
        BinaryPrimitives.WriteInt16LittleEndian(body, slot); BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), id);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(6), material);
        return body;
    }
}
