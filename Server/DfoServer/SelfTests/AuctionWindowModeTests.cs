using static DfoServer.Testing.TestCheck;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DfoServer.Development.Auction;
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
/// 拍卖行的**窗口 mode 分流**（搜索 186/187 与「我的拍卖品」188/189/190 的请求首字节）。
///
/// 背景（2026-10-12 实机）：普通拍卖行窗口发 `mode=0`、金币寄售窗口发 `mode=1`，
/// 两个窗口的数据是**两套**（拍卖行挂牌 vs 寄售金币）。原先两条路径都不看 mode、
/// 一律把拍卖行挂牌发回去，客户端就把**我的装备画进了金币寄售的「确认寄售的金币」面板**。
/// 金币寄售本仓库未接入 ⇒ 现在的契约是：`mode=0` 走真实数据，**`mode≠0` 一律回空列表**
/// （`flag + mode(回显) + count=0`），既不伪造寄售数据、也不让面板停在无响应，
/// 并且**不消耗**探针候选（金币寄售的请求不该推进「我的拍卖品」的记录布局轮换）。
///
/// 只在**真实命令表 + 真实会话 + 真实发送队列**上验证：这一层才看得出「谁发的 mode、
/// 服务端回了什么、发出去的字节是什么」，而 mode 回显错一个字节就是把记录灌进另一类窗口。
/// </summary>
internal static class AuctionWindowModeTests
{
    const int SampleTemplate = 10400;
    static readonly Location Room = new(38, 1, 450, 234, 0);

    public static void Run()
    {
        int checks = Count;
        Transport().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} auction window-mode assertions");
    }

    /// <summary>真实链路：命令表派发 186/187/188/189 ⇒ 应答字节、窗口回显、日志与零副作用。</summary>
    static async Task Transport()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-auction-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("auction-mode", () => { });
            var repository = new CharacterRepository(path);
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var service = new AuctionService(repository, new AuctionCategoryResolver(catalog), catalog, pieces,
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var me = repository.Create(owner, new("ModeTester", Room, null) { Level = 95, Inventory = new([], Gold: 1_000_000) });
            // 两条挂牌：**自己的**（188 的数据源）与**机器人的**（186/187 搜索能看到，搜索会排除自己上架的）。
            long mine = Seed(path, repository, me.Id, me.Name, 17_000, now);
            long other = Seed(path, repository, null, "拍卖机器人甲", 20_000, now);
            That(mine > 0 && other > mine, "夹具备好两条挂牌：自己的 + 机器人的（缺任一条都会让断言失去意义）");

            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), Room, Room, new TownDock(0, [], []),
                [], new Dictionary<uint, uint[]>());
            var stats = new CharacterStatComputer(new CharacterCatalog(new Dictionary<byte, CharacterProfile>()));
            var projection = new InventoryProjection(stats, new TownProjection(towns, stats, null, pieces), pieces);
            string logDirectory = Path.Combine(root, "Logs");
            using var log = new ServerLog(logDirectory);
            // 探针默认关闭（`off` 也按页签给默认候选）；第 ④ 组才把「我的拍卖品」换成轮换探针。
            var off = AuctionProbe.Parse("off");
            AuctionProbe myLists = off;
            var handler = new AuctionHandler(catalog, service, projection,
                () => off.NextSearch(), () => off.NextAverage(), panel => myLists.NextMyList(panel), log);
            var commands = new GameCommandRegistry();
            handler.Register(commands);
            commands.Seal();

            var session = new GameSession(repository, towns, owner);
            session.Select(me.Slot);
            session.MarkReady();
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var sent = new List<(ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0,
                (_, opcode, _, body, action) => sent.Add((opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);

            // ① 188（我的上架）mode=0（普通拍卖行窗口）：真发记录，作为「门闸没有误伤正常路径」的对照。
            sent.Clear();
            var replied = await commands.DispatchAsync(context, new Packet(1, 188, [0x00]));
            That(replied == CommandResult.Handled && sent.Count == 1 && sent[0].Opcode == 188 && sent[0].Action == "auction_my_list",
                "188 mode=0 走「我的拍卖品」正常路径：一个应答包，动作名 auction_my_list");
            That(sent[0].Body.Length == 3 + 151 && sent[0].Body[1] == 0x00 && sent[0].Body[2] == 1,
                "★ 188 mode=0 = flag(01) + mode(00) + count(1) + 1×151B（自己的那条挂牌真的发出去了）");
            That(BitConverter.ToInt32(sent[0].Body, 3) == mine, "记录 [0..3] 就是自己那条挂牌的 id");

            // ② 188 mode=1（金币寄售窗口「确认寄售的金币」）：**有在售挂牌也只回空列表**。
            sent.Clear();
            string before = Dump(path);
            await commands.DispatchAsync(context, new Packet(1, 188, [0x01]));
            That(sent.Count == 1 && sent[0].Opcode == 188 && sent[0].Body.SequenceEqual<byte>([0x01, 0x01, 0x00]),
                "★★ 188 mode=1 = flag(01) + mode 回显(01) + count(0)，共 3 字节：金币寄售面板不再被画进拍卖行装备");
            That(sent[0].Action == "auction_my_list_consignment",
                "空列表的动作名区分于 auction_my_list（日志里两条路径可分辨）");
            That(Dump(path) == before, "mode=1 的空列表零写库");

            // ③ 189 mode=1：计数宽度随页签（WORD），空列表因此是 4 字节而不是 3。
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 189, [0x01]));
            That(sent.Count == 1 && sent[0].Opcode == 189 && sent[0].Body.SequenceEqual<byte>([0x01, 0x01, 0x00, 0x00]),
                "189 mode=1 = flag + mode + count(WORD=0)，共 4 字节（头部宽度按页签取，不是写死的 3）");

            // ④ 金币寄售的请求**不消耗**候选：mode=1 之后 mode=0 仍用同一个候选，第二次 mode=0 才轮换。
            myLists = AuctionProbe.Parse("mylist=a21-151,head-only");
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 188, [0x01]));
            That(sent[0].Body.Length == 3, "换成轮换探针后，mode=1 仍然是 3 字节空列表");
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 188, [0x00]));
            That(sent[0].Body.Length == 3 + 151, "★ mode=1 没有消耗候选：下一次 mode=0 用的还是第一个候选 a21-151");
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 188, [0x00]));
            That(sent[0].Body.SequenceEqual<byte>([0x01, 0x00, 0x00]),
                "再点一次才轮到第二个候选 head-only（轮换本身没坏）");

            // ⑤ 186（按道具搜索）mode=0：真发记录，且搜索排除自己上架的挂牌（只剩机器人的那条）。
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 186, ItemSearchBody(0x00)));
            That(sent.Count == 1 && sent[0].Opcode == 186 && sent[0].Action == "auction_search_reply",
                "186 mode=0 走真实挂牌读路径");
            That(BitConverter.ToInt32(sent[0].Body, 2) == 1 && BitConverter.ToUInt16(sent[0].Body, 6) == 1 &&
                sent[0].Body.Length == 8 + 168,
                "★ 186 mode=0 = flag + mode(00) + total(=1) + count(u16=1) + 1×168B（自己的那条被排除，机器人的那条在内）");

            // ⑥ 186 mode=1（金币寄售窗口的「金币搜索」）：同样只回空列表。
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 186, ItemSearchBody(0x01)));
            That(sent.Count == 1 && sent[0].Opcode == 186 &&
                sent[0].Body.SequenceEqual<byte>([0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
                "★★ 186 mode=1 = flag + mode 回显 + total(0) + count(0)，共 8 字节：金币寄售的「金币搜索」不再列出拍卖行装备");
            That(sent[0].Action == "auction_search_consignment_reply", "搜索空列表的动作名区分于 auction_search_reply");

            // ⑦ 187（分类搜索）mode=1 也回空列表；mode=0 仍然真发 —— 门闸只挡寄售窗口，不误伤普通拍卖行。
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 187, CategorySearchBody(0x01)));
            That(sent[0].Opcode == 186 && sent[0].Body.SequenceEqual<byte>([0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
                "187 mode=1 与 186 同款空列表（分类搜索的寄售窗口路径同样不发明数据）");
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 187, CategorySearchBody(0x00)));
            That(BitConverter.ToInt32(sent[0].Body, 2) >= 1 && sent[0].Body[1] == 0x00,
                "★ mode=0 的 187 分类搜索不受影响（没有把普通拍卖行一起关掉），且 mode 仍回显 0");

            // ⑧ 日志：两条路径分别记账，且金币寄售那条永远带 mode=1。
            log.Flush();
            string jsonl = Path.Combine(logDirectory, "server.jsonl");
            var myListConsignment = Logged(jsonl, "auction_my_list_consignment");
            That(myListConsignment.Count == 3 && myListConsignment.All(entry => entry.GetProperty("Mode").GetInt32() == 1) &&
                myListConsignment.All(entry => entry.GetProperty("Note").GetString()!.Length != 0),
                $"★ 每次 mode=1 的「我的拍卖品」都写 auction_my_list_consignment 并记 mode=1（{myListConsignment.Count} 条）");
            var myList = Logged(jsonl, "auction_my_list");
            That(myList.Count >= 3 && myList.All(entry => entry.GetProperty("Mode").GetInt32() == 0),
                "auction_my_list 只记普通拍卖行窗口（mode=0）——两条路径没有混在一条日志里");
            var searchConsignment = Logged(jsonl, "auction_search_consignment");
            That(searchConsignment.Count == 2 && searchConsignment.All(entry => entry.GetProperty("Mode").GetInt32() == 1),
                $"★ 186/187 的 mode=1 请求写 auction_search_consignment（{searchConsignment.Count} 条，含分类搜索那条）");
            var search = Logged(jsonl, "auction_search");
            That(search.All(entry => entry.GetProperty("Mode").GetInt32() == 0),
                "★ auction_search 只记普通拍卖行窗口（mode=0）：mode=1 的请求在到达探针/读路径之前就分流了");
        }
        finally
        {
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }

    /// <summary>186（按道具搜索）请求体：mode + 偏移 + 保留 + 分类 + 道具模板 id（末两字节为固定尾）。</summary>
    static byte[] ItemSearchBody(byte mode)
    {
        var body = new byte[14];
        body[0] = mode;
        BitConverter.GetBytes(SampleTemplate).CopyTo(body, 10);
        return body;
    }

    /// <summary>187（按分类搜索）请求体：20 字节，mode + 分类 id @5（其余为实机样本的固定位）。</summary>
    static byte[] CategorySearchBody(byte mode)
    {
        var body = new byte[20];
        body[0] = mode;
        body[5] = 0xA5;      // 40101：未采集过的分类 id ⇒ 服务端不过滤（本组只关心 mode 分流）
        body[6] = 0x9C;
        body[7] = 0x07;
        body[9] = 0x1F;
        body[19] = 0x08;
        return body;
    }

    /// <summary>插入一条挂牌并返回 listing_id（真实批量插入入口，不经业务上架）。</summary>
    static long Seed(string path, CharacterRepository repository, long? sellerId, string sellerName, int buyout, long now)
    {
        var core = new ItemCore(SampleTemplate, ItemKind.Equipment, 1_000_000 + (int)(sellerId ?? 0)) { Attr = 2, Durability = 45 };
        repository.InsertListings([new(sellerId, sellerName, SampleTemplate, (byte)ItemKind.Equipment, 1,
            AuctionPackets.ItemEntrySnapshot(core), buyout - 1_000, buyout, now, now + 86_400)]);
        return Query(path, "SELECT max(listing_id) FROM auction_listings");
    }

    /// <summary>整库逐表 dump：任何一个字节的变化都会让「零副作用」断言失败。</summary>
    static string Dump(string path)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        var tables = new List<string>();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                tables.Add(reader.GetString(0));
        var rows = new List<object>();
        foreach (string table in tables)
        {
            command.CommandText = "SELECT * FROM [" + table + "] ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new { Table = table, Values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray() });
        }
        return JsonSerializer.Serialize(rows);
    }

    static long Query(string path, string sql)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        db.Open();
        return db;
    }

    /// <summary>读回服务器日志里某一类记录（真实 jsonl 行，共享读写打开）。</summary>
    static List<JsonElement> Logged(string path, string kind)
    {
        var details = new List<JsonElement>();
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
}
