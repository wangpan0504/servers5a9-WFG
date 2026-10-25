using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class MagicBoxTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
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
            ReadEffects(peer);
        }
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select(expertContract: restoreExpertContract)[4].Body;
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
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "随机盒真实TCP后的存档完整性");

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
        new(2682272, ItemKind.Consumable, 999))])
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
            That(packet.Kind == 0 && packet.Opcode is 53 or 412 or 1760 or 66 or 901 or 2 or 950 or 99,
                "十连仅发布已提交钱包、幸运值、契约及邮件的真实通知");
            if (packet.Opcode == 412) { Bytes(packet.Body, [0, 5], "十连0→1幸运值投影"); gauge = true; }
        }
        That(gauge, "十连提交后发布幸运值");
    }

}
