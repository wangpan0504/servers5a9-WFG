using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record ItemMakingSkillState(ImmutableArray<ushort> Points)
{
    public const int Count = 11;
    public static ItemMakingSkillState Empty { get; } = new(Enumerable.Repeat((ushort)0, Count).ToImmutableArray());
    public void Validate()
    {
        if (Points.IsDefault || Points.Length != Count || Points.Any(value => value > 1000))
            throw new InvalidDataException("制作熟练度存档无效");
    }
    public bool Same(ItemMakingSkillState other) => Points.SequenceEqual(other.Points);
    public ItemMakingSkillState Set(byte type, int value)
    {
        if (type >= Count || value is < 0 or > 1000) throw new InvalidDataException("制作熟练度类别或点数无效");
        return new(Points.SetItem(type, (ushort)value));
    }
}
