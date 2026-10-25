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
internal sealed class NpcShopCatalog
{
    readonly FrozenSet<int> saleRestrictions;
    public FrozenDictionary<int, NpcShop> Shops { get; }
    public int EquipmentSellRate { get; }

    public NpcShopCatalog(PvfArchive pvf)
    {
        const string restrictionsPath = "etc/chn_undeletable_item.etc";
        saleRestrictions = ParseSaleRestrictions(pvf.Contains(restrictionsPath) ? pvf.ReadText(restrictionsPath) : null);
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

    public bool Allows(int npcId, int shopId, int itemId) => Shops.TryGetValue(shopId, out var shop) &&
        shop.Items.Contains(itemId) &&
        (shop.Raid || shop.NpcId == npcId);

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
}

internal sealed record ItemPurchaseLimit(bool AccountWide, int Count, bool ResetDaily);
internal sealed record ItemShopPrice(int Gold, int SellGold, ImmutableArray<ItemMaterialCost> Materials, ItemPurchaseLimit? Limit)
{
    int? EquipmentValue { get; init; }
    int EquipmentSellRate { get; init; }

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
        var materials = Enumerable.Range(0, material.Length / 2).Select(index => new ItemMaterialCost(material[index * 2], material[index * 2 + 1])).ToImmutableArray();
        var result = new ItemShopPrice((int)gold, sell, materials, limit)
        {
            EquipmentValue = equipment ? value >= 0 ? value : (int)gold : null,
            EquipmentSellRate = equipmentSellRate
        };
        return equipment ? result with { SellGold = result.GetSellGold(0, 0) } : result;
    }
}
