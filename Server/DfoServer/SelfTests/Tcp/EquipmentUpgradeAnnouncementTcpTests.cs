using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class EquipmentUpgradeAnnouncementTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        var items = resources.ItemResources;
        That(items.AmplifyUpgrades.ReinforceRules.NoticeLevel == 12 && items.AmplifyUpgrades.Rules.NoticeLevel == 12,
            "强化与增幅公告门槛分别读取匹配PVF的notice，不混用概率或写死其他等级");
        That(items.Enhancements.Get(490002014) is { Available: true, Level: 12, SuccessWeight: 10000 } &&
            items.Enhancements.Get(10008747) is { Available: true, Level: 12, SuccessWeight: 10000 } &&
            items.Items.Get(490705409).RefinementTicket is { Available: true, Level: 7, SuccessWeight: 10000 },
            "真实PVF三种等级券提供确定结果，回归不靠重抽或修改概率");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "equipment-upgrade-announcements"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var weapon = items.Items.Get(27308);
        var step = items.AmplifyUpgrades.Rules.Steps[12];
        byte refinedLevel = 6;
        var core = InventoryRewardGrantService.Create(weapon) with { Attr = 11, ExpireTime = 0 };
        foreach (var (account, name) in new[] { ("10000", "UpgradeA"), ("20011", "UpgradeB") })
        {
            using var owner = owners.Acquire(account, () => { });
            repository.Create(owner, new(name, resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = account == "10000" ? new([
                    new(0, Bag(21), core), new(0, Bag(22), core with { AmplifyType = 2, AmplifyValue = 21 }),
                    new(0, Bag(23), core with { Attr = 0, GenuineUpgrade = 6 }), new(0, Bag(24), core with { Attr = 0, GenuineUpgrade = 6 }),
                    new(0, Bag(65), Ticket(490002014, 3)), new(0, Bag(66), Ticket(10008747, 1)),
                    new(0, Bag(67), Ticket(490705409, 1)), new(0, Bag(68), Ticket(490705102, 2)),
                    new(0, Bag(69), Ticket(10007990, 1)), new(0, Bag(70), Ticket(10005312, 1)),
                    new(0, Bag(121), Ticket(step.MaterialId, step.MaterialCount * 2))], Gold: 10_000_000) : InventorySnapshot.Empty
            });
        }
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        using var observer = Open(fixture, "20011", gamePort: endpoints[11], channelNumber: 11);
        using (var actor = Open(fixture))
        {
            actor.Select();
            string snapshot = Snapshot();
            actor.Send(80, EnhancementTests.Request(0, 0, 21, weapon.Id + 1, 65, []));
            actor.Expect(1, 80, [0, 23, 0]); actor.Barrier();
            That(Snapshot() == snapshot, "拒绝不扣券、不改装备、不公告");
            Reinforce(actor);
            actor.Barrier();
            observer.Select();
            observer.Barrier();
            Reinforce(actor);
            observer.Barrier();
            actor.Send(80, EnhancementTests.Request(1, 0, 22, weapon.Id, 66, []));
            var amplified = actor.Expect(1, 80).Body;
            That(amplified.Length == 27 && amplified[0] == 1 && amplified[12] == 0 && amplified[13] == 12,
                "真实增幅券提交+12后才确认成功：" + Convert.ToHexString(amplified));
            actor.Expect(0, 14);
            actor.Barrier(); observer.Barrier();
            Refine(actor, 23, 67);
            actor.Expect(1, 438, Build(w =>
            {
                w.Write((byte)1); w.Write((short)67); w.Write(0); w.Write((byte)6);
                w.Write((byte)0); w.Write((byte)7); w.Write((byte)0); w.Write((short)23);
            }));
            actor.Expect(0, 14);
            actor.Barrier(); observer.Barrier();
            Refine(actor, 23, 69);
            actor.Expect(1, 438, Build(w =>
            {
                w.Write((byte)1); w.Write((short)69); w.Write(0); w.Write((byte)7);
                w.Write((byte)0); w.Write((byte)8); w.Write((byte)0); w.Write((short)23);
            }));
            actor.Expect(0, 14); actor.Barrier(); observer.Barrier();
            That(Core(21)?.Upgrade == 12 && Core(22)?.Upgrade == 12 && Core(23)?.GenuineUpgrade == 8,
                "真实强化、增幅、锻造和增量券成功照常消费、提升等级，但本人及同组其他频道均无公告");
            Step(actor, 0, 21, 70, "强化");
            Step(actor, 1, 22, 121, "增幅");
            Refine(actor, 24, 68);
            var refined = actor.Expect(1, 438).Body;
            That(refined.Length == 13 && refined[0] == 1 && refined[7] == 6 &&
                (refined[8] == 0 && refined[9] == 7 || refined[8] == 1 && refined[9] == 6),
                "便携炉沿普通逐级锻造表计算，回归接受本次真实成功或失败结果而不重抽");
            refinedLevel = refined[9];
            actor.Expect(0, 14);
            string refinement = $"玩家[UpgradeA]锻造 {weapon.Name} 第7阶段" + (refined[8] == 0 ? "成功" : "失败");
            ExpectText(actor, refinement); ExpectText(observer, refinement);
            actor.Barrier(); observer.Barrier();
            snapshot = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_upgrade_notice BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='equipment_refinement' BEGIN SELECT RAISE(ABORT,'announcement rollback'); END");
            Refine(actor, 24, 68); actor.Closed();
            observer.Barrier();
            That(Snapshot() == snapshot, "晚期SQL失败完整回滚，不向同组其他频道发成功公告");
            fixture.Sql("DROP TRIGGER fail_upgrade_notice");
        }
        using (var actor = Open(fixture))
        {
            actor.Select(); actor.Barrier();
            string snapshot = Snapshot();
            actor.Send(80, EnhancementTests.Request(1, 0, 22, weapon.Id, 66, []));
            actor.Expect(1, 80, [0, 23, 0]); actor.Barrier(); observer.Barrier();
            That(Snapshot() == snapshot, "重连后耗尽源券不能借旧公告重放资产或重复发成功消息");
        }
        using (var owner = owners.Acquire("10000", () => { }))
        {
            var saved = new CharacterRepository(fixture.Database).List(owner).Single();
            That(saved.Inventory.Find(Bag(23))?.Core.GenuineUpgrade == 8 && saved.Inventory.Find(Bag(24))?.Core.GenuineUpgrade == refinedLevel &&
                saved.Inventory.Gold == 10_000_000 - items.AmplifyUpgrades.Rules.Gold(weapon),
                "公告不改变真实券、逐级尝试和失败惩罚，不额外扣费，重开仓储恢复本次真实结果");
        }
        await fixture.StopAsync();
        Console.WriteLine($"Equipment upgrade announcement TCP: {Count - before} passed");

        ItemCore Ticket(int id, int count) => InventoryRewardGrantService.Create(items.Items.Get(id), count) with { ExpireTime = 0 };
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        void Reinforce(TcpPeer peer)
        {
            peer.Send(80, EnhancementTests.Request(0, 0, 21, weapon.Id, 65, []));
            var ack = peer.Expect(1, 80).Body;
            That(ack.Length == 27 && ack[0] == 1 && ack[12] == 0 && ack[13] == 12,
                "强化等级券先回真实成功结果：" + Convert.ToHexString(ack));
            peer.Expect(0, 14);
            peer.Barrier();
        }
        void Step(TcpPeer peer, byte mode, short target, short material, string kind)
        {
            peer.Send(80, EnhancementTests.Request(mode, 0, target, weapon.Id, material, []));
            var ack = peer.Expect(1, 80).Body;
            That(ack.Length >= 27 && ack[0] == 1 && ack[11] == 12 && ack[12] <= 3 &&
                (ack[12] != 0 || ack[13] == 13), "普通逐级尝试真实提交结果：" + Convert.ToHexString(ack));
            string text = $"玩家[UpgradeA]{kind} +13 {weapon.Name}" + (ack[12] == 0 ? "成功" : "失败");
            int announcements = 0;
            peer.Barrier(packet =>
            {
                if (packet.Opcode == 502)
                {
                    Bytes(packet.Body, TextBody(text), "普通逐级尝试在本人回执后公告真实成功或失败");
                    announcements++;
                }
                else
                    That(packet.Opcode == 14 && packet.Body.Length >= 3 && packet.Body[0] == 0 &&
                        packet.Body.Length == 3 + U16(packet.Body, 1) * 123,
                        "普通费用和失败补偿继续沿完整背包差量发布，不吞掉其他通知");
            });
            That(announcements == 1, "仅普通逐级概率路径产生一次公告");
            ExpectText(observer, text); observer.Barrier();
            That(ack[12] == 3 ? Core(target) is null : Core(target)?.Upgrade == ack[13],
                "公告不改变本次真实升档、降级、保级或销毁结果");
        }
        ItemCore? Core(short slot)
        {
            var rows = fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
                "WHERE s.list_type=0 AND s.slot_index=$p0", slot);
            return rows.Length == 0 ? null : System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)rows.Single()[0]!);
        }
        void Refine(TcpPeer peer, short target, short material) => peer.Send(438, Build(w =>
        {
            w.Write((byte)0); w.Write(target); w.Write(weapon.Id); w.Write(material);
            w.Write(3); w.Write("NPC"u8); w.Write((byte)0);
        }));
    }

    internal static void ExpectText(TcpPeer peer, string text) => Bytes(peer.Expect(0, 502).Body, TextBody(text),
        "服务器组公告使用原生系统聊天，带完整CP936名字和文案，不依赖同屏缓存");

    static byte[] TextBody(string text) => Build(w =>
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(text);
        w.Write((byte)2); w.Write(bytes.Length); w.Write(bytes);
    });

    static InventorySlot Bag(short index) => new(InventoryListType.Main, index);
}
