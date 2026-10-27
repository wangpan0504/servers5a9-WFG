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
        if (Definition is null) throw new InvalidDataException("白斯追击药水定义缺失");
        bool ordinary = Definition.BuffId is 300 or 1282 &&
            Definition.Weights.SequenceEqual(new BossSelectionWeight[] { new(92689, 40), new(92690, 40), new(92697, 20) });
        bool strong = Definition.BuffId == 1275 &&
            Definition.Weights.SequenceEqual(new BossSelectionWeight[] { new(92689, 0), new(92690, 0), new(92697, 100) });
        if (ItemId <= 0 || UntilMilliseconds <= 0 || Remaining < 1 || Remaining > Definition.Entries ||
            Definition.DungeonId != 7191 || Definition.Entries != (Definition.BuffId == 1282 ? 1 : 5) || !ordinary && !strong)
            throw new InvalidDataException("白斯追击药水状态无效");
    }
}
