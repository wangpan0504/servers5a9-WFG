using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record MileagePurchaseLimit(int CommodityId, int Maximum, int Period, int Parameter1, int Parameter2, int Parameter3)
{
    public bool Supported => Period == 0 && Parameter1 == 0 && Parameter2 == 0 && Parameter3 == 0;
}

internal sealed class MileageShopCatalog
{
    public FrozenDictionary<int, CashShopProduct> Products { get; }
    public FrozenDictionary<int, MileagePurchaseLimit> Limits { get; }
    public FrozenSet<int> CeraOnly { get; }
    readonly FrozenDictionary<int, int> specific;
    readonly FrozenDictionary<string, int> rates;
    public int CashLimit { get; }
    public bool Available => Products.Count != 0;

    public MileageShopCatalog(MileageShopFile file)
    {
        Products = file.Items.ToFrozenDictionary(item => item.CommodityId,
            item => new CashShopProduct(item.CommodityId, item.ItemId, item.Count, item.CeraPrice, "item")
            { IsMileage = true, MileagePrice = item.MileagePrice, OtherPrice = item.OtherPrice });
        Limits = file.Limits.ToFrozenDictionary(row => row.CommodityId,
            row => new MileagePurchaseLimit(row.CommodityId, row.Maximum, row.Period, row.Parameter1, row.Parameter2, row.Parameter3));
        if (Limits.Keys.Any(id => !Products.ContainsKey(id))) throw new InvalidDataException("积分限购物品不在商城中");
        CeraOnly = file.CeraOnly.ToFrozenSet();
        specific = file.SpecificMileage.ToFrozenDictionary();
        rates = file.CategoryPercent.ToFrozenDictionary(row => Normalize(row.Key), row => row.Value, StringComparer.OrdinalIgnoreCase);
        CashLimit = file.CashMileageLimit ?? int.MaxValue;
    }

    public int Rebate(CashShopProduct product, int settledPrice)
    {
        if (product.IsMileage || settledPrice <= 0 || product.CeraPrice <= 0) return 0;
        if (specific.TryGetValue(product.CommodityId, out int amount))
            return amount;
        if (!rates.TryGetValue(Normalize(product.Section), out int percent)) return 0;
        // 039BBDE0先按float32乘分类率，再加0.5转整数；不使用默认的银行家舍入。
        float value = settledPrice * (percent * 0.01f) + 0.5f;
        if (!float.IsFinite(value) || value >= int.MaxValue) throw new OverflowException("商城返积分超出范围");
        return (int)value;
    }

    static string Normalize(string name) => name.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "visual" => "nametag",
        "selectablecharacterpremium" => "premium",
        "characpremiumpackage" => "package",
        var value => value
    };
}
