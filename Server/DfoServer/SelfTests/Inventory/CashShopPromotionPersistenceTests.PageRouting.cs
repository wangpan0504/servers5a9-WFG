using System.Buffers.Binary;
using System.Net;
using System.Text;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Chat;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopPromotionPersistenceTests
{
    const string PageNonce = "0123456789abcdef0123456789abcdef";

    public static void RunPageRoutingCore()
    {
        int before = Count;
        var failures = new List<Exception>();
        void Check(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
        }
        Check("service真实32套disabled与模板名称", PageServiceDisabled);
        Check("service真实active状态", PageServiceReady);
        Check("service不可核保已知历史累计", PageServiceUnverified);
        Check("service缺名称保进度而不造名称", PageServiceMissingName);
        Check("service旧owner不能读新账本", PageServiceOwner);
        Check("机器namespace及nonce合同", PageNamespace);
        Check("真实Handler关闭能力仍吞保留机器namespace", () => PageRouting(false).GetAwaiter().GetResult());
        Check("真实Handler查询完整snapshot并保普通聊天", () => PageRouting(true).GetAwaiter().GetResult());
        Check("Runtime页面能力独立默认关闭", PageRuntimeDefault);
        if (failures.Count != 0) throw new AggregateException("Promotion page routing behavioral failures", failures);
        Console.WriteLine($"Promotion page service/routing: {Count - before} checks passed.");
    }

    static ItemTemplate PageNamedTemplate(int id) =>
        new(id, ItemKind.Consumable, 0, 1, 0, 1, 0, 0, 0, "[free]", ushort.MaxValue, 0, "夹具真实模板名称-" + id);

    static void PageServiceDisabled()
    {
        using var f = PageFixture();
        var committed = f.Store.Commit(f.Owner, f.Plan(32), f.Now);
        f.Character = committed.Character;
        f.Catalog = f.MakeCatalog(false);
        string before = f.Sql.Snapshot();
        var snapshot = new CashShopPromotionPageService(f.Store, PageNamedTemplate).Read(f.Owner, f.Catalog, 6500);
        That(snapshot.Status == "disabled" && snapshot.Count == 32 && snapshot.ProgressRevision == 1 &&
            snapshot.CatalogRevision == f.Catalog.Revision && snapshot.AwardMask27 == 134217727 && snapshot.OverflowAwardCount == 2 &&
            snapshot.MaximumPurchases == 5000 && snapshot.TtlMs == 6500 && snapshot.Rewards.Length == 9 &&
            snapshot.Rewards.All(reward => reward.Name == PageNamedTemplate(reward.ItemId).Name && reward.Count == 1) &&
            snapshot.OverflowReward is { ItemId: 490705415, Count: 1 } overflow && overflow.Name == PageNamedTemplate(overflow.ItemId).Name &&
            f.Sql.Snapshot() == before, "service从已核真实账本和ItemTemplate.Name装配完整disabled快照，零写入");
    }

    static void PageServiceReady()
    {
        using var f = PageFixture();
        f.Character = f.Store.Commit(f.Owner, f.Plan(2), f.Now).Character;
        var snapshot = new CashShopPromotionPageService(f.Store, PageNamedTemplate).Read(f.Owner, f.Catalog, 10000);
        That(snapshot.Status == "ready" && snapshot.Count == 2 && snapshot.ProgressRevision == 1 && snapshot.AwardMask27 == 1 &&
            snapshot.OverflowAwardCount == 0 && snapshot.Rewards.Length == 9, "active且账本完整时仅展示已核事实");
    }

    static void PageServiceUnverified()
    {
        using var f = PageFixture();
        f.Character = f.Store.Commit(f.Owner, f.Plan(2), f.Now).Character;
        f.Sql.Execute("UPDATE cash_shop_promotion_progress SET count=5001");
        int names = 0;
        var snapshot = new CashShopPromotionPageService(f.Store, id => { names++; return PageNamedTemplate(id); })
            .Read(f.Owner, f.Catalog, 9000);
        That(snapshot.Status == "unavailable" && snapshot.Count == 5001 && snapshot.ProgressRevision == 1 &&
            snapshot.AwardMask27 is null && snapshot.OverflowAwardCount is null && snapshot.Rewards.IsDefaultOrEmpty &&
            snapshot.OverflowReward is null && names == 0, "缺账只保原5001/revision，不钳制、不猜名称或阈值奖项");
    }

    static void PageServiceMissingName()
    {
        foreach (bool missing in new[] { true, false })
        {
            using var f = PageFixture();
            f.Character = f.Store.Commit(f.Owner, f.Plan(2), f.Now).Character;
            var snapshot = new CashShopPromotionPageService(f.Store, id => missing
                ? throw new KeyNotFoundException("隔离夹具缺失名称") : PageNamedTemplate(id) with { Name = "\uFEFF坏名称" })
                .Read(f.Owner, f.Catalog, 8000);
            That(snapshot.Status == "unavailable" && snapshot.Count == 2 && snapshot.ProgressRevision == 1 &&
                snapshot.AwardMask27 is null && snapshot.OverflowAwardCount is null && snapshot.Rewards.IsDefaultOrEmpty &&
                snapshot.OverflowReward is null, "模板缺失或名称不能满足既定wire合同，保已知进度但不输出半套奖励");
        }
    }

    static void PageServiceOwner()
    {
        using var f = PageFixture();
        var service = new CashShopPromotionPageService(f.Store, PageNamedTemplate);
        using var replacement = f.Sql.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => service.Read(f.Owner, f.Catalog, 10000), "service不吞旧owner失效成正常结果");
    }

    static void PageNamespace()
    {
        foreach (string text in new[] { "#cp-promo/1", "#cp-promo/1 bad", "#cp-promo/1\t" + PageNonce, "#cp-promo/1  " + PageNonce })
            That(PromotionPageProjection.MachineNamespace(new(3, 0, 0, 0, text)) &&
                PromotionPageProjection.MachineNonce(new(3, 0, 0, 0, text)) is null, "保留namespace坏语法只吞不广播");
        That(PromotionPageProjection.MachineNonce(new(3, 0, 0, 0, "#cp-promo/1 " + PageNonce)) == PageNonce,
            "仅精确普通频道44字符机器请求取得nonce");
        foreach (string text in new[] { "#cp-promo/1x", " #cp-promo/1 " + PageNonce, "#CP-promo/1 " + PageNonce, "#cp-score/1 " + PageNonce, "#战力" })
            That(!PromotionPageProjection.MachineNamespace(new(3, 0, 0, 0, text)), "相似前缀/原评分不被页面吞掉");
        That(!PromotionPageProjection.MachineNamespace(new(6, 0, 0, 0, "#cp-promo/1 " + PageNonce)) &&
            !PromotionPageProjection.MachineNamespace(new(3, 0, 0, 0, "#cp-promo/1 " + PageNonce) { TargetName = "Receiver" }),
            "其它频道或带目标不作为页面查询");
    }

    static TownCatalog PageTowns(Fixture f)
    {
        var point = f.Character.Position;
        var map = new TownMap("page-fixture.map", [new(new(0, 0, 1000, 1000), [], [])], [], false, []);
        return new(new() { [(point.Town, point.Area)] = new(point.Town, point.Area, 1, [], [], [], point, map, [], []) },
            point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
    }

    static async Task PageRouting(bool enabled)
    {
        using var f = PageFixture();
        f.Character = f.Store.Commit(f.Owner, f.Plan(32), f.Now).Character;
        f.Catalog = f.MakeCatalog(false);
        var clock = new CharacterScoreNoticeTests.Clock();
        var session = new GameSession(f.Store, PageTowns(f), f.Owner, timeProvider: clock, promotionStore: f.Store);
        session.Select(0); session.MarkReady();
        try
        {
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var frames = new List<(ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0,
                (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), f.Owner, session, sender);
            var delivery = new PageDelivery();
            using var log = new ServerLog(Path.Combine(Path.GetDirectoryName(f.Sql.Path)!, "page-routing"));
            var page = enabled ? new PromotionPageProjection(() => f.Catalog, new(f.Store, PageNamedTemplate)) : null;
            var commands = new GameCommandRegistry();
            new ChatHandler(new ChatService(new PageChatStore(), delivery), null, log, promotionPage: page).Register(commands);
            commands.Seal();
            async Task Send(string text)
            {
                clock.Advance(1);
                await commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.SEND_MESSAGE, PageChatBody(text)));
                await sender.DrainAsync();
            }
            string before = f.Sql.Snapshot();
            await Send("#cp-promo/1 bad");
            That(frames.Count == 0 && delivery.Count == 0, "真实Handler坏机器请求在聊天限流后吞掉，关闭能力也不广播");
            await Send("#cp-promo/1 " + PageNonce);
            That(frames.Count == (enabled ? 1 : 0) && delivery.Count == 0 && f.Sql.Snapshot() == before,
                "真实Handler只向本人排队完整页面且零写入，关闭能力不输出");
            if (enabled)
            {
                var frame = frames.Single();
                int title = BinaryPrimitives.ReadInt32LittleEndian(frame.Body);
                int length = BinaryPrimitives.ReadInt32LittleEndian(frame.Body.AsSpan(4 + title));
                string body = Encoding.ASCII.GetString(frame.Body, 8 + title, length);
                string[] fields = body.Split('|');
                That(frame.Opcode == 1709 && title == 10 && Encoding.ASCII.GetString(frame.Body, 4, title) == "CP_PROMO/1" &&
                    fields.Length == 19 && fields[0] == "1" && fields[1] == "snapshot" && fields[2] == PageNonce &&
                    fields[3] == session.Selected!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                    fields[11] == "disabled" && fields[12] == "32" && fields[14] == "134217727" && fields[15] == "2" &&
                    fields[17].Split(';').Length == 9, "独立解析真实Handler输出19段，disabled不抹累计或奖励");
                await Send("#cp-promo/1 " + PageNonce);
                That(frames.Count == 1 && delivery.Count == 0, "同连接5秒内机器重复查询无额外响应或广播");
            }
            await Send("#cp-promo/1x");
            That(delivery.Count == 1, "相似普通文本仍走原广播");
        }
        finally { session.End(); }
    }

    static byte[] PageChatBody(string text)
    {
        byte[] data = ChatText.Encoding.GetBytes(text);
        byte[] body = new byte[11 + data.Length];
        body[0] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), (uint)data.Length);
        data.CopyTo(body, 11);
        return body;
    }

    static void PageRuntimeDefault()
    {
        var parameter = typeof(ServerRuntime).GetMethod(nameof(ServerRuntime.Create))!.GetParameters()
            .Single(candidate => candidate.Name == "enablePromotionPage");
        That(parameter.ParameterType == typeof(bool) && parameter.HasDefaultValue && parameter.DefaultValue is false,
            "Runtime页面能力显式独立参数且默认false，不借活动Enabled或Score能力启用");
    }

    sealed class PageChatStore : IChatDirectoryStore
    {
        public string? AccountOfCharacter(ushort id) => null;
        public string[] GuildMemberAccounts(long id) => [];
    }
    sealed class PageDelivery : IChatDelivery
    {
        public int Count;
        public int DeliverAll(Func<ChatRecipient, bool> filter, ChatNotice notice) { Count++; return 0; }
        public bool DeliverTo(string account, Func<ChatRecipient, bool> filter, ChatNotice notice) { Count++; return false; }
    }
}
