using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal sealed record BossSelectionBuff(int ItemId, int Remaining, long UntilMilliseconds,
    BossSelectionBuffDefinition Definition)
{
    public bool Active(long now) => Remaining > 0 && UntilMilliseconds > now;
    public bool Allows(uint dungeonId, bool party, bool practice, long now) =>
        Active(now) && dungeonId == Definition.DungeonId && !party && !practice;
    public static bool Same(BossSelectionBuff? left, BossSelectionBuff? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null &&
        left.ItemId == right.ItemId && left.Remaining == right.Remaining && left.UntilMilliseconds == right.UntilMilliseconds &&
        left.Definition.BuffId == right.Definition.BuffId && left.Definition.DungeonId == right.Definition.DungeonId &&
        left.Definition.Entries == right.Definition.Entries && left.Definition.Weights.SequenceEqual(right.Definition.Weights);

    public void Validate()
    {
        if (ItemId <= 0 || UntilMilliseconds <= 0 || Remaining is < 1 or > 5 || Definition is not { BuffId: 300, DungeonId: 7191, Entries: 5 } ||
            !Definition.Weights.SequenceEqual(new BossSelectionWeight[] { new(92689, 40), new(92690, 40), new(92697, 20) }))
            throw new InvalidDataException("白斯追击药水状态无效");
    }
}
