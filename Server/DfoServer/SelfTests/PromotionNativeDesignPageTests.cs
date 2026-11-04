using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PromotionNativeDesignPageTests
{
    internal static void RunCore()
    {
        int before = Count;
        var first = PromotionNativeDesignPage.Create(Available(0));
        That(first is not null, "真实零累计必须产生原生活动10999，不能只有机器快照");
        foreach (var (count, next) in new (long, string)[]
        {
            (0, "距下档2套"), (1, "距下档1套"), (9, "距下档1套"), (10, "距下档2套"),
            (20, "距下档2套"), (30, "距下档1套"), (31, "距下档1套"),
            (5000, "已达累计上限"), (5001, "已达累计上限"), (long.MaxValue, "已达累计上限")
        })
        {
            var page = PromotionNativeDesignPage.Create(Available(count));
            That(page is { EventId: 10999, Record04: 2, Record08: 4, Visible: true,
                Route: "design popup window/2/0", Title: "众神降世礼包 多重购买有惊喜" }, "原生目录记录沿已证2/4与Design路由");
            var texts = ReadContent(page!, true);
            string digits = count.ToString(CultureInfo.InvariantCulture);
            That(texts[0] == (digits.Length <= 5 ? digits : "见下"), "短计数区不截断或钳制历史事实");
            That(texts[1] == "累计：" + digits + " " + next, "完整真实累计及下一档进入原生正文");
        }
        var disabled = PromotionNativeDesignPage.Create(Available(32) with { Status = "disabled" });
        That(disabled is not null, "disabled仍保留已知累计与真实奖项只读页");
        var disabledText = ReadContent(disabled!, true);
        That(disabledText[0] == "32" && disabledText[1] == "活动未开启 累计：32 距下档1套", "关闭购买不抹去历史累计或伪称活动开启");
        foreach (long? count in new long?[] { null, 32, long.MaxValue })
        {
            var unknown = Available(32) with { Status = "unavailable", Count = count,
                ProgressRevision = count.HasValue ? 11 : null, AwardMask27 = null,
                OverflowAwardCount = null, Rewards = [], OverflowReward = null };
            var page = PromotionNativeDesignPage.Create(unknown);
            That(page is not null, "不可核账本仍呈现如实只读状态");
            var texts = ReadContent(page!, false);
            That(texts[0] == "累计：" + (count?.ToString(CultureInfo.InvariantCulture) ?? "未知") &&
                texts[1] == "奖励状态暂不可核，请稍后重开活动页", "不可核不造零累计或已发奖项");
        }
        InvalidSnapshots();
        FullEventInfo(first!);
        Console.WriteLine($"PromotionNativeDesignPageTests: {Count - before} passed");
    }

    static PromotionPageSnapshot Available(long count)
    {
        uint mask = 0;
        for (int round = 0; round < 3; round++)
            for (int tier = 2; tier <= 10; tier++)
                if (count >= tier + round * 10) mask |= 1u << (round * 9 + tier - 2);
        return new("spring-2020", "replica-1", "v1", "default", "package", 7, 11, "ready", count, 5000,
            mask, count > 30 ? count - 30 : 0, 10000,
            Enumerable.Range(490705441, 9).Select(id => new PromotionPageReward(id, 1, "合成奖励")).ToImmutableArray(),
            new(490705415, 1, "合成追加奖励"));
    }

    static string[] ReadContent(EventInfoDesignPage page, bool items)
    {
        string[] tokens = page.Content.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int cursor = 0;
        string Take() => tokens[cursor++];
        int Number() => int.Parse(Take(), CultureInfo.InvariantCulture);
        if (items)
        {
            That(Take() == "[image_with_itemcontrol]" && Take() == "`Interface2/cs_shop/cs_calendar.img`" &&
                Number() == 12 && Number() == 9, "原背景帧12与真实九ItemControl");
            int[] columns = [54, 125, 196], rows = [135, 203, 279];
            for (int i = 0; i < 9; i++)
                That(Number() == 490705441 + i && Number() == 1 && Number() == columns[i % 3] &&
                    Number() == rows[i / 3], "九奖物品号、数量和原格坐标完整");
        }
        That(Take() == "[text_control]" && Number() == 2, "正文只用已证两文本语法，不把机器协议交原生consumer");
        string[] texts = new string[2];
        for (int i = 0; i < 2; i++)
        {
            That(Take() == "```", "文字开delimiter单独占token");
            List<string> words = [];
            string token;
            while ((token = Take()) != "```") words.Add(token);
            texts[i] = string.Join(' ', words);
            That(Number() == (items ? i == 0 ? 196 : 8 : 8) &&
                Number() == (items ? i == 0 ? 344 : 445 : i == 0 ? 40 : 65) &&
                Take() == "`COLOR_WHITE`", "每段文本的坐标与颜色完整消费");
        }
        That(cursor == tokens.Length, "正文全部消费，无截断和多余token");
        return texts;
    }

    static void InvalidSnapshots()
    {
        var snapshot = Available(32);
        foreach (var bad in new[]
        {
            snapshot with { Campaign = "other" }, snapshot with { Period = "other" },
            snapshot with { RulesVersion = "other" }, snapshot with { Realm = "other" },
            snapshot with { Group = "other" }, snapshot with { CatalogRevision = -1 },
            snapshot with { ProgressRevision = -1 }, snapshot with { Status = "other" },
            snapshot with { Count = -1 }, snapshot with { Count = null },
            snapshot with { ProgressRevision = null }, snapshot with { MaximumPurchases = 4999 },
            snapshot with { Rewards = [] }, snapshot with { Rewards = snapshot.Rewards.RemoveAt(8) },
            snapshot with { Rewards = snapshot.Rewards.SetItem(0, new(490705449, 1, "合成奖励")) },
            snapshot with { Rewards = snapshot.Rewards.SetItem(0, new(490705441, 2, "合成奖励")) },
            snapshot with { AwardMask27 = 0 }, snapshot with { OverflowAwardCount = 0 },
            snapshot with { OverflowReward = null }, snapshot with { OverflowReward = new(490705441, 1, "合成奖励") }
        }) That(PromotionNativeDesignPage.Create(bad) is null, "错scope/无效快照/缺奖不能伪造成功原生页");
        That(PromotionNativeDesignPage.Create(snapshot with { Status = "unavailable", Rewards = [], OverflowReward = null }) is null,
            "不可核快照不得夹带假已发位图或追加计数");
    }

    static void FullEventInfo(EventInfoDesignPage page)
    {
        ushort[] extra = [2532, 333, 2395];
        ushort[] active = [2395];
        byte[] old = Inflate(SeriaLuckPackets.EventInfoCompressed(extra, active));
        byte[] noPage = Inflate(SeriaLuckPackets.EventInfoCompressed(extra, active, []));
        Bytes(old, noPage, "第三可选参数无页时保旧108全部字节");
        byte[] expected = LegacyBody(extra, active);
        Bytes(old, expected, "旧目录及全部12数值尾段由独立编码验证");

        byte[] plain = Inflate(SeriaLuckPackets.EventInfoCompressed(extra, active, [page]));
        using var input = new MemoryStream(plain);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        That(reader.ReadUInt16() == 5, "完整108保四个既有目录项并追加一页");
        foreach (ushort id in new ushort[] { 2037, 2532, 333, 2395 })
            That(reader.ReadUInt16() == id && reader.ReadUInt32() == 1 && ReadString(reader) == "" &&
                ReadString(reader) == "" && reader.ReadByte() == 0, "既有目录逐项未丢失");
        That(reader.ReadUInt16() == 10999 && reader.ReadUInt32() == 1 && ReadString(reader) == "" &&
            ReadString(reader) == "" && reader.ReadByte() == 1, "10999元数据与扩展标志完整");
        That(reader.ReadByte() == 2 && reader.ReadByte() == 4 && ReadString(reader) == page.Title &&
            ReadString(reader) == "众神降世礼包" && ReadString(reader) == "" && reader.ReadUInt32() == 0 &&
            reader.ReadUInt32() == 0 && ReadString(reader) == "design popup window/2/0" &&
            ReadString(reader) == page.Content && reader.ReadByte() == 1, "正文和全部扩展字段线序完整");
        That(reader.ReadByte() == 2, "原active tail项数保持");
        foreach (ushort id in new ushort[] { 2037, 2395 })
        {
            That(reader.ReadUInt16() == id, "active tail原事件身份保留");
            for (int index = 0; index < 12; index++)
                That(reader.ReadUInt32() == (id == 2037 && index == 0 ? 8u : 0u), "完整12数值块保留");
        }
        That(input.Position == input.Length, "解压108消费至EOF，无尾段丢失或多余字节");
        Bytes(plain, Inflate(SeriaLuckPackets.EventInfoCompressed(extra, active, [page with { ShowItemIds = [] }])),
            "未登记日历道具时完整旧目录正文及尾段逐字节保持");
        var other = page with { EventId = 12345, Content = "另一活动说明", ShowItemIds = [303] };
        byte[] twoPages = Inflate(SeriaLuckPackets.EventInfoCompressed(extra, [2395, 12345],
            [page, other with { ShowItemIds = [] }]));
        byte[] expectedItems = AppendItems(AppendItems(twoPages, page.Content, "\n[show_item_index] 2 101 202"),
            other.Content, "\n[show_item_index] 1 303");
        Bytes(expectedItems, Inflate(SeriaLuckPackets.EventInfoCompressed(extra, [2395, 12345],
            [page with { ShowItemIds = [101, 202] }, other])),
            "不同活动独立登记多个模板，只有Content长度及标签改变，原设计页正文、其他目录和48B数值尾完整保持");
        foreach (int[] ids in new int[][] { [0], [-1], [101, 101] })
            Reject<InvalidDataException>(() => SeriaLuckPackets.EventInfoCompressed(extra, active,
                [page with { ShowItemIds = ids }]), "日历展示道具拒绝无效或重复模板");
        foreach (ushort id in new ushort[] { 0, 9, 2037, 2532 })
            Reject<InvalidDataException>(() => SeriaLuckPackets.EventInfoCompressed(extra, active, [page with { EventId = id }]),
                "设计页不能覆盖保留或既有目录ID");
        Reject<InvalidDataException>(() => SeriaLuckPackets.EventInfoCompressed(extra, active, [page, page]), "同页不能重复入目录");
    }

    static byte[] AppendItems(byte[] body, string content, string suffix)
    {
        byte[] original = CharacterName.Encoding.GetBytes(content);
        int at = body.AsSpan().IndexOf(original);
        That(at >= 4, "独立期望定位原页完整Content");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(body.AsSpan(0, at - 4));
        byte[] replacement = CharacterName.Encoding.GetBytes(content + suffix);
        writer.Write(replacement.Length);
        writer.Write(replacement);
        writer.Write(body.AsSpan(at + original.Length));
        return output.ToArray();
    }

    static byte[] Inflate(byte[] body)
    {
        using var compressed = new MemoryStream(body);
        using var stream = new ZLibStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        stream.CopyTo(plain);
        return plain.ToArray();
    }

    static string ReadString(BinaryReader reader) => CharacterName.Encoding.GetString(reader.ReadBytes(checked((int)reader.ReadUInt32())));

    static byte[] LegacyBody(ushort[] extra, ushort[] active)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)(1 + extra.Length));
        foreach (ushort id in extra.Prepend((ushort)2037))
        {
            writer.Write(id); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0);
        }
        writer.Write((byte)(1 + active.Length));
        foreach (ushort id in active.Prepend((ushort)2037))
        {
            writer.Write(id);
            for (int i = 0; i < 12; i++) writer.Write(id == 2037 && i == 0 ? 8u : 0u);
        }
        return output.ToArray();
    }
}
