using System.Text.Json;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class PromotionPageSessionTests
{
    const string NonceA = "0123456789abcdef0123456789abcdef";
    const string NonceB = "abcdef0123456789abcdef0123456789";
    static PromotionScope Scope => CashShopPromotionTests.PilotRules().Scope;

    static CashShopPromotionCatalog Catalog(bool enabled = false)
    {
        var rules = CashShopPromotionTests.PilotRules();
        return CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, enabled) }),
            CashShopCatalog.Parse("[item]\n" + string.Concat(rules.Products.Select(product =>
                $"{product.CommodityId} {product.ItemId} 1 0 0 100 0 0 0\n"))),
            id => new(id, ItemKind.Consumable, 0, 1, 0, 1, 0, 0, 0, "[free]", ushort.MaxValue, 0, "合成页面外盒"), _ => null);
    }

    static PromotionPageCapture Begin(GameSession session, CashShopPromotionCatalog catalog, string nonce = NonceA)
    {
        var begun = session.TryBeginPromotionPage(catalog, Scope, nonce);
        That(begun.Capture is not null && begun.Status == "ready", "真实就绪Session接受本人页面查询并安装新view");
        return begun.Capture!;
    }

    public static void RunCore()
    {
        int before = Count;
        var failures = new List<Exception>();
        void Check(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
        }
        Check("query替换/5秒门/pending精确释放", Lifecycle);
        Check("服务器固定scope拒绝其它身份", FixedScope);
        Check("snapshot10秒截止而view持续", Lifetime);
        Check("真实资产revision变化不撤view", AssetRevision);
        Check("真实catalog对象替换撤view", CatalogChange);
        Check("选角/换角/End身份撤销", SelectionChanges);
        Check("真实owner接管拒绝旧view", OwnerChange);
        Check("最低revision仅当前scope/catalog", RevisionFloor);
        foreach (string change in new[] { "none", "nonce", "catalog", "same-role", "other-role", "end", "ttl", "floor", "owner" })
            Check("真实sender snapshot写前guard:" + change, () => Queued(change, false).GetAwaiter().GetResult());
        Check("真实sender长期changed经过10秒及正常资产变更仍可发送", () => Queued("none", true).GetAwaiter().GetResult());
        Check("真实sender旧changed在后续commit后拒绝", () => Queued("floor", true).GetAwaiter().GetResult());
        if (failures.Count != 0) throw new AggregateException("Promotion page session behavioral failures", failures);
        Console.WriteLine($"Promotion page session: {Count - before} checks passed.");
    }

    static void Lifecycle()
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        var catalog = Catalog();
        var first = Begin(f.Session, catalog);
        That(first.Account == f.Owner.Account && first.Generation == f.Owner.Generation &&
            first.SelectionEpoch == f.Session.SelectionEpoch && first.CharacterId == f.Session.Selected!.Id &&
            ReferenceEquals(first.Catalog, catalog) && first.Scope == Scope && first.Nonce == NonceA,
            "捕获真实账号generation/选角epoch/id/catalog实例/scope/nonce");
        That(f.Session.PromotionPageRemainingMilliseconds(first) == 10000, "初始snapshot剩余10秒");
        f.Clock.Advance(4);
        var throttled = f.Session.TryBeginPromotionPage(catalog, Scope, NonceB);
        That(throttled.Capture is null && throttled.Status == "throttled" &&
            f.Session.PromotionPageSnapshotStatus(first, catalog, 0) == "ready", "不足5秒拒绝不撤已合法view");
        f.Clock.Advance(1);
        var second = Begin(f.Session, catalog, NonceB);
        That(f.Session.PromotionPageSnapshotStatus(first, catalog, 0) != "ready" &&
            f.Session.PromotionPageSnapshotStatus(second, catalog, 0) == "ready", "5秒等号新query替换未结束旧pending及view");
        That(!f.Session.EndPromotionPage(first) && f.Session.EndPromotionPage(second) && !f.Session.EndPromotionPage(second),
            "旧finally不能清新pending，当前End精确命中且仅释放一次");
        That(f.Session.PromotionPageSnapshotStatus(second, catalog, 0) == "ready", "finally结束读取不撤已入队snapshot");
    }

    static void FixedScope()
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        var catalog = Catalog();
        That(f.Session.TryBeginPromotionPage(catalog, Scope with { Group = "other" }, NonceA).Capture is null,
            "调用方不能把其它活动scope装成固定页面");
        Begin(f.Session, catalog);
    }

    static void Lifetime()
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        var catalog = Catalog();
        var capture = Begin(f.Session, catalog);
        f.Clock.Advance(9);
        That(f.Session.PromotionPageSnapshotStatus(capture, catalog, 0) == "ready" &&
            f.Session.PromotionPageRemainingMilliseconds(capture) == 1000, "9秒snapshot仍可发送且TTL剩1秒");
        f.Clock.RollBack(30);
        f.Clock.Advance(1);
        That(f.Session.PromotionPageSnapshotStatus(capture, catalog, 0) != "ready" &&
            f.Session.PromotionPageRemainingMilliseconds(capture) == 0, "墙钟回拨不能延长单调时钟10秒截止");
        That(f.Session.PromotionPageViewStatus(capture, catalog) == "ready", "snapshot到期不撤长期view");
        That(f.Session.CapturePromotionPageChanged(catalog, new(Scope, 2, 1)) is null, "没有观察实际commit不能凭输入revision发changed");
        f.Session.ObservePromotionPageCommit(catalog, new(Scope, 2, 1));
        That(ReferenceEquals(f.Session.CapturePromotionPageChanged(catalog, new(Scope, 2, 1)), capture) &&
            f.Session.PromotionPageChangedStatus(capture, catalog, 1) == "ready", "10秒后同view观察成功commit仍可changed");
    }

    static void AssetRevision()
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        var catalog = Catalog();
        var capture = Begin(f.Session, catalog);
        var previous = f.Session.Selected!;
        f.Session.CommitDevelopmentInventory("NoticeHero", false, character => new(true, 0,
            new(character.Inventory, character.Inventory with { Gold = character.Inventory.Gold + 1 }, "page-revision-test")), "rejected");
        That(!ReferenceEquals(previous, f.Session.Selected) && previous.Revision < f.Session.Selected!.Revision &&
            f.Session.PromotionPageSnapshotStatus(capture, catalog, 0) == "ready" &&
            f.Session.PromotionPageViewStatus(capture, catalog) == "ready", "真实库存提交替换Selected/revision不使同角色页面失效");
    }

    static void CatalogChange()
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        var catalog = Catalog();
        var capture = Begin(f.Session, catalog);
        var reloaded = Catalog();
        That(reloaded.Revision != catalog.Revision &&
            f.Session.PromotionPageViewStatus(capture, reloaded) != "ready" &&
            f.Session.PromotionPageSnapshotStatus(capture, reloaded, 0) != "ready", "内容相同的新目录实例/revision也撤旧结果");
        f.Session.ObservePromotionPageCommit(reloaded, new(Scope, 2, 1));
        That(f.Session.CapturePromotionPageChanged(reloaded, new(Scope, 2, 1)) is null, "新目录不能复用旧view发changed");
    }

    static void SelectionChanges()
    {
        foreach (string change in new[] { "same-role", "other-role", "end" })
        {
            using var f = new CharacterScoreNoticeTests.Fixture();
            var catalog = Catalog();
            var capture = Begin(f.Session, catalog);
            ChangeSelection(f, change);
            That(f.Session.PromotionPageViewStatus(capture, catalog) != "ready" &&
                f.Session.PromotionPageSnapshotStatus(capture, catalog, 0) != "ready", "真实生命周期撤销旧view：" + change);
        }
        using var notReady = new CharacterScoreNoticeTests.Fixture();
        notReady.Session.ReturnToRoster();
        That(notReady.Session.TryBeginPromotionPage(Catalog(), Scope, NonceA).Capture is null, "未就绪不能创建view");
    }

    static void OwnerChange()
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        var catalog = Catalog();
        var capture = Begin(f.Session, catalog);
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        That(f.Session.PromotionPageViewStatus(capture, catalog) != "ready" &&
            f.Session.PromotionPageSnapshotStatus(capture, catalog, 0) != "ready" &&
            f.Session.TryBeginPromotionPage(catalog, Scope, NonceB).Capture is null,
            "真实目录接管后旧generation既不能发旧结果也不能读取新结果");
    }

    static void RevisionFloor()
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        var catalog = Catalog();
        var capture = Begin(f.Session, catalog);
        f.Session.ObservePromotionPageCommit(catalog, new(Scope with { Group = "other" }, 2, 1));
        That(f.Session.PromotionPageSnapshotStatus(capture, catalog, 0) == "ready", "其它促销scope不推进本页面floor");
        f.Session.ObservePromotionPageCommit(catalog, new(Scope, 2, 1));
        That(f.Session.PromotionPageSnapshotStatus(capture, catalog, 0) != "ready" &&
            f.Session.PromotionPageSnapshotStatus(capture, catalog, null) != "ready" &&
            f.Session.PromotionPageSnapshotStatus(capture, catalog, 1) == "ready", "成功commit后旧/未知snapshot revision不能盖新事实");
        f.Session.ObservePromotionPageCommit(catalog, new(Scope, 1, 0));
        That(f.Session.PromotionPageSnapshotStatus(capture, catalog, 0) != "ready", "迟到低revision不能回退floor");
        f.Session.ObservePromotionPageCommit(catalog, new(Scope, 3, 2));
        That(f.Session.PromotionPageChangedStatus(capture, catalog, 1) != "ready" &&
            f.Session.PromotionPageChangedStatus(capture, catalog, 2) == "ready", "后续成功commit使旧changed失效");
    }

    static void ChangeSelection(CharacterScoreNoticeTests.Fixture f, string change)
    {
        if (change == "end") { f.Session.End(); return; }
        f.Session.ReturnToRoster();
        f.Session.Select(change == "same-role" ? (ushort)0 : (ushort)1);
        f.Session.MarkReady();
    }

    static byte[] EmptySnapshot(PromotionPageCapture capture) => MachinePromotionPagePackets.Snapshot(capture.Nonce,
        capture.CharacterId, new(Scope.Campaign, Scope.Period, Scope.RulesVersion, Scope.Realm, Scope.Group,
            capture.Catalog.Revision, 0, "unavailable", 0, 5000, null, null, 10000, [], null));

    static async Task Queued(string change, bool changed)
    {
        using var f = new CharacterScoreNoticeTests.Fixture();
        var catalog = Catalog();
        var current = catalog;
        var capture = Begin(f.Session, catalog);
        if (changed)
        {
            f.Clock.Advance(12);
            f.Session.CommitDevelopmentInventory("NoticeHero", false, character => new(true, 0,
                new(character.Inventory, character.Inventory with { Gold = character.Inventory.Gold + 1 }, "page-changed-test")), "rejected");
            f.Session.ObservePromotionPageCommit(catalog, new(Scope, 2, 1));
        }
        using var stop = new CancellationTokenSource();
        using var stream = new GameSenderBudgetTests.HeldStream();
        await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
        Task held = sender.Send(0, 2, [1], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool accepted = sender.TryPost(0, 1709, changed
            ? MachinePromotionPagePackets.Changed(capture.Nonce, capture.CharacterId, catalog.Revision, 1) : EmptySnapshot(capture),
            "promotion-page-test", () => !stop.IsCancellationRequested && (changed
                ? f.Session.PromotionPageChangedStatus(capture, current, 1)
                : f.Session.PromotionPageSnapshotStatus(capture, current, 0)) == "ready");
        IDisposable? replacement = null;
        try
        {
            That(accepted && stream.Frames.Count == 0, "真实发送泵已排队但尚未写出页面");
            f.Session.EndPromotionPage(capture);
            switch (change)
            {
                case "nonce": f.Clock.Advance(5); Begin(f.Session, catalog, NonceB); break;
                case "catalog": current = Catalog(); break;
                case "same-role": case "other-role": case "end": ChangeSelection(f, change); break;
                case "ttl": f.Clock.Advance(10); break;
                case "floor": f.Session.ObservePromotionPageCommit(catalog, new(Scope, changed ? 3 : 2, changed ? 2 : 1)); break;
                case "owner": replacement = f.Owners.Acquire(f.Owner.Account, () => { }); break;
            }
            stream.Release.TrySetResult();
            if (change == "owner")
            {
                bool stale = false;
                try { await held.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidOperationException error) when (error.Message == "Stale account generation") { stale = true; }
                await sender.DisposeAsync();
                That(stale && stream.Frames.Count == 1 && sender.PendingBytes == 0 && stop.IsCancellationRequested,
                    "首包写出后原owner复核精确拒绝，发送泵终止取消连接，零页面帧且预算归零");
            }
            else
            {
                await held.WaitAsync(TimeSpan.FromSeconds(5));
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                That(stream.Frames.Count == (change == "none" ? 2 : 1) && sender.PendingBytes == 0,
                    "真实sender写前guard只发送当前合法页面并释放预算：" + change + "/changed=" + changed);
            }
        }
        finally { stream.Release.TrySetResult(); replacement?.Dispose(); }
    }
}
