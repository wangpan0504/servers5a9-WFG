using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record NpcShop(int NpcId, FrozenSet<int> Items)
{
    // 攻坚商店(.shp)不写[NPC]段，按文件类型识别；购买时不再校验商人，见 Allows 的说明。
    public bool Raid { get; init; }
}
// 交易窗口的商品：BuyGold 是[item list]基准价×倍率后的金币买价，SellGold 是收购价；只列材料兑换的商品为 0/null。
internal sealed record NpcTradingProduct(int BuyGold, int? SellGold);
internal sealed class NpcShopCatalog
{
    readonly FrozenSet<int> saleRestrictions;
    public FrozenDictionary<int, NpcShop> Shops { get; }
    public FrozenDictionary<int, FrozenDictionary<int, NpcTradingProduct>> Trading { get; }
    public int EquipmentSellRate { get; }

    public NpcShopCatalog(PvfArchive pvf)
    {
        const string restrictionsPath = "etc/chn_undeletable_item.etc";
        saleRestrictions = ParseSaleRestrictions(pvf.Contains(restrictionsPath) ? pvf.ReadText(restrictionsPath) : null);
        // 交易商人不在 itemshop/*.shp 内，名册只存在于 etc/tradingsystem.etc；文件缺失按无交易商人处理。
        const string tradingPath = "etc/tradingsystem.etc";
        Trading = ParseTrading(pvf.Contains(tradingPath) ? pvf.ReadText(tradingPath) : null);
        EquipmentSellRate = Integers(Section(pvf.ReadText("equipment/pricetable.tbl"), "rate")).First();
        var shops = new Dictionary<int, NpcShop>();
        foreach (var (id, path) in List(pvf.ReadText("itemshop/itemshop.lst")))
        {
            string resolved = "itemshop/" + path;
            if (!pvf.Contains(resolved))
                resolved = new[] { "itemshop/(r)" + path, "itemshop/(f)" + path }.FirstOrDefault(pvf.Contains)
                    ?? throw new InvalidDataException("PVF 商店文件缺失: " + path);
            string text = pvf.ReadText(resolved);
            int npc = Integers(Section(text, "NPC")).FirstOrDefault(-1);
            // 攻坚商店没有[NPC]声明，靠 [type] `[etc shop]` 与商人的[raid shop]角色位识别。
            bool raid = npc < 0 && Regex.IsMatch(Section(text, "type"), @"`\[etc shop\]`", RegexOptions.IgnoreCase);
            if (npc < 0 && !raid)
                continue;
            // 职业分页只是商品展示；递归收集各页，不能据此禁止跨职业购买。
            string sell = string.Join("\n", Regex.Matches(text, @"\[sell info\](.*?)\[/sell info\]", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value));
            var items = Regex.Matches(sell, @"\[item list\](.*?)\[/item list\]", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .SelectMany(match => Integers(match.Groups[1].Value)).Where(item => item > 0)
                .Concat(Integers(Section(text, "one a day item")).Where(item => item > 0)).ToFrozenSet();
            shops.Add(id, new(npc, items) { Raid = raid });
        }
        if (shops.Count == 0 || EquipmentSellRate < 0)
            throw new InvalidDataException("PVF 商店或售价表无效");
        Shops = shops.ToFrozenDictionary();
    }

    // 交易窗口客户端固定发 ShopId=-1，此时只核对交易名册内的商人-商品对；普通商店仍校验 .shp。
    public bool Allows(int npcId, int shopId, int itemId) => shopId < 0
        ? Trading.TryGetValue(npcId, out var trading) && trading.ContainsKey(itemId)
        : Shops.TryGetValue(shopId, out var shop) && shop.Items.Contains(itemId) && (shop.Raid || shop.NpcId == npcId);

    // 交易名册覆盖买价，普通商店沿用模板价。名册只列材料兑换的商品没有金币买价，
    // 模板也没有[need material]时无价可付，必须拒绝，不能退化成免费购买。
    public ItemShopPrice? PurchasePrice(int npcId, int shopId, int itemId, ItemShopPrice template)
    {
        if (shopId >= 0) return template;
        if (!Trading.TryGetValue(npcId, out var trading) || !trading.TryGetValue(itemId, out var product)) return null;
        if (product.BuyGold > 0) return template with { Gold = product.BuyGold };
        return template.Materials.Length != 0 ? template : null;
    }

    // 只有[item list]明确标价的商品才有名册收购价；只列兑换/特殊交易的商品沿用模板价。
    public int? TradingSellGold(int npcId, int itemId) =>
        Trading.TryGetValue(npcId, out var trading) && trading.TryGetValue(itemId, out var product) ? product.SellGold : null;

    public bool IsTradingNpc(int npcId) => Trading.ContainsKey(npcId);

    // 某个商人在全部商店页上的在售物品并集；职业分页只是展示，库存按商人整体计算。
    public FrozenSet<int> Stock(int npcId) => Shops.Values.Where(shop => shop.NpcId == npcId)
        .SelectMany(shop => shop.Items).ToFrozenSet();

    // 攻坚商店不校验商人：实机同一家店的不同入口 NPC 声明并不统一——
    // 卢克的艾丽卡(460)有 [raid shop]，超时空漩涡的假面骑士(619)只有 [move town]，
    // 而客户端照样能开出该店。入口由客户端 UI 决定，服务端只核对商品是否属于该店。

    public bool CanSell(int itemId) => itemId > 0 && !saleRestrictions.Contains(itemId);

    internal static FrozenSet<int> ParseSaleRestrictions(string? text)
    {
        // 该名册由原生出售资格读取；缺失或损坏不能静默退化成允许全部出售。
        var block = Regex.Match(text ?? "", @"\A\s*\[undeletable item\](.*?)\[/undeletable item\]\s*\z",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!block.Success)
            throw new InvalidDataException("PVF 售卖限制名册缺失或块结构无效");
        var items = new HashSet<int>();
        foreach (string token in block.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id) || id <= 0)
                throw new InvalidDataException("PVF 售卖限制名册包含无效物品编号");
            items.Add(id);
        }
        return items.ToFrozenSet();
    }

    internal static FrozenDictionary<int, FrozenDictionary<int, NpcTradingProduct>> ParseTrading(string? text)
    {
        // 缺失文件表示本版没有交易商人；块结构或价格非法时装配失败，不能静默放行。
        var trading = new Dictionary<int, Dictionary<int, NpcTradingProduct>>();
        if (string.IsNullOrWhiteSpace(text))
            return FrozenDictionary<int, FrozenDictionary<int, NpcTradingProduct>>.Empty;
        foreach (Match block in Regex.Matches(text, @"\[trade item list\](.*?)\[/trade item list\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string body = block.Groups[1].Value;
            int[] who = Integers(Regex.Match(body, @"\A[^\[]*").Value);
            if (who.Length != 1 || who[0] <= 0)
                throw new InvalidDataException("PVF 交易商人缺少有效商人编号");
            if (!trading.TryGetValue(who[0], out var products))
                trading.Add(who[0], products = new Dictionary<int, NpcTradingProduct>());
            foreach (Match row in Regex.Matches(Section(body, "item list"), @"[^\r\n]+"))
            {
                string[] parts = row.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 ||
                    !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int itemId) || itemId <= 0 ||
                    !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int baseGold) || baseGold < 0 ||
                    !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double multiplier) || !(multiplier > 0))
                    throw new InvalidDataException("PVF 交易商品价格无效");
                // 买价按远离零取整：6666×1.25=8332.5 应记 8333，与客户端显示的整数价一致。
                int buy = (int)Math.Clamp(Math.Round(baseGold * multiplier, MidpointRounding.AwayFromZero), 0, int.MaxValue);
                products[itemId] = new(buy, baseGold);
            }
            // [shop item] 只列可用材料兑换的商品，没有独立金币买价。
            foreach (int itemId in Integers(Section(body, "shop item")).Where(id => id > 0))
                products.TryAdd(itemId, new(0, null));
        }
        foreach (Match block in Regex.Matches(text, @"\[particular trade item list\](.*?)\[/particular trade item list\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            int[] values = Integers(block.Groups[1].Value);
            if (values.Length < 2 || values[0] <= 0 || (values.Length - 2) % 2 != 0)
                throw new InvalidDataException("PVF 特殊交易商品定义无效");
            if (!trading.TryGetValue(values[0], out var products))
                trading.Add(values[0], products = new Dictionary<int, NpcTradingProduct>());
            // 行首数字含义未确认，只按 (物品, 标记) 对取成员资格；标记不是 -1 时拒绝，避免把未知语义当成可兑换。
            for (int index = 2; index < values.Length; index += 2)
            {
                if (values[index] <= 0 || values[index + 1] != -1)
                    throw new InvalidDataException("PVF 特殊交易商品定义无效");
                products.TryAdd(values[index], new(0, null));
            }
        }
        var result = new Dictionary<int, FrozenDictionary<int, NpcTradingProduct>>(trading.Count);
        foreach (var (npc, products) in trading)
            result.Add(npc, products.ToFrozenDictionary());
        return result.ToFrozenDictionary();
    }
}

internal enum ItemPurchasePeriod { Legacy, Daily, Weekly, Monthly, Accumulate }
internal sealed record ItemPurchaseLimit(bool AccountWide, int Count, bool ResetDaily)
{
    public ItemPurchasePeriod Period { get; init; }
}
// [sale limit] 是物品级收购上限：账号内各角色共享，每日 UTC+8 06:00 重置。
internal sealed record ItemSaleLimit(bool AccountWide, int Count);
internal sealed record ItemShopPrice(int Gold, int SellGold, ImmutableArray<ItemMaterialCost> Materials, ItemPurchaseLimit? Limit)
{
    int? EquipmentValue { get; init; }
    int EquipmentSellRate { get; init; }
    public ItemSaleLimit? SaleLimit { get; init; }
    public ItemPurchaseLimit? PeriodicLimit { get; init; }
    public ImmutableArray<string> UnresolvedPurchaseLimits { get; init; } = [];

    public int GetSellGold(ushort durability, ushort maximumDurability)
    {
        if (EquipmentValue is not { } value)
            return SellGold;
        // Client 040C13A0：单精度折算耐久后截断，再以整数取90%；不能先把满耐久价截断。
        float amount = maximumDurability == 0 ? EquipmentSellRate * 0.001f * value :
            (float)value * EquipmentSellRate * 0.001f * Math.Min(durability, maximumDurability) / maximumDurability;
        return (int)Math.Clamp((long)amount * 90 / 100, 0, int.MaxValue);
    }

    public static ItemShopPrice Parse(PvfDocument text, bool equipment, int equipmentSellRate)
    {
        int Read(string tag, int fallback = 0) => Integers(text.Section(tag)).FirstOrDefault(fallback);
        int price = Read("price", -1), value = Read("value", -1);
        int[] material = Integers(text.Section("need material"));
        if (material.Length % 2 != 0 || material.Any(value => value <= 0))
            throw new InvalidDataException("NPC 兑换材料定义无效");
        // 材料兑换只按 price + add price 收金币，value 是卖价依据，不能当作兑换费用。
        long gold = material.Length != 0 ? price < 0 ? 0 : (long)price + Read("add price") : price >= 0 ? price : value;
        gold = Math.Clamp(gold, 0, int.MaxValue);
        int sell = equipment ? 0 : Math.Max(0, value >= 0 ? value / 5 : price / 5);
        string limited = text.Section("daily purchase limit");
        ItemPurchaseLimit? limit = null;
        if (limited.Length != 0)
        {
            string scope = Regex.Match(limited, "`([^`]+)`").Groups[1].Value.ToLowerInvariant();
            int count = Integers(limited).Single();
            // trade：可交易商品的购买上限，按账号共享。chraracter 是 character 的笔误。
            bool account = scope is "account" or "trade";
            bool character = scope is "character" or "charac" or "chraracter";
            if ((!account && !character) || count <= 0)
                throw new InvalidDataException("NPC 限购定义无效");
            limit = new(account, count, text.Has("reset daily purchase item"));
        }
        ItemPurchaseLimit? periodicLimit = null;
        var unresolvedLimits = ImmutableArray.CreateBuilder<string>();
        foreach (string body in text.Sections("purchase limit"))
        {
            // 已见空声明和角色公会战赛季声明保留原文；规则未闭合，购买端必须拒绝。
            if (string.IsNullOrWhiteSpace(body))
            {
                unresolvedLimits.Add(body);
                continue;
            }
            var season = Regex.Match(body, @"\A\s*`character`\s+`agitwarseason`\s+([0-9]+)\s*\z", RegexOptions.IgnoreCase);
            if (season.Success && int.TryParse(season.Groups[1].Value, out int seasonalMaximum) && seasonalMaximum > 0)
            {
                unresolvedLimits.Add(body);
                continue;
            }
            var declaration = Regex.Match(body, @"\A\s*`(account|character)`\s+`(daily|weekly|monthly|accumulate)`\s+([0-9]+)\s*\z", RegexOptions.IgnoreCase);
            if (!declaration.Success || !int.TryParse(declaration.Groups[3].Value, out int maximum) || maximum <= 0)
                throw new InvalidDataException("NPC 商品周期限购声明无效");
            // 原生0413DB8D..0413DD84读取purchase limit并写STK+E30/+E2C；前段+2D4属于sale limit。
            periodicLimit = new(declaration.Groups[1].Value.Equals("account", StringComparison.OrdinalIgnoreCase), maximum, false)
            {
                Period = declaration.Groups[2].Value.ToLowerInvariant() switch
                {
                    "daily" => ItemPurchasePeriod.Daily,
                    "weekly" => ItemPurchasePeriod.Weekly,
                    "monthly" => ItemPurchasePeriod.Monthly,
                    "accumulate" => ItemPurchasePeriod.Accumulate,
                    _ => throw new InvalidDataException("NPC 商品限购周期未知")
                }
            };
        }
        string saleLimited = text.Section("sale limit");
        ItemSaleLimit? saleLimit = null;
        if (saleLimited.Length != 0)
        {
            string[] scope = Regex.Matches(saleLimited, "`([^`]+)`").Select(match => match.Groups[1].Value.ToLowerInvariant()).ToArray();
            int count = Integers(saleLimited).Single();
            // 本版资源只出现过 `account` `daily`；未确认的范围或周期不能放宽成更宽的收购规则。
            if (scope.Length != 2 || scope[0] != "account" || scope[1] != "daily" || count <= 0)
                throw new InvalidDataException("NPC 收购上限定义无效");
            saleLimit = new(true, count);
        }
        var materials = Enumerable.Range(0, material.Length / 2).Select(index => new ItemMaterialCost(material[index * 2], material[index * 2 + 1])).ToImmutableArray();
        var result = new ItemShopPrice((int)gold, sell, materials, limit)
        {
            EquipmentValue = equipment ? value >= 0 ? value : (int)gold : null,
            EquipmentSellRate = equipmentSellRate,
            SaleLimit = saleLimit,
            PeriodicLimit = periodicLimit,
            UnresolvedPurchaseLimits = unresolvedLimits.ToImmutable()
        };
        return equipment ? result with { SellGold = result.GetSellGold(0, 0) } : result;
    }
}
