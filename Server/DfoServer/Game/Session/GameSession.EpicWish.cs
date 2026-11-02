using DfoServer.Game.Characters;
using DfoServer.Game.EpicWish;

namespace DfoServer.Game.Session;

internal sealed record EpicWishReceipt(Character Character, long Generation, long SelectionEpoch, SessionPhase Phase);

internal sealed partial class GameSession
{
    public EpicWishReceipt? SaveEpicWish(EpicWishService service, EpicWishList wishes)
    {
        // 目录解析在账号租约外完成；保存只修改展示清单，不消耗材料或改变掉率。
        if (!service.Eligible(wishes)) return null;
        return owner.Execute<EpicWishReceipt?>(() =>
        {
            if (!Ready || Selected is not { } character || pendingRental is not null || HasPendingTownQuestCommit)
                return null;
            service.Save(owner, character, wishes);
            if (!character.EpicWishes.Same(wishes)) Selected = character with { EpicWishes = wishes };
            return new(Selected!, owner.Generation, SelectionEpoch, Phase);
        });
    }

    public bool CanPublishEpicWish(EpicWishReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        Phase == receipt.Phase && ReferenceEquals(Selected, receipt.Character));
}
