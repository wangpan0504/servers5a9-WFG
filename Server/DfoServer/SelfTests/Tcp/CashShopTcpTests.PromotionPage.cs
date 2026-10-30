using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Chat;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    const string PromotionPageNonceA = "0123456789abcdef0123456789abcdef";
    const string PromotionPageNonceB = "abcdef0123456789abcdef0123456789";

    // 共用本轮只读资源；只测跨层query/提交/发布次序，不重跑下层身份和损坏矩阵。
    static async Task PromotionPageAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var rules = Inventory.CashShopPromotionTests.PilotRules();
        CashShopPromotionCatalog MakeCatalog(bool enabled) => CashShopPromotionCatalog.Parse(
            JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, enabled) }), items.CashShop, items.Items.Get, _ => null);
        var catalog = MakeCatalog(false);
        // 原主货有明确历史绝对期限；本夹具在期限前购买，不绕其过期拒绝语义。
        var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2020-03-01T00:00:00Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cash-shop-promotion-page"), resources: resources,
            promotions: () => catalog, clock: clock, enablePromotionPage: true);
        SeedPageBuyer(fixture);
        uint commodity = 105681;
        int price = items.CashShop.Product((int)commodity)!.CeraPrice;
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.ValidateNativePromotionEvents = body => AssertPromotionNativeEvents(body, 0, true, peer.ExpectedOpenEvents);
            peer.Select();
            catalog = MakeCatalog(true);
            // 从未合法查询的连接，成功购买也没有changed；严格屏障不会跳过意外机器通知。
            peer.Send(64, PromotionPageBuyBody(commodity));
            That(U32(peer.Expect(1, 64).Body, 6) == commodity, "未查询页面的真实购买保原商品成功ACK");
            peer.Expect(0, 14);
            That(U32(peer.Expect(0, 53).Body, 1) == 100000 - price, "未查询页面的真实购买只扣本车价格");
            AssertPromotionNativeRefresh(peer.Expect(0, 108), 1, false, peer.ExpectedOpenEvents);
            peer.Barrier();

            peer.Send(17, PromotionPageChatBody(PromotionPageNonceA));
            var first = PromotionPageFields(peer.Expect(0, 1709).Body);
            That(first.Length == 19 && first[1] == "snapshot" && first[2] == PromotionPageNonceA &&
                first[3] == peer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) && first[11] == "ready" &&
                first[12] == "1" && first[13] == "5000" && first[14] == "0" && first[15] == "0" && first[5] == "1",
                "真实Runtime页面能力显式开启后query读取唯一原进度count1/rev1");
            AssertPromotionPageNames(first, items.Items);
            peer.Barrier();

            clock.Advance(TimeSpan.FromSeconds(12));
            peer.Send(64, PromotionPageBuyBody(commodity));
            That(U32(peer.Expect(1, 64).Body, 6) == commodity, "页面view超过10秒后真实购买仍先原成功ACK");
            peer.Expect(0, 14);
            That(U32(peer.Expect(0, 53).Body, 1) == 100000 - 2 * price, "第二真实购买按原价格扣款");
            peer.Expect(0, 99, [1, 0]);
            var changed = PromotionPageFields(peer.Expect(0, 1709).Body);
            That(changed.SequenceEqual(new[] { "1", "changed", PromotionPageNonceA, first[3], first[4], "2" }),
                "ACK/库存/邮箱发布末端才发changed，长期view保原nonce/catalog并只给新revision");
            AssertPromotionNativeRefresh(peer.Expect(0, 108), 2, false, peer.ExpectedOpenEvents);
            peer.Barrier();
            That(fixture.Query("SELECT count,revision FROM cash_shop_promotion_progress") is [[2L, 2L]] &&
                fixture.Query("SELECT count(*) FROM cash_shop_promotion_orders") is [[2L]] &&
                fixture.Query("SELECT count(*) FROM cash_shop_promotion_awards") is [[1L]] &&
                fixture.Query("SELECT count(*) FROM mailbox_messages") is [[1L]],
                "页面query/changed零奖励副作用，两次真实购买只产生原订单和一次资格邮件");

            catalog = MakeCatalog(false);
            clock.Advance(TimeSpan.FromSeconds(5));
            peer.Send(17, PromotionPageChatBody(PromotionPageNonceB));
            var refreshed = PromotionPageFields(peer.Expect(0, 1709).Body);
            That(refreshed.Length == 19 && refreshed[1] == "snapshot" && refreshed[2] == PromotionPageNonceB &&
                refreshed[3] == first[3] && refreshed[4] != first[4] && refreshed[5] == "2" && refreshed[11] == "disabled" &&
                refreshed[12] == "2" && refreshed[14] == "1" && refreshed[15] == "0",
                "关闭活动后新nonce仍读真实累计/账本，旧目录view不复用或清零");
            AssertPromotionPageNames(refreshed, items.Items);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.ValidateNativePromotionEvents = body => AssertPromotionNativeEvents(body, 2, true, peer.ExpectedOpenEvents);
            peer.Select();
            peer.Barrier();
        }
        await fixture.StopAsync();

        // 独立默认关闭样本不传enablePromotionPage，保留机器请求也不得变成聊天广播。
        await using var disabled = new TcpTestFixture(source, Path.Combine(root, "cash-shop-promotion-page-disabled"), resources: resources,
            promotions: () => catalog, clock: clock);
        SeedPageBuyer(disabled);
        await disabled.StartAsync();
        using (var peer = Open(disabled))
        {
            peer.Select();
            peer.Send(17, PromotionPageChatBody(PromotionPageNonceA));
            peer.Barrier();
        }
        await disabled.StopAsync();

        void SeedPageBuyer(TcpTestFixture target)
        {
            var repository = new CharacterRepository(target.Database, resources.Quests, promotions: () => catalog);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("10000", () => { });
            var character = repository.Create(owner, CharacterCreation.Prepare("PageTcp", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            repository.CommitInventory(owner, character, new(character.Inventory,
                CurrencyWallet.Set(character.Inventory, CurrencyKind.Cera, 100000), "fund-page-tcp"));
        }
    }

    static void AssertPromotionNativeRefresh(WirePacket packet, long count, bool disabled, IReadOnlyList<ushort> extraEvents)
    {
        That(packet is { Kind: 0, Opcode: 108, Protected: true }, "购买刷新沿原保护压缩108发布，不重复ACK");
        using var compressed = new MemoryStream(packet.Body);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        byte[] bounded = new byte[32768];
        int length = 0;
        while (length < bounded.Length)
        {
            int read = zlib.Read(bounded, length, bounded.Length - length);
            if (read == 0) break;
            length += read;
        }
        That(zlib.ReadByte() == -1, "购买刷新108解压有界且消费完整压缩正文");
        AssertPromotionNativeEvents(bounded[..length], count, disabled, extraEvents);
    }

    // 独立按已保存wire逐字段消费；不调用生产Builder构造期望或仅搜10999字符串。
    static void AssertPromotionNativeEvents(byte[] payload, long count, bool disabled, IReadOnlyList<ushort> extraEvents)
    {
        using var input = new MemoryStream(payload);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        string Text()
        {
            int length = checked((int)reader.ReadUInt32());
            That(length is >= 0 and <= 16384 && length <= input.Length - input.Position, "108字符串长度完整且有界");
            byte[] bytes = reader.ReadBytes(length);
            return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
        }
        That(reader.ReadUInt16() == 5 + extraEvents.Count, "页面只追加10999，原完整活动目录项数保留");
        foreach (ushort id in (ushort[])[2037, 2378, 2395, 333, .. extraEvents])
            That(reader.ReadUInt16() == id && reader.ReadUInt32() == 1 && Text() == "" && Text() == "" && reader.ReadByte() == 0,
                "原赛丽亚/红包/乔纳森/魔兽/开放活动每字段保留");
        That(reader.ReadUInt16() == 10999 && reader.ReadUInt32() == 1 && Text() == "" && Text() == "" && reader.ReadByte() == 1,
            "原生10999唯一完整元数据及扩展标志");
        That(reader.ReadByte() == 2 && reader.ReadByte() == 4 && Text() == "众神降世礼包 多重购买有惊喜" &&
            Text() == "众神降世礼包" && Text() == "" && reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0 &&
            Text() == "design popup window/2/0", "正常活动列表记录保持原生444设计页的已证类型与完整线序");
        string content = Text();
        string[] tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int cursor = 0;
        string Take()
        {
            That(cursor < tokens.Length, "原生正文token未截断");
            return tokens[cursor++];
        }
        int Number() => int.Parse(Take(), System.Globalization.CultureInfo.InvariantCulture);
        That(Take() == "[image_with_itemcontrol]" && Take() == "`Interface2/cs_shop/cs_calendar.img`" &&
            Number() == 12 && Number() == 9, "九奖使用原日历图与真实itemcontrol数");
        int[] x = [54, 125, 196], y = [135, 203, 279];
        for (int index = 0; index < 9; index++)
            That(Number() == 490705441 + index && Number() == 1 && Number() == x[index % 3] && Number() == y[index / 3],
                "九个真实礼盒itemcontrol逐档/数量/坐标完整，无占位奖励");
        That(Take() == "[text_control]" && Number() == 2, "累计和状态两文本控件完整");
        string[] texts = new string[2];
        for (int index = 0; index < 2; index++)
        {
            That(Take() == "```", "原生文本开始使用独立反引号token");
            var words = new List<string>();
            string token;
            while ((token = Take()) != "```") words.Add(token);
            texts[index] = string.Join(' ', words);
            That(Number() == (index == 0 ? 196 : 8) && Number() == (index == 0 ? 344 : 445) && Take() == "`COLOR_WHITE`",
                "两文本控件的坐标/颜色完整");
        }
        string digits = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        That(texts[0] == digits && texts[1] == (disabled ? "活动未开启 " : "") + "累计：" + digits +
            " 距下档" + (count == 0 ? "2" : "1") + "套" && cursor == tokens.Length,
            "原生显示取真实累计0/1/2与Disabled状态，正文全部消费不猜发奖位图");
        That(reader.ReadByte() == 1 && reader.ReadByte() == 2, "原生活动可见位与原状态尾段项数完整");
        foreach (ushort id in new ushort[] { 2037, 2395 })
        {
            That(reader.ReadUInt16() == id, "原active tail活动身份未丢失");
            for (int index = 0; index < 12; index++)
                That(reader.ReadUInt32() == (id == 2037 && index == 0 ? 8u : 0u), "原active tail完整12个数值逐字段保留");
        }
        That(input.Position == input.Length, "原生108逐字段消费至EOF，保持原其他事件和状态块");
    }

    static byte[] PromotionPageBuyBody(uint commodity)
    {
        byte[] body = new byte[19];
        body[2] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), commodity);
        return body;
    }

    static byte[] PromotionPageChatBody(string nonce)
    {
        byte[] text = ChatText.Encoding.GetBytes("#cp-promo/1 " + nonce);
        byte[] body = new byte[11 + text.Length];
        body[0] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), (uint)text.Length);
        text.CopyTo(body, 11);
        return body;
    }

    static string[] PromotionPageFields(byte[] body)
    {
        That(body.Length >= 19 && BinaryPrimitives.ReadUInt32LittleEndian(body) == 10 &&
            Encoding.ASCII.GetString(body, 4, 10) == "CP_PROMO/1" && body[^1] == 0,
            "真实1709只读页面专用完整标题/type");
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(14)));
        That(length is > 0 and <= 4000 && body.Length == length + 19, "真实机器页面正文未按普通公告600B截断");
        return Encoding.ASCII.GetString(body, 18, length).Split('|');
    }

    static void AssertPromotionPageNames(string[] fields, ItemCatalog items)
    {
        string[] records = fields[17].Split(';');
        That(records.Length == 9, "九档全部保留");
        for (int index = 0; index < 10; index++)
        {
            string[] reward = (index < 9 ? records[index] : fields[18]).Split(',');
            That(reward.Length == 3, "真实名称奖励记录完整三段");
            int expectedId = index < 9 ? 490705441 + index : 490705415;
            string encoded = reward[2].Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            string name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
            That(reward[0] == expectedId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                reward[1] == "1" && name == items.Get(expectedId).Name,
                "真实ItemTemplate.Name完整传输，未用客户端猜名或占位名称");
        }
    }
}
