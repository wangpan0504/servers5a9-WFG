using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed record RaidClearCountsReceipt(Character Character, long Epoch, ImmutableArray<RaidClearPeriod> Periods,
    ImmutableArray<RaidClearCount> Counts, long QuotaDisplayRevision = 0);

internal sealed partial class GameSession
{
    internal RaidClearCountsReceipt? CaptureRaidClearCounts(RaidWeeklyCycle? lukeCycle) => owner.Execute(() =>
    {
        // 入城投影早于MarkReady；只读次数绑定已选角色及epoch，不依赖后续操作就绪标记。
        if (Selected is not { } character) return null;
        var periods = RaidClearCounts.Periods((timeProvider ?? TimeProvider.System).GetUtcNow(), lukeCycle);
        return new RaidClearCountsReceipt(character, SelectionEpoch, periods, repository.ReadRaidClearCounts(owner, character.Id, periods), quotaDisplayRevision);
    });

    internal bool CanPublishRaidClearCounts(RaidClearCountsReceipt receipt, RaidWeeklyCycle? lukeCycle) => owner.Execute(() =>
        SelectionEpoch == receipt.Epoch && quotaDisplayRevision == receipt.QuotaDisplayRevision && Selected?.Id == receipt.Character.Id &&
        receipt.Periods.SequenceEqual(RaidClearCounts.Periods((timeProvider ?? TimeProvider.System).GetUtcNow(), lukeCycle)));
}
