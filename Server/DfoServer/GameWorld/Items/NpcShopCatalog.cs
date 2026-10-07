using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record NpcShop(int NpcId, FrozenSet<int> Items);
internal sealed class NpcShopCatalog
{
    public FrozenDictionary<int, NpcShop> Shops { get; }
    public int EquipmentSellRate { get; }

    public NpcShopCatalog(PvfArchive pvf)
    {
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
            if (npc < 0)
                continue;
            // 职业分页只是商品展示；递归收集各页，不能据此禁止跨职业购买。
            string sell = string.Join("\n", Regex.Matches(text, @"\[sell info\](.*?)\[/sell info\]", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value));
            var items = Regex.Matches(sell, @"\[item list\](.*?)\[/item list\]", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .SelectMany(match => Integers(match.Groups[1].Value)).Where(item => item > 0)
                .Concat(Integers(Section(text, "one a day item")).Where(item => item > 0)).ToFrozenSet();
            shops.Add(id, new(npc, items));
        }
        if (shops.Count == 0 || EquipmentSellRate < 0)
            throw new InvalidDataException("PVF 商店或售价表无效");
        Shops = shops.ToFrozenDictionary();
    }

    public bool Allows(int npcId, int shopId, int itemId) => Shops.TryGetValue(shopId, out var shop) &&
        shop.NpcId == npcId && shop.Items.Contains(itemId);
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
            string scope = Regex.Match(limited, "`([^`]+)`").Groups[1].Value;
            int count = Integers(limited).Single();
            if (scope is not ("account" or "character" or "charac") || count <= 0)
                throw new InvalidDataException("NPC 限购定义无效");
            limit = new(scope == "account", count, text.Has("reset daily purchase item"));
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
