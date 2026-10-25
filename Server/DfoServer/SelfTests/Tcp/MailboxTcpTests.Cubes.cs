using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class MailboxTcpTests
{
    static void VerifyWalletClaims(TcpTestFixture fixture, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var service = new MailboxService(new CharacterRepository(fixture.Database), items.Items, items.GoldLimits, items.EpicPieces);
        InventoryItem Cube(int id, int count) => new(0, new(InventoryListType.Main, (short)(id == 3037 ? 358 : 354)),
            InventoryRewardGrantService.Create(items.Items.Get(id)) with { Value = count });
        var seeded = service.Deliver(new("cube-source", service.FindRecipient("MailSender")!, "系统", "晶块寄送来源", 0,
            [Cube(3037, 100), Cube(3033, 50)], DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        That(seeded.Accepted, "系统邮件允许明确支持的小晶块托管");
        using var receiver = Open(fixture, "20001");
        receiver.Select(prepared: true);
        using (var sender = Open(fixture))
        {
            sender.Select(prepared: true);
            var source = ReadList(sender.Mailbox.Single()).Summaries.Where(row => row.MailId == seeded.MailId).ToArray();
            Extract(sender, [seeded.MailId]); ExpectClaim(sender, source);
            var delta = sender.Expect(0, 14).Body;
            CheckItem(delta, 358, 3037, 100); CheckItem(delta, 354, 3033, 50);
            sender.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId') IN(3033,3037)") is [[0L]],
                "系统晶块领取直接进入钱包并退出邮件托管，不创建背包实体");

            long gold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0", sender.Id)[0][0]);
            long fee = MailboxService.Fee(1000, 2, items.Items.Get(3037).ShopPrice.SellGold * 30L + items.Items.Get(3033).ShopPrice.SellGold * 5L);
            SendMail(sender, true, 1000, [(0, 358, 3037, 30), (0, 354, 3033, 5)], "晶块与金币");
            sender.Expect(1, 315, [1]);
            delta = sender.Expect(0, 14).Body;
            CheckItem(delta, 0, 0, checked((int)(gold - 1000 - fee)));
            CheckItem(delta, 358, 3037, 70); CheckItem(delta, 354, 3033, 45);
            sender.Barrier(); receiver.Expect(0, 99, [1, 0]);
            receiver.Send(96); receiver.Expect(1, 96, [1, 0, 0]);
            var page = ReadList(receiver.Expect(0, 97).Body);
            uint mailId = page.Details.Single(row => row.Text == "晶块与金币").Id;
            var rows = page.Summaries.Where(row => row.MailId == mailId).ToArray();
            That(rows.Length == 2 && rows[0].Gold == 1000 && U32(rows[0].Core, 6) == 30 && U32(rows[1].Core, 6) == 5 &&
                fixture.Query("SELECT count(*) FROM mailbox_attachments a JOIN character_inventory_slots s ON s.item_uid=a.item_uid WHERE a.mail_id=$p0", mailId) is [[0L]],
                "两种晶块在邮件中保存完整数量和金币，托管记录没有真实背包槽");
            long recipientGold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0", receiver.Id)[0][0]);
            Extract(receiver, rows.Select(row => row.ClaimId).ToArray()); ExpectClaim(receiver, rows);
            delta = receiver.Expect(0, 14).Body;
            CheckItem(delta, 0, 0, checked((int)recipientGold + 1000));
            CheckItem(delta, 358, 3037, 30); CheckItem(delta, 354, 3033, 5);
            receiver.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId') IN(3033,3037)") is [[0L]],
                "玩家邮件领取后晶块汇入接收账号钱包，托管UID全部释放");
            Extract(receiver, [mailId]); receiver.Expect(1, 95, [0, 21, 0]); receiver.Barrier();

            string before = Snapshot();
            SendMail(sender, false, 0, [(0, 358, 3037, 71)], "数量不足");
            sender.Expect(1, 94, [0, 17, 0]); delta = sender.Expect(0, 14).Body;
            CheckItem(delta, 358, 3037, 70); sender.Barrier();
            SendMail(sender, false, 0, [(0, 354, 3037, 1)], "错误虚拟槽");
            sender.Expect(1, 94, [0, 17, 0]); delta = sender.Expect(0, 14).Body;
            CheckItem(delta, 354, 3033, 45); sender.Barrier();
            That(Snapshot() == before, "超量或模板槽位不匹配不扣钱包、金币及邮费，失败差量恢复真实虚拟槽");
            fixture.Sql("CREATE TRIGGER fail_cube_mail BEFORE INSERT ON mailbox_audit_log BEGIN SELECT RAISE(ABORT,'cube mail rollback'); END");
            SendMail(sender, false, 0, [(0, 358, 3037, 1)], "晶块回滚"); sender.Closed();
            receiver.Barrier();
            That(Snapshot() == before, "晚期邮件审计失败同时回滚钱包、金币、托管UID和新邮件，不给接收者提醒");
            fixture.Sql("DROP TRIGGER fail_cube_mail");
        }
        using (var sender = Open(fixture))
        {
            sender.Select(prepared: true); sender.Barrier();
            That(fixture.Query("SELECT currency_kind,balance FROM account_currency_balances WHERE account='10000' AND currency_kind IN(3,7) ORDER BY currency_kind")
                is [[3L, 45L], [7L, 70L]], "重连恢复寄送及失败后的精确钱包余额");
        }

        string Snapshot() => string.Join('\n', new[] { "characters", "account_wallets", "account_currency_balances", "character_inventory_items",
            "character_inventory_slots", "mailbox_messages", "mailbox_attachments", "mailbox_audit_log", "inventory_audit_log" }
            .Select(table => fixture.Snapshot("SELECT * FROM " + table + " ORDER BY 1")));
    }
}
