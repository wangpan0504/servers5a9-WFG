using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal enum EpicPieceShopAction { Select, Refresh, Complete }

// 仅属于本次正式副本；数量资产仍在账号图鉴，不把商人的临时清单写成第二套钱包。
internal sealed record EpicPieceShopState(bool Available)
{
    public int SelectedEquipment { get; init; }
    public int Refreshes { get; init; }
    public bool Completed { get; init; }
    public ImmutableDictionary<int, ImmutableArray<int>> Offers { get; init; } = ImmutableDictionary<int, ImmutableArray<int>>.Empty;
    public ImmutableArray<int> Materials => Offers.GetValueOrDefault(SelectedEquipment, []);
}

internal sealed record EpicPieceShopPlan(EpicPieceShopState After, InventoryResult Inventory);
