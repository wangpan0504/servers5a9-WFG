using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    public static async Task PromotionsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        Inventory.CashShopPromotionSessionTests.Run(resources.Towns, resources.ItemResources);
        Inventory.CashShopPromotionSessionTests.RunBoundaries(resources);
        var items = resources.ItemResources;
        var rules = Inventory.CashShopPromotionSessionTests.Rules with { Products = [new(100004, 1, 1)] };
        var catalog = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, true) }),
            items.CashShop, items.Items.Get, candidate => candidate.OverflowItemId == 2058 &&
                candidate.Tiers.All(row => row.ItemId == 2058) && items.Items.Get(2058).Kind == ItemKind.Consumable
                ? null : "测试只验证普通药品完整实例及邮件消费者");
        That(catalog.Product(100004)?.Availability == PromotionAvailability.Active, "TCP只启用已明确的合成普通药品促销夹具");
        var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2035-12-31T23:59:59Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cash-shop-promotions"), resources: resources,
            promotions: () => catalog, clock: clock);
        var repository = new CharacterRepository(fixture.Database, resources.Quests, promotions: () => catalog);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire("10000", () => { }))
        {
            var created = repository.Create(owner, CharacterCreation.Prepare("PromoTcp", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            repository.CommitInventory(owner, created, new(created.Inventory,
                CurrencyWallet.Set(created.Inventory, CurrencyKind.Cera, 2000), "seed"));
            repository.Create(owner, CharacterCreation.Prepare("PromoOther", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
        }
        byte[] request = new byte[34];
        request[2] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(7), 100004);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(22), 100004);
        await fixture.StartAsync();
        uint mailId;
        using (var peer = Open(fixture))
        {
            peer.Select();
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);
            peer.Send(64, request);
            for (int row = 0; row < 2; row++)
                That(U32(peer.Expect(1, 64).Body, 6) == 100004, "重复商品行分别保持原成功ACK");
            peer.Expect(0, 14);
            That(U32(peer.Expect(0, 53).Body, 1) == 1800, "促销沿原库存和钱包投影顺序扣两行点券");
            peer.Barrier();
            That(fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[long count]] && count == 2 &&
                fixture.Query("SELECT count(*) FROM mailbox_messages") is [[1L]], "图内SQL同步提交累计及一封赠礼，屏障前无邮件包");
            That(fixture.Query("SELECT created_at FROM mailbox_messages") is [[long created]] &&
                created == clock.GetUtcNow().ToUnixTimeSeconds(), "真实TCP促销规划及邮件使用Runtime注入的未来时钟");
            peer.Send(42);
            peer.Expect(1, 42, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            peer.Expect(0, 24);
            FormalDungeonTcpTests.RefreshedQuests(peer);
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 4 })
            {
                That(packet.Body.Length == 6 && U16(packet.Body, 0) == peer.Id, "回城在邮箱前恢复本人虚弱状态");
                packet = peer.Read();
            }
            That(packet is { Kind: 0, Opcode: 97 }, "回城从持久邮箱恢复一次列表");
            var page = MailboxTcpTests.ReadList(packet.Body);
            That(page.Summaries is [var gift] && gift.ItemId == 2058 && page.Details.Length == 1,
                "回城只投影一封完整赠礼，无重复邮件或裸报警");
            mailId = page.Summaries.Single().MailId;
            peer.Barrier();
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(1);
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries.Length == 0, "换角不把原角色赠礼投影给另一角色");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries is [var gift] && gift.MailId == mailId,
                "断线重连由真实邮箱恢复同一赠礼ID，不重发奖励");
            string before = PromotionSnapshot();
            fixture.Sql("CREATE TRIGGER fail_promotion_mail BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT,'promotion mail failure'); END");
            peer.Send(64, request);
            peer.Closed();
            That(PromotionSnapshot() == before, "原协议整车邮件晚SQL失败回滚主货、扣费、累计、资格和所有邮件");
            fixture.Sql("DROP TRIGGER fail_promotion_mail");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries is [var gift] && gift.MailId == mailId &&
                fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[2L]], "服务重建不丢原赠礼或清零累计");
            peer.Send(64, request);
            peer.Expect(1, 64); peer.Expect(1, 64); peer.Expect(0, 14); peer.Expect(0, 53);
            peer.Expect(0, 99, [1, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[4L]] &&
                fixture.Query("SELECT count(*) FROM mailbox_messages") is [[2L]], "相同购买包正常复购且整车只提醒一次");
        }
        await fixture.StopAsync();
        var diagnostics = Directory.EnumerateFiles(fixture.Root, "server.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(entry => entry.GetProperty("Kind").GetString() == "cash_shop_buy")
            .Select(entry => entry.GetProperty("Detail")).Where(detail => detail.GetProperty("Accepted").GetBoolean()).ToArray();
        That(diagnostics.Length == 2 && diagnostics.All(detail => detail.TryGetProperty("Order", out var order) &&
            order.GetString() is { Length: 12 } && detail.TryGetProperty("Campaign", out _) &&
            detail.GetProperty("Cost").GetProperty("PlanMilliseconds").GetDouble() >= 0 &&
            detail.GetProperty("Cost").GetProperty("CommitMilliseconds").GetDouble() >= 0 &&
            detail.GetProperty("Cost").GetProperty("AllocatedBytes").GetInt64() > 0 &&
            !detail.TryGetProperty("Commodities", out _) && !detail.TryGetProperty("PaymentModes", out _)),
            "促销每车只记脱敏订单、活动、数量、结果与有界整车阶段成本，不逐叶记录资产");
        await PromotionMatrixAsync(source, root, resources);
        await PromotionDisconnectAsync(source, root, resources);
        await PermanentContinuationAsync(source, root, resources);
        string PromotionSnapshot() => PurchaseSnapshot(fixture) + string.Concat(new[] { "rules", "progress", "orders", "awards" }
            .Select(table => JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_" + table + " ORDER BY rowid"))));
    }
}
