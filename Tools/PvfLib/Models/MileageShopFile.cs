using System.Globalization;
using System.Text.RegularExpressions;

namespace PvfLib;

public sealed record MileageShopEntry(int CommodityId, int ItemId, int Count, int OtherPrice,
    int MileagePrice, int CeraPrice, int Category, int Group);
public sealed record MileageShopLimit(int CommodityId, int Maximum, int Period, int Parameter1, int Parameter2, int Parameter3);

// 商城声明保留原列；周期参数的业务解释不属于归档解析器。
public sealed class MileageShopFile
{
    static readonly Regex Tokens = new("`[^`]*`|[^\\s]+", RegexOptions.CultureInvariant);
    public IReadOnlyList<MileageShopEntry> Items { get; }
    public IReadOnlyList<MileageShopLimit> Limits { get; }
    public IReadOnlyDictionary<int, int> SpecificMileage { get; }
    public IReadOnlyDictionary<string, int> CategoryPercent { get; }
    public IReadOnlySet<int> CeraOnly { get; }
    public int? CashMileageLimit { get; }

    public MileageShopFile(string normalShop, string? mileageShop)
    {
        var normal = new PvfDocument(normalShop);
        var mileage = new PvfDocument(mileageShop ?? "");
        var items = new List<MileageShopEntry>();
        var ids = new HashSet<int>();
        var values = Read(mileage, "item", 9);
        for (int i = 0; i < values.Length; i += 9)
        {
            var item = new MileageShopEntry(Int(values[i]), Int(values[i + 1]), Int(values[i + 2]), Int(values[i + 3]),
                Int(values[i + 4]), Int(values[i + 5]), Int(values[i + 7]), Int(values[i + 8]));
            if (item.CommodityId <= 0 || item.ItemId <= 0 || item.Count <= 0 || item.OtherPrice < 0 || item.MileagePrice < 0 ||
                item.CeraPrice < 0 || !ids.Add(item.CommodityId)) throw new InvalidDataException("积分商城商品声明无效或重复");
            items.Add(item);
        }
        Items = items.AsReadOnly();
        var limits = new List<MileageShopLimit>();
        ids.Clear();
        values = Read(mileage, "purchasing limit", 6);
        for (int i = 0; i < values.Length; i += 6)
        {
            int[] row = values.AsSpan(i, 6).ToArray().Select(Int).ToArray();
            if (row[0] <= 0 || row[1] <= 0 || row.Skip(2).Any(v => v < 0) || !ids.Add(row[0]))
                throw new InvalidDataException("积分商城限购声明无效或重复");
            limits.Add(new(row[0], row[1], row[2], row[3], row[4], row[5]));
        }
        Limits = limits.AsReadOnly();
        var specific = new Dictionary<int, int>();
        values = Read(normal, "specific product mileage", 2);
        for (int i = 0; i < values.Length; i += 2)
        {
            int id = Int(values[i]), amount = Int(values[i + 1]);
            if (id <= 0 || amount < 0 || !specific.TryAdd(id, amount)) throw new InvalidDataException("指定商品积分声明无效或重复");
        }
        SpecificMileage = specific;
        var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        values = Read(normal, "mileage rate by category", 2);
        for (int i = 0; i < values.Length; i += 2)
        {
            string name = Text(values[i]);
            int percent = Int(values[i + 1]);
            if (name.Length == 0 || percent < 0 || !categories.TryAdd(name, percent)) throw new InvalidDataException("商城分类积分声明无效或重复");
        }
        CategoryPercent = categories;
        CeraOnly = Read(normal, "buy only cera", 1).Select(Int).ToHashSet();
        if (CeraOnly.Any(id => id <= 0)) throw new InvalidDataException("仅点券商品编号无效");
        values = Read(mileage, "limit mileage by cash", 1);
        if (values.Length > 1 || values.Length == 1 && Int(values[0]) < 0) throw new InvalidDataException("消费返积分上限无效");
        CashMileageLimit = values.Length == 0 ? null : Int(values[0]);
    }

    static string[] Read(PvfDocument document, string tag, int width)
    {
        if (!document.Has(tag)) return [];
        if (document.DeclarationCount(tag) != 1 || !document.Has("/" + tag)) throw new InvalidDataException("商城表声明不完整或重复: " + tag);
        var values = Tokens.Matches(document.Section(tag)).Select(m => m.Value).ToArray();
        if (values.Length % width != 0) throw new InvalidDataException("商城表行宽无效: " + tag);
        return values;
    }
    static int Int(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
        ? result : throw new InvalidDataException("商城表不是整数: " + value);
    static string Text(string value) => value.Length >= 2 && value[0] == '`' && value[^1] == '`'
        ? value[1..^1] : throw new InvalidDataException("商城表不是字符串");
}
