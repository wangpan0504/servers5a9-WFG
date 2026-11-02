using System.Collections.Immutable;

namespace DfoServer.Game.EpicWish;

internal sealed record EpicWishList(ImmutableArray<int> Items)
{
    public const int MaximumItems = 5;
    public static EpicWishList Empty { get; } = new([]);

    public void Validate()
    {
        if (Items.IsDefault || Items.Length > MaximumItems || Items.Any(id => id < 0) ||
            Items.Where(id => id != 0).Distinct().Count() != Items.Count(id => id != 0))
            throw new InvalidDataException("史诗祈愿清单无效");
    }

    public bool Same(EpicWishList other) => Items.AsSpan().SequenceEqual(other.Items.AsSpan());
}
