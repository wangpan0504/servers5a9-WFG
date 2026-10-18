using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AccountCargoTier(ushort Capacity, int GoldLimit, int MaterialId, int MaterialCount,
    int GoldCost, int ProductId, int CeraCost, int ToolItemId);

internal sealed class CargoCatalog
{
    public int RequiredLevel { get; }
    public IReadOnlyList<AccountCargoTier> AccountTiers { get; }

    public CargoCatalog(PvfArchive pvf)
    {
        string text = pvf.ReadText("etc/accountcargo.etc");
        RequiredLevel = PvfScript.Integers(PvfScript.Section(text, "required level")).Single();
        int[] values = PvfScript.Integers(PvfScript.Block(text, "upgrade info"));
        if (RequiredLevel is < 1 or > 95 || values.Length == 0 || values.Length % 6 != 0)
            throw new InvalidDataException("PVF 账号仓库升级表不完整");
        // 只读取仓库表引用的商城商品价格，商品号不是物品模板号。
        var prices = new Dictionary<int, int>();
        var tools = new Dictionary<int, int>();
        var productIds = Enumerable.Range(0, values.Length / 6).Select(i => values[i * 6 + 5]).Where(id => id > 0).ToHashSet();
        string shop = PvfScript.Block(pvf.ReadText("etc/(r)cerashop.etc"), "item etc");
        string[] tokens = Regex.Matches(shop, @"`[^`]*`|\S+").Select(m => m.Value).ToArray();
        if (tokens.Length % 9 != 0)
            throw new InvalidDataException("PVF 仓库商品段宽度");
        for (int i = 0; i < tokens.Length; i += 9)
        {
            int product = int.Parse(tokens[i]);
            if (!productIds.Contains(product))
                continue;
            if (int.Parse(tokens[i + 2]) != 1 || int.Parse(tokens[i + 3]) != 0 || int.Parse(tokens[i + 4]) != 0 ||
                !prices.TryAdd(product, int.Parse(tokens[i + 5])) || prices[product] <= 0)
                throw new InvalidDataException("PVF 仓库商品费用不支持");
            int itemId = int.Parse(tokens[i + 1]);
            if (itemId <= 0 || tools.ContainsValue(itemId))
                throw new InvalidDataException("PVF 仓库升级工具对应不唯一");
            tools.Add(product, itemId);
        }
        var tiers = new List<AccountCargoTier>();
        for (int i = 0; i < values.Length; i += 6)
        {
            int capacity = values[i], product = values[i + 5];
            if (capacity is < 1 or > 240 || tiers.Count > 0 && capacity <= tiers[^1].Capacity ||
                values[i + 1] < 0 || values[i + 3] < 0 || values[i + 4] < 0 ||
                (values[i + 2] > 0) != (values[i + 3] > 0) || product > 0 && (!prices.ContainsKey(product) || tiers.Any(tier => tier.ProductId == product)))
                throw new InvalidDataException("PVF 仓库档位或费用无效");
            tiers.Add(new((ushort)capacity, values[i + 1], values[i + 2], values[i + 3], values[i + 4], product,
                product > 0 ? prices[product] : 0, product > 0 ? tools[product] : 0));
        }
        AccountTiers = tiers.AsReadOnly();
    }

    public AccountCargoTier? Next(ushort capacity) => capacity == 0 ? AccountTiers[0] :
        AccountTiers.SkipWhile(tier => tier.Capacity != capacity).Skip(1).FirstOrDefault();
    public int GoldLimit(ushort capacity) => AccountTiers.SingleOrDefault(tier => tier.Capacity == capacity)?.GoldLimit ?? 0;
}
