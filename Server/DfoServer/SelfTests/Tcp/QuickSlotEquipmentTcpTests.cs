using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class QuickSlotEquipmentTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        var old = items.Get(196386);
        var crest = items.Get(400360017);
        var finite = items.Get(400360000);
        That(old.IsQuickSlotEquipment && old.QuickSlotEquipment is { Heraldry: false } && old.Durability == 0 &&
            crest.IsQuickSlotEquipment && crest.QuickSlotEquipment is { Heraldry: true } && crest.Durability == 100 &&
            finite.IsQuickSlotEquipment && finite.QuickSlotEquipment is { Heraldry: false } && finite.Durability == 100,
            "真实PVF区分永久纹章、有限旧符咒与Heraldry；不从5000负重推导能量");
        That(items.ActionConsumable(2683829) is { Label: "[charm charging]", Parameters: [500, 100] },
            "精粹的符咒能量2683829按真实动作声明恢复100，上限500");
        await using var fixture = new TcpTestFixture(options, root, gm: true, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("QuickSlotTcp", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            created = repository.Create(owner, candidate with { Level = 5,
                InitialLearning = candidate.InitialLearning with { Experience = SelfTestCatalogs.Growth(resources.Archive).GetLevelStartExperience(5) },
                Inventory = candidate.Inventory with
            { Items = candidate.Inventory.Items
                .Add(new(0, Slot(9), InventoryRewardGrantService.Create(old) with { Value = 0, Attr = 3, EnchantCardId = 1234 }))
                .Add(new(0, Slot(10), InventoryRewardGrantService.Create(crest)))
                .Add(new(0, Slot(11), InventoryRewardGrantService.Create(finite) with { Durability = 75, Attr = 4, EnchantCardId = 1235 }))
                .Add(new(0, Slot(90), InventoryRewardGrantService.Create(items.Get(10004001), 3)))
                .Add(new(0, Slot(91), InventoryRewardGrantService.Create(items.Get(2683829), 3))) } });
        }
        var first = created.Inventory.Find(Slot(9))!;
        var second = created.Inventory.Find(Slot(10))!;
        var third = created.Inventory.Find(Slot(11))!;
        fixture.Sql("UPDATE character_fatigue SET used=maximum-1,remaining=1 WHERE character_id=$p0", created.Id);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            // 空快捷格在请求前面，与匹配客户端实际拖放顺序一致。
            Move(peer, 3, 0, 9, 196386); Ack(peer, 3, 9); peer.Barrier();
            That(Read(3) == first.Core && Uid(3) == first.Uid && Read(9) is null,
                "真实CMD19装入永久纹章，完整保存零Value、零耐久、UID及打造，无独立启用请求");
            Move(peer, 8, 0, 10, 400360017); Ack(peer, 8, 10); peer.Barrier();
            That(Read(3) == first.Core && Read(8) == second.Core && Uid(8) == second.Uid && Read(10) is null,
                "快捷栏已有纹章时允许继续拖入第二件快捷栏装备，不返错误23");
            Move(peer, 3, 196386, 11, 400360000); Ack(peer, 3, 11); peer.Barrier();
            That(Read(3) == third.Core && Uid(3) == third.Uid && Uid(11) == first.Uid && Uid(8) == second.Uid,
                "多件快捷栏装备并存时按客户端右键顺序直接替换首格成功，品级、耐久不互换，不重建实例");
            Move(peer, 10, 0, 11, 196386); Ack(peer, 10, 11); peer.Barrier();
            Move(peer, 8, 400360017, 3, 400360000); Ack(peer, 8, 3); peer.Barrier();
            Move(peer, 11, 0, 3, 400360017); Ack(peer, 11, 3); peer.Barrier();
            string before = Assets();
            peer.Send(23, [0, 255, 255, 255, 255, 0, 0, 0]);
            peer.Expect(1, 23, Build(w =>
            { w.Write((byte)1); w.Write(created.Inventory.Gold); w.Write((byte)0); w.Write((short)-1); w.Write((ushort)0); }));
            peer.Barrier();
            peer.Send(23, [0, 8, 0, 255, 255, 0, 0, 0]); peer.Expect(1, 23, [0, 19, 0]); peer.Barrier();
            That(Assets() == before && Read(8) == third.Core,
                "旧符咒75耐久不被普通一键修理补满，单件修理也拒绝且不扣费用，连接仍可继续");
            Move(peer, 11, 400360017, 8, 400360000); Ack(peer, 11, 8); peer.Barrier();
            before = Assets();
            Charge(peer, 10, 90); Charged(peer, 10, 1, 0); peer.Barrier();
            That(Assets() == before && Read(10) == first.Core,
                "永久纹章不消费充能券，也不把其品级改为人工能量");
            Charge(peer, 11, 90); Charged(peer, 11, 0, 20);
            var delta = peer.Expect(0, 14).Body; peer.Barrier();
            That(Read(11) == third.Core with { Durability = 95 } && Read(90)?.Count == 2 && Uid(11) == third.Uid &&
                delta.Length == 3 + 123 * 2 && delta[0] == 0 && U16(delta, 1) == 2 && U16(delta, 3) == 11 &&
                U16(delta, 3 + 11) == 95 && U32(delta, 3 + 6) == (uint)third.Core.Value,
                "真实1113恢复耐久20；独立读取N14的offset11为95，offset6品级不变，完整核心原子保存");
            Charge(peer, 11, 90); Charged(peer, 11, 0, 5); peer.Expect(0, 14); peer.Barrier();
            before = Assets();
            Charge(peer, 11, 90); Charged(peer, 11, 4, 0); peer.Barrier();
            Charge(peer, 8, 90); Charged(peer, 8, 1, 0); peer.Barrier();
            peer.Send(1113, new byte[20]); Charged(peer, 0, 1, 0); peer.Barrier();
            That(Assets() == before && Read(11)?.Durability == 100 && Read(90)?.Count == 1,
                "恢复5后满能量、新纹章与短包拒绝均不消费，客户端有明确业务结果且连接可继续");
            Charge(peer, 11, 91); Charged(peer, 11, 0, 100); peer.Expect(0, 14); peer.Barrier();
            before = Assets();
            fixture.Sql("CREATE TRIGGER fail_charm_charge AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'charm rollback'); END");
            try
            {
                Charge(peer, 11, 91); peer.Closed();
                That(Assets() == before, "充能晚期SQL失败整体回滚，能量、源券及审计均不留下半笔结果");
            }
            finally { fixture.Sql("DROP TRIGGER fail_charm_charge"); }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            That(Read(8) == second.Core && Uid(8) == second.Uid && Uid(10) == first.Uid &&
                Read(10) == first.Core && Read(11) == third.Core with { Durability = 200 } && Read(91)?.Count == 2,
                "新连接恢复真实位置、充能后完整核心及回滚前源券余额，没有创建Equipment31副本");
            Charge(peer, 11, 91); Charged(peer, 11, 0, 100); peer.Expect(0, 14); peer.Barrier();
            That(Read(11) == third.Core with { Durability = 300 } && Read(91)?.Count == 1,
                "回滚后重新使用仍成功恢复100，源券余额与目标能量在同一事务提交");
            Move(peer, 8, 400360017, 12, 0); Ack(peer, 8, 12); peer.Barrier();
            Move(peer, 10, 196386, 6, 0); Ack(peer, 10, 6); peer.Barrier();
            FormalDungeonTcpTests.EntryTown(peer);
            var map = FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);
            Charge(peer, 11, 91); Charged(peer, 11, 0, 100); peer.Expect(0, 14); peer.Barrier();
            That(Read(11) == third.Core with { Durability = 400 } && Read(91) is null,
                "没有地点限制的充能券可在已加载副本正常使用，不借城镇操作标记额外拒绝");
            string before = Assets();
            Move(peer, 6, 196386, 13, 0); peer.Expect(1, 19, [0, 23, 0, 0, 0]); peer.Barrier();
            That(Assets() == before && Uid(6) == first.Uid, "正式副本装卸拒绝保留源，失败后连接仍能完成屏障");
            int row = Enumerable.Range(0, map[18]).First(index => map[19 + index * 22 + 13] != 9);
            int at = 19 + row * 22;
            ulong previous = checked((ulong)Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", created.Id)[0][0]));
            peer.Send(39, FormalDungeonTcpTests.Death(U16(map, at + 6), peer.Id, map[at + 12]));
            var notices = PartyDungeonTcpTests.Drain(peer);
            var exp = notices.Single(packet => packet.Kind == 0 && packet.Opcode == 37).Body;
            ulong gained = BinaryPrimitives.ReadUInt64LittleEndian(exp.AsSpan(1)) - previous;
            That(exp.Length == 91 && gained > 0 && U32(exp, 51) > 0 && U32(exp, 51) == U32(exp, 55) &&
                gained * 2 == U32(exp, 51) * 3UL,
                "永久纹章即使Value与耐久均为0仍有200%经验，N37单列Buff并扣除基础余项，不重复授予");
            peer.Send(39, FormalDungeonTcpTests.Death(U16(map, at + 6), peer.Id, map[at + 12]));
            That(!PartyDungeonTcpTests.Drain(peer).Any(packet => packet.Kind == 0 && packet.Opcode == 37),
                "重复死亡不会重复发放装备经验或播报");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            Move(peer, 11, 400360000, 6, 196386); Ack(peer, 11, 6); peer.Barrier();
            string fatigue = fixture.Snapshot("SELECT * FROM character_fatigue");
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);
            peer.Send(48, [31]); peer.Barrier();
            That(Read(6) == third.Core with { Durability = 400 } && fixture.Snapshot("SELECT * FROM character_fatigue") == fatigue,
                "无限疲劳不实际扣余额或符咒能量，通用CMD48类型31也不额外扣能量");
        }
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out var mode) &&
            mode == "OK {\"infiniteFatigue\":false}", "耗能回归在隔离服务关闭无限疲劳");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            string before = Assets() + fixture.Snapshot("SELECT revision FROM characters") + fixture.Snapshot("SELECT * FROM character_fatigue");
            fixture.Sql("CREATE TRIGGER fail_fatigue_energy AFTER UPDATE ON character_fatigue BEGIN SELECT RAISE(ABORT,'fatigue energy rollback'); END");
            try
            {
                peer.Send(37, new byte[8]); peer.Closed();
                That(Assets() + fixture.Snapshot("SELECT revision FROM characters") + fixture.Snapshot("SELECT * FROM character_fatigue") == before,
                    "能量写后疲劳SQL失败不发成功帧，完整回滚物品核心、余额、版本及审计");
            }
            finally { fixture.Sql("DROP TRIGGER fail_fatigue_energy"); }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);
            var delta = peer.Expect(0, 14).Body;
            peer.Barrier();
            That(Read(6) == third.Core with { Durability = 399 } && Uid(6) == third.Uid &&
                fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", created.Id) is [[0L]] &&
                delta.Length == 126 && delta[0] == 0 && U16(delta, 1) == 1 && U16(delta, 3) == 6 &&
                U16(delta, 3 + 11) == 399 && U32(delta, 3 + 6) == (uint)third.Core.Value,
                "最后一点真实疲劳与能量同减1，提交后N14只刷新自己的装备格且保留UID、品级与打造");
            string before = Assets() + fixture.Snapshot("SELECT * FROM character_fatigue");
            peer.Send(37, new byte[8]);
            That(!PartyDungeonTcpTests.Drain(peer).Any(packet => packet.Kind == 0 && packet.Opcode is 14 or 1092),
                "重复加载不重新扣能量或发送空消费者1092");
            peer.Send(48, [31]); peer.Barrier();
            That(Assets() + fixture.Snapshot("SELECT * FROM character_fatigue") == before,
                "有限疲劳模式同样忽略重复的类型31耐久上报，不触发二次扣减或自动修理");
        }

        ItemCore? Read(short slot) => fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
            "WHERE s.character_id=$p0 AND list_type=0 AND slot_index=$p1", created.Id, slot) is [[string json]]
            ? JsonSerializer.Deserialize<ItemCore>(json) : null;
        long Uid(short slot) => fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=$p1",
            created.Id, slot) is [[long uid]] ? uid : 0;
        string Assets() => fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") + fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
    }

    static InventorySlot Slot(short slot) => new(InventoryListType.Main, slot);
    static void Move(TcpPeer peer, short source, int id, short target, int targetId) => peer.Send(19, Build(w =>
    {
        w.Write((byte)0); w.Write(source); w.Write(id); w.Write(0);
        w.Write((byte)0); w.Write(target); w.Write(targetId); w.Write(0); w.Write(-1); w.Write((byte)0); w.Write((byte)0);
    }));
    static void Ack(TcpPeer peer, short source, short target) => peer.Expect(1, 19, Build(w =>
    { w.Write((byte)1); w.Write((byte)0); w.Write(source); w.Write(1); w.Write((byte)0); w.Write(target); }));
    static void Charge(TcpPeer peer, int target, int source) => peer.Send(1113, Build(w =>
    { w.Write(Enumerable.Repeat((byte)0xA5, 13).ToArray()); w.Write(target); w.Write(source); }));
    static void Charged(TcpPeer peer, int target, uint status, int restored) => peer.Expect(1, 1113, Build(w =>
    { w.Write((byte)1); w.Write(status); w.Write(target); w.Write(0u); w.Write(restored); }));
}
