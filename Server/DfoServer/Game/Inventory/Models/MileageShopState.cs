using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record MileageShopState(int CashEarned, ImmutableSortedDictionary<int, int> Purchases)
{
    public static MileageShopState Empty { get; } = new(0, ImmutableSortedDictionary<int, int>.Empty);
    public bool Same(MileageShopState other) => CashEarned == other.CashEarned && Purchases.SequenceEqual(other.Purchases);
    public void Validate()
    {
        if (CashEarned < 0 || Purchases.Any(row => row.Key <= 0 || row.Value <= 0)) throw new InvalidDataException("积分商城账号状态无效");
    }
}
