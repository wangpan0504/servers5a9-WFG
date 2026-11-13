using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
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
/// 拍卖行**保证金**（10,000 金币/笔）与**到期结算**。
///
/// 三条口径全部来自用户（2026-10-12 定案），实现按它们逐条锁定：
///   ① **挂牌阶段扣**：`PlanRegister` 把保证金与道具扣减写进同一份库存计划，余额不足直接拒绝
///      （客户端可见码 `0xd2`），绝不出现「界面显示上架成功、金币没被收」；
///   ② **取消上架不退**：CMD 184 只把道具还回背包，金币一分不动 —— 保证金的意义就在于此；
///   ③ **到期未售退还**：到期结算把「道具退回 + 保证金 10,000」装进**一封系统邮件**
///      （`auction:return:{listingId}`），并把挂牌置为 `state=3`；幂等判据是状态条件更新，
///      重复结算不会退第二次。
///
/// 分四组：<see cref="Register"/>（扣款与余额不足）、<see cref="Cancel"/>（不退）、
/// <see cref="Expire"/>（规划/提交/幂等/跳过）、<see cref="Session"/>（188 读路径真的触发结算）。
/// 数据库是真实文件库（revision、审计、外键、唯一约束、事务回滚都是真的），模板显式预置因此不需要 PVF。
/// </summary>
internal static class AuctionDepositTests
{
    const int SampleTemplate = 10400;      // 与其它拍卖行自测同一件样本装备
    const int UnknownTemplate = 999_999;   // 目录里没有：到期结算必须跳过而不是把道具弄丢
    static readonly Location Room = new(38, 1, 450, 234, 0);

    public static void Run()
    {
        int checks = Count;
        Register();
        Cancel();
        Expire();
        Session().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} auction deposit/expiry assertions");
    }

    /// <summary>挂牌阶段扣保证金：金额、边界、余额不足的拒绝与客户端码、提交后的库内余额与审计。</summary>
    static void Register()
    {
        using var f = new Fixture(100_000);
        var plan = f.PlanRegister();
        That(plan.Inventory.Before.Gold == 100_000 && plan.Inventory.After.Gold == 90_000,
            "★ 挂牌保证金在上架计划里就扣掉：100,000 → 90,000（与道具扣减同一份库存计划）");
        That(plan.DepositGold == AuctionService.ListingDepositGold,
            "计划里记下本次收了多少保证金（落库后成为到期退还的唯一依据）");
        That(plan.Inventory.After.Items.Length == plan.Inventory.Before.Items.Length - 1,
            "同一次计划里也扣掉了源道具（保证金与道具同一事务提交）");

        // 边界：正好一个保证金可以挂牌，差 1 金币就拒绝。
        using var exact = new Fixture(AuctionService.ListingDepositGold);
        That(exact.PlanRegister().Inventory.After.Gold == 0,
            $"金币恰好 {AuctionService.ListingDepositGold}：可以挂牌，余额归 0");
        using var poor = new Fixture(AuctionService.ListingDepositGold - 1);
        var refused = poor.DecideRegister();
        That(refused.Error == AuctionRegisterError.InsufficientGold && refused.Options is null,
            "★ 金币不足保证金 ⇒ 规划阶段直接拒绝（不扣道具、不写挂牌）");
        That(AuctionRegisterErrors.ToClientCode(AuctionRegisterError.InsufficientGold) == AuctionRegisterErrors.Generic &&
            AuctionRegisterErrors.Whitelist.Contains(AuctionRegisterErrors.Generic),
            "余额不足映射到客户端白名单里的可见码 0xd2（不会漏成「未知码 = 成功」）");

        // 提交：金币、挂牌、审计三者一致。
        using var commit = new Fixture(100_000);
        var committed = commit.Commit(commit.PlanRegister());
        That(committed.Character is not null, "上架提交成功（返回提交后的角色快照）");
        That(commit.Gold() == 90_000, "★ 提交后库里的金币 = 100,000 − 10,000");
        That(commit.ListingState(commit.LastListingId()) == AuctionListingState.Active, "新挂牌仍是 state=0（在售）");
        That(commit.ListingDeposit(commit.LastListingId()) == AuctionService.ListingDepositGold,
            "★ 保证金逐行落库（deposit_gold = 10,000）：到期退多少由这一行说了算，不靠当时的政策常数");
        var audits = commit.CurrencyAudits("auction_register");
        That(audits.Length == 1 && audits[0].Before == 100_000 && audits[0].After == 90_000,
            "★ 保证金扣款落进 currency_audit_log（action=auction_register，前后余额真实）");
        That(commit.MailCount() == 0, "挂牌阶段不发任何邮件（保证金只是扣款）");
    }

    /// <summary>取消上架不退保证金：道具回背包、金币一分不动、没有任何退款审计。</summary>
    static void Cancel()
    {
        using var f = new Fixture(100_000);
        var committed = f.Commit(f.PlanRegister());
        That(committed.Character is not null, "先成功挂上一件（扣了 10,000 保证金）");
        int listingId = checked((int)f.LastListingId());

        var plan = f.PlanCancel(listingId);
        var cancelled = f.Repository.CancelListing(f.Owner, f.Character, plan);
        That(cancelled.Error == AuctionCancelError.None && cancelled.Character is not null, "取消上架提交成功");
        That(f.Gold() == 90_000, "★ 取消上架**不退**保证金（用户口径）：金币仍是挂牌后的 90,000");
        That(f.CurrencyAudits("auction_cancel").IsEmpty && f.CurrencyAudits("auction_register").Length == 1,
            "★ 取消路径不产生任何退款审计（只有挂牌那一次的扣款）");
        That(f.ListingState(listingId) == AuctionListingState.Cancelled, "挂牌置为已下架（state=2）");
        That(cancelled.Character!.Inventory.Items.Any(item => item.Core.ItemId == SampleTemplate),
            "道具回到背包（不退的是钱，不是道具）");
    }

    /// <summary>到期未售：规划出「一封退回邮件」、提交后退道具+退保证金、重复结算不重复退。</summary>
    static void Expire()
    {
        using var f = new Fixture(100_000);
        long expired = f.SeedListing(f.Character.Id, f.Character.Name, SampleTemplate, expiresAtUnix: f.Now - 60,
            depositGold: AuctionService.ListingDepositGold);
        long active = f.SeedListing(f.Character.Id, f.Character.Name, SampleTemplate, expiresAtUnix: f.Now + 3600);
        long robot = f.SeedListing(null, "拍卖机器人甲", SampleTemplate, expiresAtUnix: f.Now - 60);
        long other = f.SeedListing(4242, "别人", SampleTemplate, expiresAtUnix: f.Now - 60);
        That(f.Repository.LoadExpiredListings(f.Character.Id, f.Now).Length == 1,
            "★ 到期结算的输入只含「本人的、已到期且仍在售的」那一行（在售的、机器人的、别人的都不在内）");

        var plan = f.Service.PlanExpire(f.Character, f.Now);
        That(plan.Rows.Length == 1 && plan.Rows[0].ListingId == expired && plan.Skipped.IsEmpty,
            "★ 规划出一行到期结算（另一行还没到期 ⇒ 不结算）");
        var mail = plan.Rows[0].Mail;
        That(mail.Key == AuctionService.ReturnKey(plan.Rows[0].ListingId) && mail.Key == $"auction:return:{expired}",
            "退回邮件的幂等键 = auction:return:{listingId}");
        That(mail.Gold == AuctionService.ListingDepositGold && mail.Gold == 10_000,
            "★ 保证金按**这一行实际收过的金额**原额退还（10,000）");
        That(mail.Items.Length == 1 && mail.Items[0].Core.ItemId == SampleTemplate && mail.Required,
            "退回邮件里带着那件道具，且这封必须投出去（投不出去整批回滚，不丢道具）");

        var result = f.Service.SettleExpired(f.Owner, f.Character, plan);
        That(result.SettledCount == 1 && result.Rows[0].Delivered && result.Rows[0].Note!.Contains("mail_id=", StringComparison.Ordinal),
            "提交成功：状态已流转 + 退回邮件已投递（note 里带 mail_id）");
        That(f.ListingState(expired) == AuctionListingState.Expired, "★ 挂牌置为已过期（state=3）");
        That(f.ListingState(active) == AuctionListingState.Active && f.ListingState(robot) == AuctionListingState.Active &&
            f.ListingState(other) == AuctionListingState.Active,
            "在售的、机器人的、别人的三行都没被动过");
        That(f.MailCount() == 1 && f.MailGold() == 10_000 && f.AttachmentCount() == 1,
            "★ 邮箱里多了一封退回邮件：10000 金币 + 1 个道具附件");
        That(f.MailKey() == $"auction:return:{expired}", "邮件按幂等键落库（唯一约束是第二道防线）");
        That(f.Gold() == 100_000, "★ 退的钱进的是托管邮件，不是直接加钱包（卖家领取时才入账）");

        // 幂等：再规划一次没有行；拿旧计划再提交一次也不会退第二份。
        That(f.Service.PlanExpire(f.Character, f.Now).IsEmpty, "★ 已结算的行不再出现在到期规划里（幂等）");
        var again = f.Service.SettleExpired(f.Owner, f.Character, plan);
        That(again.SettledCount == 0 && again.Rows[0].Delivered == false && again.Rows[0].Note!.Contains("影响 0 行", StringComparison.Ordinal),
            "★ 拿旧计划重复提交：条件更新影响 0 行 ⇒ 跳过并记原因，不重复退保证金、不重复退道具");
        That(f.MailCount() == 1 && f.AttachmentCount() == 1, "邮箱仍然只有一封（没有退第二份）");

        // 模板解析不出来的行：不结算、不流转状态、进 Skipped（道具绝不能丢）。
        using var broken = new Fixture(100_000);
        long unknown = broken.SeedListing(broken.Character.Id, broken.Character.Name, UnknownTemplate, expiresAtUnix: broken.Now - 60);
        long good = broken.SeedListing(broken.Character.Id, broken.Character.Name, SampleTemplate, expiresAtUnix: broken.Now - 60);
        var mixed = broken.Service.PlanExpire(broken.Character, broken.Now);
        That(mixed.Rows.Length == 1 && mixed.Rows[0].ListingId == good && mixed.Skipped.Length == 1,
            "★ 一行解不出模板时只跳过它，其余行照常结算（并把它记进 Skipped）");
        broken.Service.SettleExpired(broken.Owner, broken.Character, mixed);
        That(broken.ListingState(unknown) == AuctionListingState.Active,
            "★ 解不出模板的行留在 state=0（状态不流转 ⇒ 道具不会消失，下次访问重试）");
        That(broken.ListingState(good) == AuctionListingState.Expired, "同一批里能结算的那行照常置为已过期");

        // ★ 历史行（本列出现之前上架、没有交过保证金）：只退道具、不退钱 —— 绝不用当前常数造钱。
        using var legacy = new Fixture(100_000);
        long old = legacy.SeedListing(legacy.Character.Id, legacy.Character.Name, SampleTemplate,
            expiresAtUnix: legacy.Now - 60, depositGold: 0);
        var legacyPlan = legacy.Service.PlanExpire(legacy.Character, legacy.Now);
        That(legacyPlan.Rows.Length == 1 && legacyPlan.Rows[0].Mail.Gold == 0,
            "★ 没交过保证金的历史行：退回邮件里金币为 0（不凭空退 10,000）");
        legacy.Service.SettleExpired(legacy.Owner, legacy.Character, legacyPlan);
        That(legacy.ListingState(old) == AuctionListingState.Expired && legacy.MailGold() == 0 && legacy.AttachmentCount() == 1,
            "历史行照样退回道具（金币 0 + 1 个附件），状态已流转");
    }

    /// <summary>真实链路：CMD 188（我的上架）在一次请求里就把到期行结算掉，并且列表里不再有它。</summary>
    static async Task Session()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-auction-expiry-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = System.IO.Path.Combine(root, "game.db");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("auction-expiry", () => { });
            var repository = new CharacterRepository(path);
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var service = new AuctionService(repository, new AuctionCategoryResolver(catalog), catalog, pieces,
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var me = repository.Create(owner, new("ExpiryTester", Room, null) { Level = 95, Inventory = new([], Gold: 100_000) });
            var fixtures = new Fixture(root);
            long listingId = fixtures.SeedListing(me.Id, me.Name, SampleTemplate, expiresAtUnix: now - 60,
                depositGold: AuctionService.ListingDepositGold);

            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), Room, Room, new TownDock(0, [], []),
                [], new Dictionary<uint, uint[]>());
            var stats = new CharacterStatComputer(new CharacterCatalog(new Dictionary<byte, CharacterProfile>()));
            var projection = new InventoryProjection(stats, new TownProjection(towns, stats, null, pieces), pieces);
            string logDirectory = System.IO.Path.Combine(root, "Logs");
            using var log = new ServerLog(logDirectory);
            var off = AuctionProbe.Parse("off");
            var handler = new AuctionHandler(catalog, service, projection,
                () => off.NextSearch(), () => off.NextAverage(), panel => off.NextMyList(panel), log, new OnlineSessionDirectory());
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

            sent.Clear();
            var replied = await commands.DispatchAsync(context, new Packet(1, 188, [0x00]));
            That(replied == CommandResult.Handled && sent.Count == 1 && sent[0].Opcode == 188 &&
                sent[0].Body.SequenceEqual<byte>([0x01, 0x00, 0x00]),
                "★ 188 正常应答空列表：到期行在**读之前**就被结算掉，所以「我的上架」里不再有它");
            That(fixtures.ListingState(listingId) == AuctionListingState.Expired,
                "★ 一次 188 请求就把到期行置为 state=3（本仓库没有定时任务，清扫挂在读路径上）");
            That(fixtures.MailCount() == 1 && fixtures.MailGold() == AuctionService.ListingDepositGold && fixtures.AttachmentCount() == 1,
                "★ 退回邮件（道具 + 10,000 保证金）在同一次请求里投递完成");
            That(fixtures.GoldOf(me.Id) == 100_000, "保证金进托管邮件：钱包金币不变（领取时才入账）");

            log.Flush();
            var settled = Logged(System.IO.Path.Combine(logDirectory, "server.jsonl"), "auction_expire_settled");
            That(settled.Count == 1 && settled[0].GetProperty("Settled").GetInt32() == 1 &&
                settled[0].GetProperty("DepositPolicy").GetInt32() == AuctionService.ListingDepositGold &&
                settled[0].GetProperty("Rows")[0].GetProperty("Gold").GetInt32() == AuctionService.ListingDepositGold,
                "★ 结算写 auction_expire_settled 日志：结算行数、逐行退还金额（与挂牌时的金额同源）都在里面");
        }
        finally
        {
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }

    /// <summary>预置模板：字段取值即本组断言的依据（与拍卖行其它自测同一份样本装备）。</summary>
    static readonly Dictionary<int, ItemTemplate> Templates = new()
    {
        [SampleTemplate] = new(SampleTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "残缺的牛皮背心")
    };

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

    /// <summary>真实文件库 + 一个带金币与一件可上架装备的角色 + 直接读库的核对入口。</summary>
    sealed class Fixture : IDisposable
    {
        readonly string root;
        readonly AuctionListingRepository listings;

        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public AuctionService Service { get; }
        public Character Character { get; private set; }
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public Fixture(uint gold)
        {
            root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-auction-deposit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = System.IO.Path.Combine(root, "game.db");
            Owner = Owners.Acquire("depositor", () => { });
            Repository = new(path);
            listings = new(path);
            var inventory = new InventorySnapshot([
                new(0, new(InventoryListType.Main, 27), new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 })
            ], Gold: gold);
            Character = Repository.Create(Owner, new("Depositor", Room, null) { Level = 95, Inventory = inventory });
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            Service = new AuctionService(Repository, new AuctionCategoryResolver(catalog), catalog,
                new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
        }

        /// <summary>只给「到期结算」那一组用：只需要一条真实库文件，不再建角色。</summary>
        public Fixture(string existingRoot)
        {
            root = existingRoot;
            string path = System.IO.Path.Combine(root, "game.db");
            Owner = Owners.Acquire("depositor-reader", () => { });
            Repository = new(path);
            listings = new(path);
            Service = null!;
            Character = null!;
        }

        /// <summary>用实机样本形状的上架请求规划一次（槽 27、数量 1、一口价 17,000、24 小时）。</summary>
        public AuctionRegisterDecision DecideRegister()
        {
            var body = new byte[AuctionRegisterRequest.BodySize];
            body[2] = 27;
            BitConverter.GetBytes(SampleTemplate).CopyTo(body, 4);
            BitConverter.GetBytes(1).CopyTo(body, 8);
            BitConverter.GetBytes(-1).CopyTo(body, 12);
            BitConverter.GetBytes(17_000).CopyTo(body, 16);
            BitConverter.GetBytes(17_000).CopyTo(body, 20);
            BitConverter.GetBytes(24).CopyTo(body, 24);
            return Service.PlanRegister(Character, AuctionRegisterRequest.Parse(body), Now);
        }

        /// <summary>规划出一次一定合法的上架（夹具里的样本一定通过，`Options` 非 null）。</summary>
        public AuctionRegisterPlan PlanRegister() => DecideRegister().Options!;

        public AuctionListingRegisterResult Commit(AuctionRegisterPlan plan) => Register(plan);

        public AuctionListingRegisterResult Register(AuctionRegisterPlan plan)
        {
            var result = Repository.RegisterListing(Owner, Character, plan);
            if (result.Character is not null)
                Character = result.Character;
            return result;
        }

        public AuctionCancelPlan PlanCancel(int listingId) =>
            Service.PlanCancel(Character, Service.FindListing(listingId),
                AuctionCancelRequest.Parse(CancelBody(listingId, Service.FindListing(listingId)?.ExpiresAtUnix ?? 0)), Now).Options!;

        /// <summary>直接插一条挂牌（用于构造「已到期」的行与机器人/别人的行）。`depositGold` = 0 模拟历史行。</summary>
        public long SeedListing(long? sellerId, string sellerName, int templateId, long expiresAtUnix, int depositGold = 0)
        {
            var core = new ItemCore(templateId, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 };
            Repository.InsertListings([new(sellerId, sellerName, templateId, (byte)ItemKind.Equipment, 1,
                AuctionPackets.ItemEntrySnapshot(core), 12_000, 17_000, expiresAtUnix - 3600, expiresAtUnix)
            {
                DepositGold = depositGold
            }]);
            return Query("SELECT max(listing_id) FROM auction_listings");
        }

        public long LastListingId() => Query("SELECT max(listing_id) FROM auction_listings");

        public int ListingState(long listingId) => (int)Query($"SELECT state FROM auction_listings WHERE listing_id={listingId}");

        public int ListingDeposit(long listingId) => (int)Query($"SELECT deposit_gold FROM auction_listings WHERE listing_id={listingId}");

        public long Gold() => GoldOf(Character.Id);

        /// <summary>按角色 id 读库里的金币（链路组的夹具没有自己的角色，只能按 id 读）。</summary>
        public long GoldOf(long characterId) => Query("SELECT gold FROM characters WHERE id=" + characterId);

        public int MailCount() => (int)Query("SELECT count(*) FROM mailbox_messages");

        public long MailGold() => Query("SELECT coalesce(max(gold),0) FROM mailbox_messages");

        public string? MailKey() => Text("SELECT delivery_key FROM mailbox_messages ORDER BY mail_id LIMIT 1");

        public int AttachmentCount() => (int)Query("SELECT count(*) FROM mailbox_attachments");

        public ImmutableArray<(long Before, long After)> CurrencyAudits(string action)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT balance_before,balance_after FROM currency_audit_log WHERE action_name=$action ORDER BY audit_id";
            command.Parameters.AddWithValue("$action", action);
            var rows = ImmutableArray.CreateBuilder<(long, long)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
            return rows.ToImmutable();
        }

        static byte[] CancelBody(int listingId, long expiresAtUnix)
        {
            var body = new byte[AuctionCancelRequest.BodySize];
            BitConverter.GetBytes(listingId).CopyTo(body, 1);
            BitConverter.GetBytes((int)expiresAtUnix).CopyTo(body, 5);
            return body;
        }

        long Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        string? Text(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() as string;
        }

        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = System.IO.Path.Combine(root, "game.db"),
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            db.Open();
            return db;
        }

        public void Dispose()
        {
            Owner.Dispose();
            if (Service is not null)
                TestFiles.DeleteDirectory(root, true);
        }
    }
}
