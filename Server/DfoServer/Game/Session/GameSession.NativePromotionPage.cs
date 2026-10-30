using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record NativePromotionPageCapture(string Account, long Generation, long SelectionEpoch,
    Character Character, bool Ready, SessionPhase Phase, CashShopPromotionCatalog Catalog, long CatalogRevision, long Timestamp);

internal sealed partial class GameSession
{
    internal NativePromotionPageCapture? CaptureNativePromotionPage(CashShopPromotionCatalog catalog)
    {
        if (!owner.IsCurrent) return null;
        try
        {
            return owner.Execute<NativePromotionPageCapture?>(() =>
            {
                // 入城108早于MarkReady；已选角色的Unselected初始化阶段同样合法。
                if (Selected is not { } character || !Ready && Phase != SessionPhase.Unselected) return null;
                return new(owner.Account, owner.Generation, SelectionEpoch, character, Ready, Phase,
                    catalog, catalog.Revision, (timeProvider ?? TimeProvider.System).GetTimestamp());
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return null; }
    }

    internal bool CanPublishNativePromotionPage(NativePromotionPageCapture capture,
        CashShopPromotionCatalog current, PromotionPageSnapshot snapshot, long? minimumProgressRevision = null)
    {
        if (!owner.IsCurrent) return false;
        try
        {
            return owner.Execute(() =>
            {
                if (capture.Account != owner.Account || capture.Generation != owner.Generation ||
                    capture.SelectionEpoch != SelectionEpoch || capture.Ready != Ready || capture.Phase != Phase ||
                    Selected is not { } character || character.Id != capture.Character.Id ||
                    character.Revision != capture.Character.Revision || !ReferenceEquals(character, capture.Character) ||
                    !ReferenceEquals(capture.Catalog, current) || capture.CatalogRevision != current.Revision ||
                    snapshot.CatalogRevision != capture.CatalogRevision) return false;
                var scope = CashShopPromotionPageService.Scope;
                if (snapshot.Campaign != scope.Campaign || snapshot.Period != scope.Period ||
                    snapshot.RulesVersion != scope.RulesVersion || snapshot.Realm != scope.Realm || snapshot.Group != scope.Group ||
                    snapshot.ProgressRevision is < 0 || minimumProgressRevision is < 0 ||
                    minimumProgressRevision is > 0 && (snapshot.ProgressRevision is null ||
                        snapshot.ProgressRevision < minimumProgressRevision)) return false;
                // 只守卫本次读取/排队，不为已展示的原生窗口另建在线状态。
                var elapsed = (timeProvider ?? TimeProvider.System).GetElapsedTime(capture.Timestamp);
                return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(10);
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        { return false; }
    }
}
