using static DfoServer.Testing.TestCheck;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

/// <summary>
/// CMD 334 `AUCTION_BUY_ITEM_APIECE` 的**请求抓取桩**：注册存在性、任意长度请求的解析容忍、
/// **零状态变更**（整库逐表 dump 前后一致），以及「客户端不会把这次请求当成成交」的做法。
///
/// 这一组只覆盖 334：**185 的布局已由两个实机样本定案并接入真实购买**（解析、口径、事务与失败矩阵
/// 在 <see cref="AuctionBuyTests"/>）。桩仍存在的理由不变 —— 334 连前导字节的精确偏移都只有参考实现
/// 自标「无法 100% 静态确定」，而实机证实「购买」按钮发的是 185，因此 334 不能用任何偏移去动资产。
///
/// 判据分三层：
///   ① 纯字节层（<see cref="Layout"/>）：参考布局的试探读数与长度容忍，短包**不补 0、不猜测**；
///   ② 分派层（<see cref="Capture"/>）：334 注册了、返回 <see cref="CommandResult.Unhandled"/>
///      ⇒ **一个包都不发**（客户端只有收到应答才可能把购买当成成交）；
///   ③ 持久化层：真实文件库在全部请求前后**整库逐表 dump 完全相同**（挂牌、背包、金币、审计、UID 序列）。
/// </summary>
internal static class AuctionBuyProbeTests
{
    /// <summary>参考实现 §3.4 的 26 字节样本：前导 0 + 出价金额 555 + 挂牌 31 + 到期 0x6A9A1A51 + 13B 全 0。</summary>
    const string ReferenceSample = "002B0200001F000000511A9A6A00000000000000000000000000";

    const int SampleTemplate = 10400;
    static readonly Location Room = new(38, 1, 450, 234, 0);

    /// <summary>预置模板：只用于给挂牌造一份真实的条目快照字节，本组不涉及购买资格判定。</summary>
    static readonly Dictionary<int, ItemTemplate> Templates = new()
    {
        [SampleTemplate] = new(SampleTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "残缺的牛皮背心")
    };

    public static void Run()
    {
        int checks = Count;
        Layout();
        Capture().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} auction buy capture assertions");
    }

    /// <summary>
    /// 试探布局的字节层断言：**185 已退出试探**（布局定案后不允许同一偏移有两处定义），
    /// 334 的参考偏移读出的值、长度不足时字段保持缺失（不补 0）、0..40 字节都不抛异常。
    /// </summary>
    static void Layout()
    {
        // ① 185：布局定案 ⇒ 试探入口关闭，长度只作为「参考长度」的读数保留。
        Reject<InvalidOperationException>(() => AuctionBuyProbes.Describe(AuctionBuyCommand.Bidding, new byte[30]),
            "★ 185 已退出试探读数：布局定案后偏移只有 AuctionBuyRequest 一份定义（防两处漂移）");
        That(AuctionBuyProbes.ReferenceSize(AuctionBuyCommand.Bidding) == AuctionBuyRequest.BodySize &&
            AuctionBuyRequest.BodySize == 30,
            "185 的参考长度就是定案的 30 字节（不再是参考实现那套 26B 猜测）");
        That(AuctionBuyProbes.LayoutName(AuctionBuyCommand.Bidding) == "settled-185-buy-30" &&
            AuctionBuyProbes.Evidence(AuctionBuyCommand.Bidding).Contains("两样本", StringComparison.Ordinal),
            "日志里的布局名与出处指向「两样本差分定案」，事后不必再回忆来源");

        // ② 334：参考布局连前导字节位置都未定，因此只锁「字段顺序与长度下界」，
        //    并确认它同样容忍任意长度 —— 精确偏移由实机请求体离线锁定，不在这里猜。
        That(AuctionBuyProbes.ReferenceSize(AuctionBuyCommand.BuyItemApiece) == 34 &&
            AuctionBuyProbes.LayoutName(AuctionBuyCommand.BuyItemApiece) == "reference-334-buy-34",
            "334 的参考长度下界 34 字节（前导1+总价4+数量4+挂牌id8+13B串+道具id4）");
        var buy = AuctionBuyProbes.Describe(AuctionBuyCommand.BuyItemApiece, new byte[40]);
        That(buy.Fields.Select(field => field.Name).SequenceEqual(
            new[] { "Leading", "TotalPrice", "Count", "ListingIdLow", "ListingIdHigh", "ItemKeyString", "ItemKey" }),
            "334 的试探字段顺序按参考实现 §3.10 排列");
        That(buy.Fields.All(field => field.Complete) && buy.Fields.All(field => field.Note.Contains("未定", StringComparison.Ordinal)),
            "★ 334 的每个字段都标注了「偏移未定」：本客户端没有实测过这条布局，读到的值只能算试探");

        byte[] reference = Convert.FromHexString(ReferenceSample);
        var probe = AuctionBuyProbes.Describe(AuctionBuyCommand.BuyItemApiece, reference);
        That(probe.BodySize == 26 && !probe.BodyComplete && probe.TailHex.Length == 0,
            "26 字节的参考样本短于 334 的 34 字节下界：在日志里标成「短于参考布局」，尾部读值不越界");
        That(Field(probe, "TotalPrice").Value == 555 && probe.Windows[1].Value == 555,
            "参考样本在 [1..4] 上读出金额 555（金额这一段与 185 的位置恰好相同）");
        That(Field(probe, "ListingIdLow").Value != 31 && probe.Windows[5].Value == 31,
            "★ 334 的挂牌 id 候选偏移 [9] 读不出 31，而窗口读值在 [5] 上读到了 —— 两条命令的偏移表不同，334 只能算试探");
        That(probe.Windows.Length == 16, "★ 窗口读值取满 16 个：即使字段表读不到，离线对齐偏移仍有原始读数可用");

        var shortProbe = AuctionBuyProbes.Describe(AuctionBuyCommand.BuyItemApiece, new byte[5]);
        That(Field(shortProbe, "TotalPrice").Complete && !Field(shortProbe, "Count").Complete &&
            Field(shortProbe, "Count").Value is null && Field(shortProbe, "Count").Raw.Length == 0,
            "★ 5 字节包体：读得到的字段给值，读不到的标成缺失且**不补 0**（否则「偏移不对」会被误读成「客户端发了 0」）");
        That(!shortProbe.BodyComplete && shortProbe.Note.Contains("短于参考布局", StringComparison.Ordinal) && shortProbe.Windows.Length == 2,
            "短包在日志里明确写出「短于参考布局」，窗口读值随之截断（日志有界）");

        var empty = AuctionBuyProbes.Describe(AuctionBuyCommand.BuyItemApiece, []);
        That(empty.BodySize == 0 && !empty.BodyComplete && empty.Fields.All(field => !field.Complete) &&
            empty.Windows.IsEmpty && empty.TailHex.Length == 0,
            "空包体：全部字段缺失、窗口为空，不抛异常（桩只记录，不按长度拒绝）");

        bool tolerated = true;
        for (int length = 0; length <= 40; length++)
            tolerated &= AuctionBuyProbes.Describe(AuctionBuyCommand.BuyItemApiece, new byte[length]).BodySize == length;
        That(tolerated, "0..40 字节的任意请求体都能解析尝试（不抛异常、不按长度拒绝）：抓包优先于校验");
        That(AuctionBuyProbes.Describe(AuctionBuyCommand.BuyItemApiece, new byte[64]).Windows.Length == AuctionBuyProbes.WindowCount,
            "窗口读值上限固定为 16，不随包体增长（日志不会因为大包变成巨型行）");
    }

    /// <summary>
    /// 分派层与持久化层：真实文件库 + 真实会话 + 真实发送队列，把 334 用多种长度打进去，断言
    /// 「一个包都没发」且「整库逐表 dump 一个字节都没变」，同时确认每次请求都留下了可离线比对的日志。
    /// </summary>
    static async Task Capture()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-auction-buy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("auction-buy", () => { });
            var repository = new CharacterRepository(path);
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var service = new AuctionService(repository, new AuctionCategoryResolver(catalog), catalog, pieces,
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var inventory = new InventorySnapshot([
                new(0, new(InventoryListType.Main, 27), new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 })
            ]);
            var character = repository.Create(owner, new("Buyer", Room, null) { Level = 95, Inventory = inventory });
            // 一条机器人卖家的在售挂牌（seller_character_id IS NULL）：桩不得碰它，
            // 后面「整库 dump 不变」正好把「挂牌行没有被改写/没有成交」一起锁住。
            repository.InsertListings([new(null, "拍卖机器人甲", SampleTemplate, (byte)ItemKind.Equipment, 1,
                AuctionPackets.ItemEntrySnapshot(new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998)), 12_000, 17_000,
                now, now + 86_400)]);

            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), Room, Room, new TownDock(0, [], []),
                [], new Dictionary<uint, uint[]>());
            var stats = new CharacterStatComputer(new CharacterCatalog(new Dictionary<byte, CharacterProfile>()));
            var projection = new InventoryProjection(stats, new TownProjection(towns, stats, null, pieces), pieces);
            string logDirectory = Path.Combine(root, "Logs");
            // using：日志写入器持有 server.jsonl 的文件句柄，不释放就删不掉临时目录（本组在 finally 里整目录删除）。
            using var log = new ServerLog(logDirectory);
            var handler = new AuctionHandler(catalog, service, projection,
                static () => AuctionProbeSettings.Disabled, static () => AuctionProbeSettings.Disabled,
                static _ => AuctionProbeSettings.Disabled, log);
            var commands = new GameCommandRegistry();
            handler.Register(commands);
            commands.Seal();
            That((ushort)CmdPacketType.AUCTION_BIDDING == 185 && (ushort)CmdPacketType.AUCTION_BUY_ITEM_APIECE == 334,
                "两个 opcode 就是 185 / 334（取自 PacketTypes，不抄别的客户端版本编号）");

            var session = new GameSession(repository, towns, owner);
            session.Select(character.Slot);
            session.MarkReady();
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var sent = new List<string>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, action) => sent.Add(action));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);

            // 各种长度都打一遍：空包、参考实现那套 26B、正好 34B 下界、带尾部字节的大包。
            byte[] reference = Convert.FromHexString(ReferenceSample);
            (ushort Opcode, byte[] Body)[] requests =
            [
                (334, []),
                (334, reference),
                (334, new byte[34]),
                (334, new byte[40]),
                (334, [.. reference, .. new byte[38]])
            ];
            string before = Snapshot(root);
            long listingsBefore = Scalar(root, "SELECT count(*) FROM auction_listings WHERE state=0");
            long goldBefore = Scalar(root, "SELECT gold FROM characters");
            foreach (var request in requests)
            {
                var result = await commands.DispatchAsync(context, new Packet(1, request.Opcode, request.Body));
                That(result == CommandResult.Unhandled,
                    $"★ 334 / {request.Body.Length}B：桩返回 Unhandled（**一个应答都不发**）");
            }
            That(stream.Length == 0 && sent.Count == 0,
                "★ 全部请求之后发送队列仍是空的：客户端收不到任何包，界面不可能把这次购买当成成交");
            That(Snapshot(root) == before,
                "★★ 整库逐表 dump 前后完全一致（挂牌/背包/金币/审计/序号）：桩没有产生任何状态变更");
            That(Scalar(root, "SELECT count(*) FROM auction_listings WHERE state=0") == listingsBefore &&
                Scalar(root, "SELECT gold FROM characters") == goldBefore,
                "挂牌数（在售）与角色金币前后一致");

            log.Flush();
            var logged = Logged(Path.Combine(logDirectory, "server.jsonl"), "auction_buy_request");
            That(logged.Count == requests.Length,
                $"每次请求都留下一条 auction_buy_request 日志（{logged.Count}/{requests.Length}）");
            That(logged.All(entry => entry.GetProperty("Implemented").GetBoolean() == false &&
                entry.GetProperty("StateChange").GetString() == "none" && entry.GetProperty("Reply").GetString() == "none"),
                "★ 每条日志都写明「未实现 / 无状态变更 / 无应答」——事后读日志就能确认桩没有越界");
            if (logged.Count != requests.Length)
                return;
            for (int index = 0; index < requests.Length; index++)
            {
                var entry = logged[index];
                That(entry.GetProperty("Opcode").GetInt32() == requests[index].Opcode &&
                    entry.GetProperty("Length").GetInt32() == requests[index].Body.Length &&
                    entry.GetProperty("Body").GetString() == Convert.ToHexString(requests[index].Body),
                    $"日志 [{index}] 记下 opcode、长度与**完整请求体 hex**（{requests[index].Opcode} / {requests[index].Body.Length}B）");
            }
            // 第二条就是参考样本：日志里的试探读数必须能直接与参考实现的金额/挂牌对上，
            // 否则「抓到了」也等于没抓到（离线比对要的就是这几个数字）。
            var sample = logged[1];
            That(Field(sample, "TotalPrice").GetProperty("Value").GetInt64() == 555 &&
                Window(sample, 5).GetInt64() == 31,
                "★ 参考样本那次的日志里能直接读到金额 555，窗口读数在 [5] 上读到挂牌 31（与参考实现吻合）");
            That(sample.GetProperty("Windows").GetArrayLength() == AuctionBuyProbes.WindowCount &&
                Field(sample, "TotalPrice").GetProperty("Note").GetString()!.Contains("未定", StringComparison.Ordinal),
                "日志同时给出 off0..15 的窗口读值与字段说明（每个字段都标注偏移未定）");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 整库逐表 dump：不逐一列举「本业务假设会碰到的表」——假设错的那张表恰好就是漏测的那张。
    /// 用 <c>sqlite_schema</c> 枚举全部表（含 <c>sqlite_sequence</c>：UID/自增序号的推进也要被锁住）。
    /// </summary>
    static string Snapshot(string root)
    {
        using var db = Open(root);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT group_concat(name,char(10)) FROM (SELECT name FROM sqlite_schema WHERE type='table' AND name<>'sqlite_schema' ORDER BY name)";
        var tables = ((string)command.ExecuteScalar()!).Split('\n');
        var text = new StringBuilder();
        foreach (string table in tables)
        {
            command.CommandText = "SELECT * FROM [" + table + "]";
            using var reader = command.ExecuteReader();
            text.Append(table).Append('{');
            while (reader.Read())
                for (int index = 0; index < reader.FieldCount; index++)
                    text.Append(reader.IsDBNull(index) ? "-" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)).Append(',');
            text.Append("};");
        }
        return text.ToString();
    }

    static long Scalar(string root, string sql)
    {
        using var db = Open(root);
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static SqliteConnection Open(string root)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "game.db"),
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        db.Open();
        return db;
    }

    /// <summary>读回服务器日志里某一类记录（真实 jsonl 行，不是内存对象）。</summary>
    static List<JsonElement> Logged(string path, string kind)
    {
        var details = new List<JsonElement>();
        // 日志写入器持有写句柄，因此必须**共享读写**打开：`File.ReadLines` 默认只共享读，会与写入器互斥。
        // 与运行中实例的日志读数口径一致（服务端日志被占用时同样用共享读打开）。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("Kind").GetString() == kind)
                details.Add(document.RootElement.GetProperty("Detail").Clone());
        }
        return details;
    }

    static AuctionBuyField Field(AuctionBuyProbe probe, string name) => probe.Fields.Single(field => field.Name == name);

    static JsonElement Field(JsonElement detail, string name) =>
        detail.GetProperty("Fields").EnumerateArray().Single(field => field.GetProperty("Name").GetString() == name);

    /// <summary>日志里的窗口读值（off0..15 的 i32 读数）。</summary>
    static JsonElement Window(JsonElement detail, int offset) =>
        detail.GetProperty("Windows").EnumerateArray().Single(window => window.GetProperty("Offset").GetInt32() == offset)
            .GetProperty("Value");
}
