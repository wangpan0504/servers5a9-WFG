using static DfoServer.Testing.CashMileageTcpChecks;
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
        PromotionPageSessionTests.RunPurchase(resources.Towns, resources.ItemResources);
        Inventory.CashShopPromotionSessionTests.RunBoundaries(resources);
        await PromotionPurchaseAsync(source, root, resources, currencyMode: 0, units: 2);
        await PromotionPurchaseAsync(source, root, resources, currencyMode: 2, units: 4);
        await PromotionMatrixAsync(source, root, resources);
        await PromotionDisconnectAsync(source, root, resources);
        await PermanentContinuationAsync(source, root, resources);
        await PromotionPageAsync(source, root, resources);
    }

    static async Task PromotionPurchaseAsync(ServerOptions source, string root, TcpTestResources resources,
        byte currencyMode, int units)
    {
        var items = resources.ItemResources;
        var rules = Inventory.CashShopPromotionSessionTests.Rules with { Products = [new(100004, 1, 1)] };
        var catalog = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, true) }),
            items.CashShop, items.Items.Get, candidate => candidate.OverflowItemId == 2058 &&
                candidate.Tiers.All(row => row.ItemId == 2058) && items.Items.Get(2058).Kind == ItemKind.Consumable
                ? null : "测试只验证普通药品完整实例及邮件消费者");
        That(catalog.Product(100004)?.Availability == PromotionAvailability.Active, "TCP只启用已明确的合成普通药品促销夹具");
        var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2035-12-31T23:59:59Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cash-shop-promotions-" + currencyMode), resources: resources,
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
        byte[] request = new byte[4 + units * 15];
        request[0] = currencyMode;
        request[2] = checked((byte)units);
        for (int row = 0; row < units; row++)
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(7 + row * 15), 100004);
        await fixture.StartAsync();
        uint[] mailIds;
        using (var peer = Open(fixture))
        {
            peer.Select();
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer, fatigue: false);
            peer.Expect(0, 36, [1, 0, 156, 0, 0, 0, 0, 0, 0, 0]);
            var mileage = MileageBefore(fixture, units * ExpectedCashMileage(items.CashShop.Product(100004)!));
            peer.Send(64, request);
            for (int row = 0; row < units; row++)
            {
                var response = peer.Expect(1, 64).Body;
                That(response.Length >= 10 && response[0] == 1 && U32(response, 6) == 100004,
                    "直接购买及原生购物车逐商品成功，不以促销来源拒绝整车");
            }
            peer.Expect(0, 14);
            That(U32(peer.Expect(0, 53).Body, 1) == 2000 - units * 100, "促销沿原库存和钱包投影顺序精确扣除本车点券");
            MileageAfter(peer, fixture, mileage);
            peer.Expect(0, 99, [1, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[long count]] && count == units &&
                fixture.Query("SELECT count(*) FROM mailbox_messages") is [[long messages]] && messages == units - 1,
                "图内SQL同步提交累计及每档独立赠礼邮件，整车只提醒一次且不发布邮件列表");
            That(fixture.Query("SELECT global_purchase FROM cash_shop_promotion_awards ORDER BY global_purchase")
                    .Select(row => (long)row[0]!).SequenceEqual(Enumerable.Range(2, units - 1).Select(number => (long)number)) &&
                fixture.Query("SELECT count(*) FROM mailbox_attachments") is [[long attachments]] && attachments == units - 1,
                "直接购买及购物车完整保留跨档全部资格和附件，不只投递末档");
            That(fixture.Query("SELECT created_at FROM mailbox_messages ORDER BY mail_id")
                    .Select(row => (long)row[0]!).SequenceEqual(Enumerable.Repeat(clock.GetUtcNow().ToUnixTimeSeconds(), units - 1)),
                "每封真实TCP逐档促销邮件均使用Runtime注入的未来时钟");
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
            That(page.Summaries.Length == units - 1 && page.Summaries.All(gift => gift.ItemId == 2058) &&
                page.Summaries.Select(gift => gift.ClaimId).Distinct().Count() == units - 1 &&
                page.Summaries.Select(gift => gift.MailId).Distinct().Count() == units - 1 && page.Details.Length == units - 1,
                "回城逐封投影各累计奖档邮件，无合并附件、重复邮件或裸报警");
            mailIds = page.Summaries.Select(gift => gift.MailId).Distinct().ToArray();
            peer.Barrier();
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(1);
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries.Length == 0, "换角不把原角色赠礼投影给另一角色");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries is var restored &&
                restored.Length == units - 1 && restored.All(gift => mailIds.Contains(gift.MailId) && gift.ItemId == 2058) &&
                restored.Select(gift => gift.MailId).Distinct().Count() == units - 1,
                "断线重连恢复全部逐档MailId，不重发或合并奖励");
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
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries is var restored &&
                restored.Length == units - 1 && restored.All(gift => mailIds.Contains(gift.MailId) && gift.ItemId == 2058) &&
                restored.Select(gift => gift.MailId).Distinct().Count() == units - 1 &&
                fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[long restoredCount]] && restoredCount == units, "服务重建不丢原赠礼或清零累计");
            var mileage = MileageBefore(fixture, units * ExpectedCashMileage(items.CashShop.Product(100004)!));
            peer.Send(64, request);
            for (int row = 0; row < units; row++) peer.Expect(1, 64);
            peer.Expect(0, 14); peer.Expect(0, 53);
            MileageAfter(peer, fixture, mileage);
            peer.Expect(0, 99, [1, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[long totalCount]] && totalCount == 2 * units &&
                fixture.Query("SELECT count(*) FROM mailbox_messages") is [[long totalMessages]] && totalMessages == 2 * units - 1,
                "相同购买包复购后逐档邮件各有独立记录且整车只提醒一次");
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
        string PromotionSnapshot() => PurchaseSnapshot(fixture) + string.Concat(new[] { "rules", "progress", "orders", "awards" }
            .Select(table => JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_" + table + " ORDER BY rowid"))));
    }
}
