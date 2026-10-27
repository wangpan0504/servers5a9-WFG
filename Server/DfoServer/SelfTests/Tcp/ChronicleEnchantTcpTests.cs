using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ChronicleEnchantTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int gearId = 100300119;
        var items = resources.ItemResources.Items;
        var catalog = resources.ItemResources.ChronicleEnchants;
        var gear = items.Get(gearId);
        foreach (int id in new[] { 1299, 1300, 1301 })
        {
            var definition = catalog.Get(id);
            That(definition is { FirstChance: 100, SecondChance: 100 } &&
                catalog.Allows(gearId, 12, 1, gear.EquipmentSlot, definition, 0), "真实闪亮气息通过自身导入表核验骑士裂魂项链选项");
        }
        foreach (int id in new[] { 4735, 4736, 4737, 4738 })
            That(catalog.Get(id) is null, "同名任务材料没有赋予表，不能当成可用气息");
        ChronicleEnchantTests.RunDestruction(resources, gearId);
        var rich = InventoryRewardGrantService.Create(gear) with
            { Attr = 0xEB, EnchantCardId = 3175, EnchantUpgrade = 2, GenuineUpgrade = 4, Rune = 5, Extension114 = new(1, 333) };
        var initial = new List<InventoryItem> { new(0, Main(9), rich), new(0, Main(10), rich) };
        for (short offset = 0; offset < 3; offset++)
            initial.Add(new(0, Main((short)(65 + offset)), InventoryRewardGrantService.Create(items.Get(1299 + offset), 2)));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "chronicle-enchant"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("ChronicleTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 12, GrowType = 1, Level = 80, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(12).Skills,
                Inventory = new([.. initial], Gold: 1234) });
        string originalUids = fixture.Snapshot("SELECT item_uid FROM character_inventory_items ORDER BY item_uid");
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string unchanged = fixture.Snapshot();
            peer.Send(370, new byte[13]); peer.Expect(1, 370, [0, 17, 0]);
            peer.Send(370, ChronicleEnchantTests.Request(65, 1299, 0, 9, gearId + 1, 0)); peer.Expect(1, 370, [0, 4, 0]);
            peer.Send(370, ChronicleEnchantTests.Request(65, 1299, 0, 9, gearId, 255)); peer.Expect(1, 370, [0, 19, 0]);
            peer.Barrier();
            That(fixture.Snapshot() == unchanged, "畸形、身份和技能伪造有失败回执，资产/审计零变化且连接继续");
            fixture.Sql("CREATE TRIGGER fail_chronicle_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='chronicle_enchant' AND NEW.slot_index=9 BEGIN SELECT RAISE(ABORT,'chronicle rollback'); END");
            peer.Send(370, ChronicleEnchantTests.Request(65, 1299, 0, 9, gearId, 0)); peer.Closed();
            That(fixture.Snapshot() == unchanged, "目标和气息更新后的SQL故障整笔回滚，不提前发布成功或目标");
            fixture.Sql("DROP TRIGGER fail_chronicle_late");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Use(peer, 65, 1299, 9);
            That(Saved(9) == rich with { Chronicle0 = new(1254, 12, 1, 19, 0) } && Saved(65).Count == 1,
                "原生效果编号与当前消费变体分开，保存完整源/目标和原UID");
            string unchanged = fixture.Snapshot();
            peer.Send(370, ChronicleEnchantTests.Request(65, 1299, 0, 9, gearId, 0)); peer.Expect(1, 370, [0, 19, 0]); peer.Barrier();
            That(fixture.Snapshot() == unchanged, "同技能同色重放拒绝，不重复扣气息或写审计");
            Use(peer, 66, 1300, 9);
            That(Saved(9) == rich with { Chronicle0 = new(1254, 12, 1, 19, 0), Chronicle1 = new(1255, 12, 1, 19, 0) },
                "真实同技能可追加不同气息，保留第一条及其他全部核心");
            unchanged = fixture.Snapshot();
            peer.Send(370, ChronicleEnchantTests.Request(67, 1301, 0, 9, gearId, 0)); peer.Expect(1, 370, [0, 19, 0]); peer.Barrier();
            That(fixture.Snapshot() == unchanged, "满两条时整笔拒绝，不能覆盖已有属性");
            Use(peer, 67, 1301, 10);
            That(Saved(10) == rich with { Chronicle0 = new(1256, 12, 1, 19, 0) }, "真实绿色气息读取自己的导入表");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restored");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            That(Saved(9).Chronicle1 == new ChronicleOption(1255, 12, 1, 19, 0) && Saved(10).Chronicle0.Id == 1256 &&
                new short[] { 65, 66, 67 }.All(slot => Saved(slot).Count == 1), "服务重建、重选恢复两条属性与三种实际材料余量");
        }
        That(fixture.Snapshot("SELECT item_uid FROM character_inventory_items ORDER BY item_uid") == originalUids &&
            fixture.Query("SELECT gold FROM characters") is [[1234L]] && fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0, "不收商店need material、金币或额外道具，UID和数据库完整");

        void Use(TcpPeer peer, short sourceSlot, int sourceId, short targetSlot)
        {
            peer.Send(370, ChronicleEnchantTests.Request(sourceSlot, sourceId, 0, targetSlot, gearId, 0));
            var clear = peer.Expect(0, 1681).Body;
            That(clear.Length == 126 && U16(clear, 1) == 1 && U16(clear, 3) == targetSlot && U32(clear, 5) == uint.MaxValue &&
                clear.AsSpan(9).IndexOfAnyExcept((byte)0) < 0,
                "原生结果窗打开前清旧目标引用，不重复扣源");
            var data = peer.Expect(0, 1681).Body;
            That(U16(data, 3) == targetSlot && U32(data, 5) == gearId && data[42] is 1 or 2,
                "ACK前发送实际提交的完整Chronicle核心");
            peer.Expect(1, 370, Build(writer => { writer.Write((byte)1); writer.Write(sourceSlot); writer.Write((ushort)1);
                writer.Write((byte)1); writer.Write((byte)0); writer.Write(targetSlot); writer.Write((byte)0); }));
            peer.Barrier();
        }
        ItemCore Saved(short slot) => JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items JOIN character_inventory_slots USING(item_uid) WHERE list_type=0 AND slot_index=$p0", slot).Single().Single())!;
        static InventorySlot Main(short index) => new(InventoryListType.Main, index);
    }
}
