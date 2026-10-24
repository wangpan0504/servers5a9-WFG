using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Titles;

namespace DfoServer.Game.Dungeons;

// 奖励须先由已核对的规则冻结；此合同只负责把原实例、资产、称号和持久楼层绑定为一次提交。
internal sealed class PreparedDungeonDespairClear(DungeonClearFact fact, Character expected,
    DungeonSpecialEntry entry, int clearDay, uint playMilliseconds, InventoryPlan inventory, TitleAchievementPlan? title)
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DungeonClearFact Fact { get; } = fact;
    public Character Expected { get; } = expected;
    public DungeonSpecialEntry Entry { get; } = entry;
    public int ClearDay { get; } = clearDay;
    public uint PlayMilliseconds { get; } = playMilliseconds;
    public InventoryPlan Inventory { get; } = inventory;
    public TitleAchievementPlan? Title { get; } = title;
    public DungeonDespairRewardPlan? Reward { get; init; }
    public DungeonDespairProgress After { get; } = new(entry.Layer == 100 ? 1 : entry.Layer + 1, clearDay,
        checked(entry.DespairProgress!.ClearCount + 1));
}

internal sealed record DungeonDespairClearCommit(Character Character, bool Duplicate, DungeonDespairProgress Progress,
    InventoryPlan Inventory);

internal sealed partial class SinglePlayerDungeonRun
{
    PreparedDungeonDespairClear? pendingDespairClear, committedDespairClear;
    internal bool HasCommittedDespairClear => committedDespairClear is not null;

    public PreparedDungeonDespairClear? PrepareDespairClear(Character expected, DungeonSpecialEntry admission,
        int clearDay, uint playMilliseconds, InventoryPlan inventory, TitleAchievementPlan? title = null,
        DungeonDespairRewardPlan? reward = null)
    {
        if ((pendingDespairClear ?? committedDespairClear) is { } original)
            return ReferenceEquals(original.Expected, expected) && ReferenceEquals(original.Inventory, inventory) &&
                ReferenceEquals(original.Title, title) && original.Entry == admission && original.ClearDay == clearDay &&
                original.PlayMilliseconds == playMilliseconds && ReferenceEquals(original.Reward, reward) ? original : null;
        if (entry.DungeonId is < 11008 or > 11107 || expected.Id != identity.CharacterId ||
            admission is not { Kind: DungeonSpecialEntryKind.Despair, DespairProgress: { } progress } ||
            admission.Layer != progress.NextLayer || entry.DungeonId != 11007 + admission.Layer ||
            clearDay is < 1 or > 3652058 || clearDay < admission.Day || progress.LastClearDay == clearDay ||
            ClearFact is not { ConfirmedDeathEvents.Count: > 0 } fact || fact.Identity != identity ||
            Phase != SinglePlayerDungeonPhase.Settlement || HasPendingDeath || HasPendingSettlement ||
            inventory.Reason != "despair_clear_reward" || !ReferenceEquals(inventory.Before, expected.Inventory) ||
            title is not null && !ReferenceEquals(title.Before, expected)) return null;
        admission.Validate();
        progress.Validate();
        reward?.Validate();
        if (reward is not null && reward.Layer != admission.Layer) return null;
        return pendingDespairClear = new(fact, expected, admission, clearDay, playMilliseconds, inventory, title) { Reward = reward };
    }

    public bool IsPendingDespairClear(PreparedDungeonDespairClear prepared) =>
        ReferenceEquals(pendingDespairClear, prepared) && Phase == SinglePlayerDungeonPhase.Settlement &&
        ReferenceEquals(ClearFact, prepared.Fact) && prepared.Fact.Identity == identity;

    public bool ConfirmCommittedDespairClear(PreparedDungeonDespairClear prepared)
    {
        if (ReferenceEquals(committedDespairClear, prepared)) return true;
        if (!IsPendingDespairClear(prepared)) return false;
        committedDespairClear = prepared;
        pendingDespairClear = null;
        return true;
    }
}
