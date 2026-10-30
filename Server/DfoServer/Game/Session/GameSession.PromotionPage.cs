using DfoServer.Game.CashShopPromotions;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record PromotionPageCapture(string Account, long Generation, long SelectionEpoch,
    ushort CharacterId, CashShopPromotionCatalog Catalog, PromotionScope Scope, string Nonce, long Timestamp);

internal sealed partial class GameSession
{
    static readonly PromotionScope PageScope = new("spring-2020", "replica-1", "v1", "default", "package");
    PromotionPageCapture? promotionPageLatest;
    PromotionPageCapture? promotionPagePending;
    long? promotionPageLastStarted;
    long promotionPageMinimumRevision;

    internal (PromotionPageCapture? Capture, string Status) TryBeginPromotionPage(
        CashShopPromotionCatalog catalog, PromotionScope scope, string nonce)
    {
        if (!owner.IsCurrent) return (null, "stale_generation");
        try
        {
            return owner.Execute<(PromotionPageCapture?, string)>(() =>
            {
                if (!Ready || Selected is not { } character) return (null, "not_ready");
                if (scope != PageScope || !catalog.Activities.Any(entry => entry.Configuration.Rules.Scope == scope))
                    return (null, "invalid_scope");
                if (nonce is not { Length: 32 } || !nonce.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    return (null, "invalid_request");
                var clock = timeProvider ?? TimeProvider.System;
                long stamp = clock.GetTimestamp();
                if (promotionPageLastStarted is { } previous &&
                    clock.GetElapsedTime(previous, stamp) < TimeSpan.FromSeconds(5))
                    return (null, "throttled");
                var capture = new PromotionPageCapture(owner.Account, owner.Generation, SelectionEpoch,
                    character.Id, catalog, scope, nonce, stamp);
                promotionPageLastStarted = stamp;
                promotionPageMinimumRevision = 0;
                Volatile.Write(ref promotionPageLatest, capture);
                // 新合法查询替换尚未结束的读取；旧finally只能清除自身pending。
                Volatile.Write(ref promotionPagePending, capture);
                return (capture, "ready");
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return (null, "stale_generation"); }
    }

    internal bool EndPromotionPage(PromotionPageCapture capture) =>
        ReferenceEquals(Interlocked.CompareExchange(ref promotionPagePending, null, capture), capture);

    internal string PromotionPageSnapshotStatus(PromotionPageCapture capture,
        CashShopPromotionCatalog current, long? progressRevision)
    {
        if (!owner.IsCurrent) return "stale_generation";
        try
        {
            return owner.Execute(() =>
            {
                string status = PromotionPageViewStatusCore(capture, current);
                if (status != "ready") return status;
                if (progressRevision is < 0 || promotionPageMinimumRevision > 0 &&
                    (progressRevision is null || progressRevision < promotionPageMinimumRevision))
                    return "progress_changed";
                return PromotionPageRemainingMilliseconds(capture) == 0 ? "expired" : "ready";
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return "stale_generation"; }
    }

    internal int PromotionPageRemainingMilliseconds(PromotionPageCapture capture)
    {
        double elapsed = (timeProvider ?? TimeProvider.System).GetElapsedTime(capture.Timestamp).TotalMilliseconds;
        if (elapsed < 0 || elapsed >= 10000) return 0;
        return (int)Math.Floor(10000 - elapsed);
    }

    internal string PromotionPageViewStatus(PromotionPageCapture capture, CashShopPromotionCatalog current)
    {
        if (!owner.IsCurrent) return "stale_generation";
        try { return owner.Execute(() => PromotionPageViewStatusCore(capture, current)); }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return "stale_generation"; }
    }

    // 成功购买仍持有owner锁时调用，先于任何ACK或刷新发布的等待。
    internal void ObservePromotionPageCommit(CashShopPromotionCatalog catalog, PromotionProgress progress)
    {
        if (!owner.IsCurrent) return;
        try
        {
            owner.Execute(() =>
            {
                if (promotionPageLatest is { } capture && PromotionPageViewStatusCore(capture, catalog) == "ready" &&
                    progress.Scope == capture.Scope && progress.Count >= 0 && progress.Revision > 0)
                    promotionPageMinimumRevision = Math.Max(promotionPageMinimumRevision, progress.Revision);
                return true;
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation") { }
    }

    internal PromotionPageCapture? CapturePromotionPageChanged(CashShopPromotionCatalog catalog, PromotionProgress progress)
    {
        if (!owner.IsCurrent) return null;
        try
        {
            return owner.Execute(() => promotionPageLatest is { } capture &&
                PromotionPageViewStatusCore(capture, catalog) == "ready" && progress.Scope == capture.Scope &&
                progress.Revision > 0 && progress.Revision == promotionPageMinimumRevision ? capture : null);
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return null; }
    }

    internal string PromotionPageChangedStatus(PromotionPageCapture capture,
        CashShopPromotionCatalog current, long progressRevision)
    {
        if (!owner.IsCurrent) return "stale_generation";
        try
        {
            return owner.Execute(() =>
            {
                string status = PromotionPageViewStatusCore(capture, current);
                if (status != "ready") return status;
                return progressRevision > 0 && progressRevision == promotionPageMinimumRevision ? "ready" : "progress_changed";
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return "stale_generation"; }
    }

    // 调用方均持有owner锁；不查SQL或资源、不等网络，也不以资产修订守卫页面。
    string PromotionPageViewStatusCore(PromotionPageCapture capture, CashShopPromotionCatalog current)
    {
        if (capture.Account != owner.Account || capture.Generation != owner.Generation) return "stale_generation";
        if (!Ready || Selected is not { } character) return "not_ready";
        if (capture.SelectionEpoch != SelectionEpoch || capture.CharacterId != character.Id) return "selection_changed";
        if (!ReferenceEquals(promotionPageLatest, capture)) return "view_changed";
        if (!ReferenceEquals(capture.Catalog, current)) return "catalog_changed";
        return "ready";
    }
}
