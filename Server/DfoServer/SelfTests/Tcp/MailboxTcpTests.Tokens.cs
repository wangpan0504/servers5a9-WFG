using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class MailboxTcpTests
{
    public static async Task RunTokenCeraAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "token-cera"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var inventory = Enumerable.Range(121, 56).Select(slot => new InventoryItem(0,
                new(InventoryListType.Main, (short)slot), InventoryRewardGrantService.Create(r.Items.Get(3030))))
                .Where(item => item.Position.Index != 176).Append(Token(2681762, 7) with { Position = new(InventoryListType.Main, 176) })
                .Concat(new[] { (Slot: (short)3, Id: 2683799), (Slot: (short)4, Id: 490705482), (Slot: (short)5, Id: 10007481) }.Select(row =>
                    new InventoryItem(0, new(InventoryListType.Main, row.Slot),
                        InventoryRewardGrantService.Create(r.Items.Get(row.Id), row.Id == 10007481 ? 1 : 2))))
                .ToImmutableArray();
            repository.Create(owner, new("TokenMail", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, CompletedQuests = [2670], Skills = resources.Characters.Get(0).Skills,
                Inventory = new(inventory, Gold: 123)
            });
            repository.Create(owner, new("TokenTwin", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, CompletedQuests = [2670], Skills = resources.Characters.Get(0).Skills });
        }
        var service = new MailboxService(repository, r.Items, r.GoldLimits, r.EpicPieces);
        var recipient = service.FindRecipient("TokenMail")!;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var delivery = new MailboxDelivery("token-mixed", recipient, "系统", "代币券测试", 17,
            [new(0, new(InventoryListType.Main, 0), InventoryRewardGrantService.Create(r.Items.Get(3030))),
                Token(2681762, 200), Token(2681917, 300)], now);
        string state = State();
        fixture.Sql("CREATE TRIGGER token_delivery_abort AFTER INSERT ON character_inventory_items WHEN json_extract(NEW.item_core,'$.ItemId')=2681917 BEGIN SELECT RAISE(ABORT,'token delivery rollback'); END");
        Reject<SqliteException>(() => service.Deliver(delivery), "拆封投递末项失败不留下普通邮件、首封代币或托管资产");
        That(State() == state, "同一投递key的全部拆封同事务回滚");
        fixture.Sql("DROP TRIGGER token_delivery_abort");
        var delivered = service.Deliver(delivery);
        state = State();
        That(delivered.Accepted && service.Deliver(delivery).MailId == delivered.MailId && State() == state &&
            fixture.Query("SELECT count(*) FROM mailbox_messages WHERE system_mail=1") is [[3L]],
            "混合投递拆为三封原生系统邮件，整批key重试不重复发放");
        Reject<InvalidOperationException>(() => service.Deliver(delivery with { Items = [Token(2681762, 201)] }), "同key不同面额不能替换原投递");
        await fixture.StartAsync();
        uint retryClaim = 0;
        int happyBalance = 300;
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            var rows = ReadList(peer.Mailbox.Single()).Summaries.Where(row => row.ItemId is 2681762 or 2681917).ToArray();
            That(rows.Length == 2 && rows.Select(row => row.MailId).Distinct().Count() == 2 &&
                rows.All(row => row.Gold == (row.ItemId == 2681762 ? 200 : 300) && U32(row.Core, 6) == row.Gold),
                "97以两种真实模板及原生金额显示，不将代币面额冒充角色金币");
            Extract(peer, rows.Select(row => row.ClaimId).ToArray()); ExpectClaim(peer, rows);
            Cera(peer, 200, 300); peer.Barrier(NoPhysicalGrant);
            That(fixture.Query("SELECT gold FROM characters WHERE slot=0") is [[123L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId') IN(2681762,2681917)") is [[1L]],
                "满材料包仍能领取：新托管退出，余额入账，只有旧背包代币实物保持原样");
            state = State(); Extract(peer, rows.Select(row => row.ClaimId).ToArray()); peer.Expect(1, 95, [0, 21, 0]);
            peer.Barrier(NoPhysicalGrant); That(State() == state, "已领摘要重放不再增加余额或生成库存");
            int tokenBalance = 200;
            foreach (var box in new[] { (Slot: (short)3, Id: 2683799, Token: 2681762, Amount: 10000),
                (Slot: (short)4, Id: 490705482, Token: 2681917, Amount: 200), (Slot: (short)5, Id: 10007481, Token: 2681917, Amount: 0) })
            {
                var definition = r.Boosters.Get(box.Id);
                That(definition.Available && definition.Pools is [{ Draws: 1 }] &&
                    definition.Pools[0].Rewards.All(row => row.ItemId == box.Token && row.Weight > 0 &&
                        (box.Amount > 0 ? row.Count == box.Amount : row.Count is 3000 or 3100 or 3200 or 3300 or 3400)),
                    "当前PVF的etc及cera奖池保留真实面额和随机候选");
                peer.Send(160, Build(w => { w.Write(box.Slot); w.Write(1); }));
                var ack = peer.Expect(1, 160).Body;
                int gained = checked((int)U32(ack, 22));
                That(box.Amount > 0 ? gained == box.Amount : gained is 3000 or 3100 or 3200 or 3300 or 3400,
                    "随机金额必须来自实际PVF，不能把所有cera池统一按1发放");
                Bytes(ack, Build(w =>
                {
                    w.Write((byte)1); w.Write((byte)0); w.Write(box.Id); w.Write(box.Slot); w.Write(0); w.Write(0);
                    w.Write((ushort)1); w.Write(box.Token); w.Write(gained);
                }), "两类代币券沿原生开箱获得清单，不额外插入背包实例");
                var delta = peer.Expect(0, 14).Body;
                bool depleted = box.Id == 10007481;
                That(delta[0] == 0 && U16(delta, 1) == 1 && U16(delta, 3) == box.Slot &&
                    U32(delta, 5) == (depleted ? uint.MaxValue : (uint)box.Id) && U32(delta, 9) == (depleted ? 0 : 1),
                    "开箱只同步真实源的一件扣除，不创建奖励STK槽位");
                if (box.Token == 2681762) tokenBalance += gained; else happyBalance += gained;
                Cera(peer, tokenBalance, happyBalance); peer.Barrier(NoPhysicalGrant);
            }
            var retry = service.Deliver(new("token-retry", recipient, "系统", "回滚", 0, [Token(2681762, 5)], now));
            That(retry.Accepted, "准备真实托管领取回滚样品");
            peer.Send(96); peer.Expect(1, 96);
            retryClaim = ReadList(peer.Expect(0, 97).Body).Summaries.Single(row => row.MailId == retry.MailId).ClaimId;
            fixture.Sql("CREATE TRIGGER token_claim_abort BEFORE INSERT ON account_currency_balances WHEN NEW.currency_kind=16 BEGIN SELECT RAISE(ABORT,'token claim rollback'); END");
            state = State(); Extract(peer, [retryClaim]); peer.Closed();
            That(State() == state, "钱包后段SQL失败回滚余额、已领资格、托管核心和审计，不发成功");
        }
        fixture.Sql("DROP TRIGGER token_claim_abort");
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            var row = ReadList(peer.Mailbox.Single()).Summaries.Single(row => row.ClaimId == retryClaim);
            Extract(peer, [row.ClaimId]); ExpectClaim(peer, [row]); Cera(peer, 10205, happyBalance); peer.Barrier(NoPhysicalGrant);
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(1, prepared: true);
            Bytes(entry.Single(packet => packet.Kind == 0 && packet.Opcode == 53).Body, Balance(10205, happyBalance),
                "同账号换角恢复已提交的两种代币余额"); peer.Barrier();
        }
        using (var peer = Open(fixture, "token-other"))
        {
            peer.Create("TokenOther"); var entry = peer.Select();
            Bytes(entry.Single(packet => packet.Kind == 0 && packet.Opcode == 53).Body, Balance(0, 0),
                "其他账号不继承代币领取和兑换结果"); peer.Barrier();
        }
        await fixture.StopAsync();
        fixture.Sql("UPDATE account_currency_balances SET balance=2147483645 WHERE account=$p0 AND currency_kind=16", fixture.Options.DevelopmentAccount);
        That(service.Deliver(new("token-cap", recipient, "系统", "上限", 0, [Token(2681762, 2), Token(2681762, 1)], now)).Accepted,
            "代币券多份面额在邮件中保持独立领取资格");
        await fixture.StartAsync("token-reopen");
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            var rows = ReadList(peer.Mailbox.Single()).Summaries.Where(row => row.ItemId == 2681762).OrderBy(row => row.MailId).ToArray();
            Extract(peer, rows.Select(row => row.ClaimId).ToArray());
            peer.Expect(1, 95, Build(w =>
            {
                w.Write((byte)1); w.Write(2);
                for (int i = 0; i < 2; i++) { w.Write(rows[i].MailId); w.Write(rows[i].ClaimId); w.Write(i == 0 ? 0 : 4); }
            }));
            Cera(peer, int.MaxValue, happyBalance); peer.Barrier(NoPhysicalGrant);
            That(fixture.Query("SELECT count(*) FROM mailbox_attachments WHERE claimed=0 AND mail_id=$p0", rows[1].MailId) is [[1L]],
                "余额恰满只领取足额前缀，超额一券保留，不截断后吞掉邮件");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "代币转换和服务重建后存档完整");

        InventoryItem Token(int id, int amount) => new(0, new(InventoryListType.Main, 0),
            InventoryRewardGrantService.Create(r.Items.Get(id), amount));
        string State() => fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM mailbox_messages ORDER BY mail_id") + fixture.Snapshot("SELECT * FROM mailbox_attachments ORDER BY attachment_id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") + fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id");
        static byte[] Balance(int token, int happy) => Build(w => { w.Write((byte)1); w.Write(0); w.Write(token); w.Write(happy); });
        static void Cera(TcpPeer peer, int token, int happy) => peer.Expect(0, 53, Balance(token, happy));
        static void NoPhysicalGrant(WirePacket packet) => That(packet.Opcode is not (14 or 97 or 537),
            "代币直接归钱包，不发伪造入包/消费、清空邮箱或积分获得通知");
    }
}
