using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record BossSelectionProjectionReceipt(int RemovedBuffId, int BuffId, int Remaining, Func<bool> IsCurrent);

internal sealed partial class GameSession
{
    long bossProjectionEpoch = -1;
    long bossProjectionVersion;
    BossSelectionBuff? projectedBossBuff;

    // 仅保存客户端投影身份；药效和次数仍以已提交库存为唯一来源。
    public BossSelectionProjectionReceipt? PlanBossSelectionProjection(bool force = false) => owner.Execute(() =>
    {
        if (Selected is not { } character) return null;
        long epoch = SelectionEpoch;
        if (bossProjectionEpoch != epoch)
        {
            bossProjectionEpoch = epoch;
            projectedBossBuff = null;
        }
        var active = character.Inventory.BossSelectionBuff is { } buff &&
            buff.Active((timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds()) ? buff : null;
        var previous = projectedBossBuff;
        if ((!force || active is null) && BossSelectionBuff.Same(previous, active)) return null;
        int removed = previous is not null && previous.Definition.BuffId != active?.Definition.BuffId
            ? previous.Definition.BuffId : 0;
        projectedBossBuff = active;
        long version = ++bossProjectionVersion;
        bool Current() => owner.Execute(() => owner.IsCurrent && SelectionEpoch == epoch && Selected?.Id == character.Id &&
            bossProjectionVersion == version && BossSelectionBuff.Same(Selected?.Inventory.BossSelectionBuff, character.Inventory.BossSelectionBuff) &&
            (active is null || active.Active((timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds())));
        return new BossSelectionProjectionReceipt(removed, active?.Definition.BuffId ?? 0, active?.Remaining ?? 0, Current);
    });
}
