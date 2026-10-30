using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Chat;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class PromotionPageProjection(Func<CashShopPromotionCatalog> current, CashShopPromotionPageService service)
{
    const string MachineCommand = "#cp-promo/1";

    internal static bool MachineNamespace(ChatSend send) => send.Type == (byte)ChatRequestType.Normal && send.TargetName is null &&
        send.Text.StartsWith(MachineCommand, StringComparison.Ordinal) &&
        (send.Text.Length == MachineCommand.Length || char.IsWhiteSpace(send.Text[MachineCommand.Length]));

    internal static string? MachineNonce(ChatSend send) => MachineNamespace(send) &&
        send.Text.Length == MachineCommand.Length + 33 && send.Text[MachineCommand.Length] == ' ' &&
        send.Text[(MachineCommand.Length + 1)..].All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? send.Text[(MachineCommand.Length + 1)..] : null;

    internal string Query(GameConnectionContext context, string nonce)
    {
        var catalog = current();
        var session = context.Session;
        var (capture, status) = session.TryBeginPromotionPage(catalog, CashShopPromotionPageService.Scope, nonce);
        if (capture is null) return status;
        try
        {
            status = session.PromotionPageViewStatus(capture, current());
            if (status != "ready") return status;
            int ttl = session.PromotionPageRemainingMilliseconds(capture);
            if (ttl == 0) return "expired";
            var snapshot = service.Read(context.Owner, catalog, ttl);
            ttl = session.PromotionPageRemainingMilliseconds(capture);
            if (ttl == 0) return "expired";
            snapshot = snapshot with { TtlMs = ttl };
            status = session.PromotionPageSnapshotStatus(capture, current(), snapshot.ProgressRevision);
            if (status != "ready") return status;
            byte[] body = MachinePromotionPagePackets.Snapshot(capture.Nonce, capture.CharacterId, snapshot);
            // current仅为Runtime已装配目录的内存引用；写前守卫不查SQL或资源。
            bool queued = context.Sender.TryPost(0, (ushort)NotiPacketType.AAS_TIPS, body, "cash_shop_promotion_page_snapshot",
                () => !context.Stop.IsCancellationRequested &&
                    session.PromotionPageSnapshotStatus(capture, current(), snapshot.ProgressRevision) == "ready");
            return queued ? "queued" : "queue_rejected";
        }
        finally { session.EndPromotionPage(capture); }
    }

    internal string Changed(GameConnectionContext context, PromotionProgress progress)
    {
        var catalog = current();
        var session = context.Session;
        var capture = session.CapturePromotionPageChanged(catalog, progress);
        if (capture is null) return "no_view";
        string status = session.PromotionPageChangedStatus(capture, current(), progress.Revision);
        if (status != "ready") return status;
        byte[] body = MachinePromotionPagePackets.Changed(capture.Nonce, capture.CharacterId, catalog.Revision, progress.Revision);
        bool queued = context.Sender.TryPost(0, (ushort)NotiPacketType.AAS_TIPS, body, "cash_shop_promotion_page_changed",
            () => !context.Stop.IsCancellationRequested &&
                session.PromotionPageChangedStatus(capture, current(), progress.Revision) == "ready");
        return queued ? "queued" : "queue_rejected";
    }
}
