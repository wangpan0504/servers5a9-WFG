using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AmplifyUpgradeTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        AmplifyUpgradeTests.RunResources(resources);
        var r = resources.ItemResources;
        var core = InventoryRewardGrantService.Create(r.Items.Get(31440)) with { Attr = 0xE0, AmplifyType = 3, AmplifyValue = 6,
            EnchantCardId = 3175, EnchantUpgrade = 2, GenuineUpgrade = 4, Rune = 8, Random0 = new(1, 2, 3), Extension114 = new(1, 333) };
        var exchange = r.Items.Get(10007110);
        var range = ItemSlotBoundService.Range(exchange.Kind, 0);
        short exchangeSlot = checked((short)Enumerable.Range(range.Start, range.End - range.Start + 1)
            .First(slot => slot is not (19 or 65 or 66 or 67 or 68 or 121)));
        var exchangeCore = InventoryRewardGrantService.Create(exchange, 7) with { ExpireTime = 0 };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "amplification-upgrade"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("UpgradeTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 2, Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(2).Skills,
                Inventory = new([new(0, Bag(19), core), new(0, new(InventoryListType.Equipment, 12), core),
                    new(0, Bag(121), InventoryRewardGrantService.Create(r.Items.Get(3242), 1)),
                    new(0, Bag(65), InventoryRewardGrantService.Create(r.Items.Get(490011012), 1) with { ExpireTime = 0 }),
                    new(0, Bag(66), InventoryRewardGrantService.Create(r.Items.Get(10007407), 1) with { ExpireTime = 0 }),
                    new(0, Bag(67), InventoryRewardGrantService.Create(r.Items.Get(10007133), 2) with { ExpireTime = 0 }),
                    new(0, Bag(68), InventoryRewardGrantService.Create(r.Items.Get(10007839), 2) with { ExpireTime = 0 }),
                    new(0, Bag(exchangeSlot), exchangeCore)], Gold: 10_000_000)
            });
        string uid = fixture.Snapshot("SELECT item_uid FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=31440 ORDER BY item_uid");
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(80, EnhancementTests.Request(1, 0, 19, 31440, 121, new byte[17]));
            var first = peer.Expect(1, 80).Body;
            Check(first, 121, 0, 0, 1, 0, 19, 0);
            var gold = peer.Expect(0, 14).Body;
            uint remainingGold = checked((uint)(10_000_000 - r.AmplifyUpgrades.Rules.Gold(r.Items.Get(31440))));
            That(gold.Length == 126 && U16(gold, 3) == 0 && U32(gold, 9) == remainingGold,
                "普通NPC增幅提交后只补金币绝对余额，ACK已原位同步源和目标");
            peer.Barrier();
            That(Core(19, 0) == core with { Attr = 0xE1 } && fixture.Query("SELECT 1 FROM character_inventory_slots WHERE list_type=0 AND slot_index=121").Length == 0,
                "真实普通+1保存完整目标并耗尽原材料槽，不复制UID或丢扩展");
            string before = Snapshot();
            peer.Send(80, EnhancementTests.Request(1, 0, 19, 31440, 121, [])); peer.Expect(1, 80, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == before, "源耗尽重放拒绝，不再次扣金币或抽结果");

            fixture.Sql("CREATE TRIGGER fail_amplify_upgrade_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='equipment_amplification_portable' AND NEW.slot_index=68 BEGIN SELECT RAISE(ABORT,'upgrade rollback'); END");
            peer.Send(80, EnhancementTests.Request(1, 0, 19, 31440, 68, [])); peer.Closed();
            That(Snapshot() == before, "新便携增幅源、目标写入后审计失败整笔回滚，不提前成功或重复扣除");
            fixture.Sql("DROP TRIGGER fail_amplify_upgrade_late");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(80, EnhancementTests.Request(1, 3, 12, 31440, 65, []));
            Check(peer.Expect(1, 80).Body, 65, 0, 0, 1, 3, 12, 0);
            That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1, "穿戴目标原位增幅后仍刷新Actor和完整Player");
            peer.Barrier();
            for (byte old = 1; old <= 2; old++)
            {
                peer.Send(80,EnhancementTests.Request(1,3,12,31440,68,[]));
                Check(peer.Expect(1,80).Body,68,2-old,old,(byte)(old+1),3,12,0);
                That(peer.Expect(0,2).Body[0] == 0 && peer.Expect(0,2).Body[0] == 1,
                    "成功之兆使用同一mode1及27B回包，保持穿戴装备的Actor和Player刷新");
                peer.Barrier();
            }
            peer.Send(80, EnhancementTests.Request(5, 0, 19, 31440, 66, []));
            Check(peer.Expect(1, 80).Body, 66, 0, 1, 2, 0, 19, 4);
            peer.Barrier();
            That(Core(12, 3) == core with { Attr = 0xE3 } && Core(19, 0) == core with { Attr = 0xE2 },
                "普通及成功之兆便携、幸运均沿真实资源升级，异次元基础值和完整装备核心不变");
            for (int remaining = 1; remaining >= 0; remaining--)
            {
                peer.Send(80, EnhancementTests.Request(1, 0, 19, 31440, 67, []));
                Check(peer.Expect(1, 80).Body, 67, remaining, remaining == 1 ? (byte)2 : (byte)10, 10, 0, 19, 0);
                peer.Expect(0, 14);
                peer.Barrier();
                That(Core(19,0) == core with { Attr = 0xEA } && Core(exchangeSlot,0) == exchangeCore,
                    "真实+10增幅券只扣源券并保留装备全属性，兑换邀请函的UID、数量和核心不变");
            }
            string before = Snapshot();
            peer.Send(80, EnhancementTests.Request(1, 0, 19, 31440, 67, [])); peer.Expect(1,80,[0,23,0]); peer.Barrier();
            That(Snapshot() == before, "已耗尽增幅券的重放不改装备，也不扣仍持有的兑换材料");
            peer.Send(80,EnhancementTests.Request(1,3,12,31440,68,[])); peer.Expect(1,80,[0,23,0]); peer.Barrier();
            That(Snapshot() == before, "成功之兆源耗尽后的重放拒绝，不重复升级或扣费");
        }
        using (var peer = Open(fixture))
        {
            var selected = peer.Select(); peer.Barrier();
            var full = selected.Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            int at = Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).Single(index => U16(full, index) == 19);
            That(full[at + 10] == 0xEA && full[at + 19] == 3 && U16(full, at + 20) == 6 && Core(exchangeSlot,0) == exchangeCore &&
                Core(12,3) == core with { Attr = 0xE3 },
                "重新选角恢复增幅券和成功之兆的等级、完整属性，商店兑换材料数量不变");
        }
        That(fixture.Query("SELECT gold FROM characters") is [[long savedGold]] && savedGold == 10_000_000 - r.AmplifyUpgrades.Rules.Gold(r.Items.Get(31440)) &&
            uid == fixture.Snapshot("SELECT item_uid FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=31440 ORDER BY item_uid") &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "只收NPC一次金币，便携及幸运免费；UID、数据库及外键一致");

        ItemCore Core(short slot, byte list) => JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items JOIN character_inventory_slots USING(item_uid) WHERE list_type=$p0 AND slot_index=$p1", list, slot).Single().Single())!;
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") + fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
        static void Check(byte[] body, short material, int remaining, byte old, byte level, byte list, short target, byte path)
        {
            That(body.Length == 27 && body[0] == 1 && body[1] == 1 && U16(body, 2) == material && U32(body, 4) == remaining &&
                U16(body, 8) == ushort.MaxValue && body[10] == path && body[11] == old && body[12] == 0 && body[13] == level &&
                body[14] == list && U16(body, 15) == target && U16(body, 17) == ushort.MaxValue && U16(body, 19) == ushort.MaxValue &&
                U32(body, 21) == 0 && body[25] == 0 && body[26] == 0, "CMD80原生27B成功完整消费，模式、路径和余量独立核对");
        }
    }
}
