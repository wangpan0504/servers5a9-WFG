using System.Collections.Frozen;
using System.Globalization;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// PVF 商城商品号与实际物品模板号分离；网络请求只携带商品号，费用和奖励必须从同一份资源目录解析。
internal sealed record CashShopProduct(int CommodityId, int ItemId, int Count, int CeraPrice, string Section,
    int DurationDays = 0, int ContractType = 0, int AvatarType = 0);
internal sealed record CashShopBonus(int ItemId, int Count);

internal sealed class CashShopCatalog
{
    static readonly (string Section, int Width, int PriceOffset)[] Sections =
    [
        ("item", 9, 5), ("item etc", 9, 5), ("premium", 9, 5), ("creature", 9, 5), ("coin", 9, 5),
        ("material", 9, 5), ("recoveryitem", 9, 5), ("item event", 9, 5),
        ("item event second category", 9, 5), ("community item", 9, 5), ("communityitem", 9, 5),
        ("regular item", 9, 5), ("regularitem", 9, 5),
        ("item mod or ext", 9, 5), ("privilege service", 9, 5),
        ("selectable character premium", 9, 5), ("charac premium package", 9, 2),
        ("package", 11, 4), ("regular package", 11, 4), ("community package", 11, 4),
        ("avatar dye", 9, 5), ("visual", 8, 5), ("avatar", 6, 5)
    ];
    static readonly Regex TokenPattern = new("`[^`]*`|-?\\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FrozenDictionary<int, CashShopProduct> Products { get; }
    public FrozenDictionary<int, ImmutableArray<CashShopBonus>> Bonuses { get; }

    CashShopCatalog(Dictionary<int, CashShopProduct> products,
        Dictionary<int, ImmutableArray<CashShopBonus>>? bonuses = null)
    {
        Products = products.ToFrozenDictionary();
        Bonuses = (bonuses ?? []).ToFrozenDictionary();
    }

    public CashShopProduct? Product(int commodityId) => Products.GetValueOrDefault(commodityId);
    public IReadOnlyList<CashShopBonus> Bonus(int commodityId) => Bonuses.GetValueOrDefault(commodityId, []);

    public static CashShopCatalog Load(PvfArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        string path = archive.Contains("etc/(r)cerashop.etc") ? "etc/(r)cerashop.etc" :
            archive.Contains("etc/cerashop.etc") ? "etc/cerashop.etc" : "etc/ori_newcashshop.etc";
        string? bonusText = archive.Contains("etc/chn_cerashop_multibonus.etc")
            ? archive.ReadText("etc/chn_cerashop_multibonus.etc") : null;
        return Parse(archive.ReadText(path), bonusText);
    }

    internal static CashShopCatalog Parse(string text, string? bonusText = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var products = new Dictionary<int, CashShopProduct>();
        foreach (var (section, width, priceOffset) in Sections)
        {
            string body = PvfScript.Section(text, section);
            if (body.Length == 0)
                continue;
            var values = TokenPattern.Matches(body).Select(match => match.Value).ToArray();
            if (values.Length % width != 0)
                throw new InvalidDataException($"商城 section={section} 行宽不完整: {values.Length}/{width}");
            for (int offset = 0; offset < values.Length; offset += width)
            {
                if (!Number(values[offset], out int commodity) || commodity <= 0)
                    continue; // selectable/default sentinel rows are not purchasable products.
                if (!Number(values[offset + 1], out int item) || item <= 0)
                    continue;
                int count = Number(values[offset + 2], out int parsedCount) && parsedCount > 0 ? parsedCount : 1;
                if (section is "selectable character premium" or "charac premium package") count = 1;
                if (!Number(values[offset + priceOffset], out int price) || price < (section == "avatar" ? -1 : 0))
                {
                    throw new InvalidDataException($"商城商品价格无效: section={section}, commodity={commodity}");
                }
                int avatarType = section == "avatar" && Number(values[offset + 2], out int selector) ? selector : 0;
                if (section == "avatar" && avatarType is < 1 or > 255)
                    throw new InvalidDataException("商城装扮类型索引无效: " + commodity);
                int durationDays = section == "visual"
                    ? Number(values[offset + 3], out int parsedDays) && parsedDays > 0 ? parsedDays : 30
                    : section == "charac premium package" && Number(values[offset + 7], out parsedDays) && parsedDays > 0 ? parsedDays : 0;
                int contractType = section == "selectable character premium" && Number(values[offset + 2], out int slot) && slot is >= 0 and < 8
                    ? checked(580 + slot) : 0;
                if (section == "selectable character premium" && Number(values[offset + 3], out int selectableDays) && selectableDays > 0)
                    durationDays = selectableDays;
                products[commodity] = new(commodity, item, section == "avatar" ? 1 : count, price, section, durationDays, contractType, avatarType);
            }
        }
        if (products.Count == 0)
            throw new InvalidDataException("商城商品目录为空");
        return new(products, ParseBonuses(bonusText));

        static bool Number(string value, out int number) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    static Dictionary<int, ImmutableArray<CashShopBonus>> ParseBonuses(string? text)
    {
        var result = new Dictionary<int, ImmutableArray<CashShopBonus>>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match block in Regex.Matches(text, @"\[bonus item\](.*?)\[/bonus item\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int[] values = PvfScript.Integers(block.Groups[1].Value);
            if (values.Length < 3 || values.Length % 2 == 0)
                throw new InvalidDataException("商城 one-plus-one 奖励行宽无效");
            int commodity = values[0];
            if (commodity <= 0 || result.ContainsKey(commodity))
                throw new InvalidDataException("商城 one-plus-one 商品号重复或无效: " + commodity);
            var bonuses = ImmutableArray.CreateBuilder<CashShopBonus>((values.Length - 1) / 2);
            for (int i = 1; i < values.Length; i += 2)
            {
                if (values[i] <= 0 || values[i + 1] <= 0)
                    throw new InvalidDataException("商城 one-plus-one 奖励物品或数量无效");
                bonuses.Add(new(values[i], values[i + 1]));
            }
            result.Add(commodity, bonuses.ToImmutable());
        }
        return result;
    }
}
