using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record PrivilegeServiceReward(int Days, int ItemId, int Count);

// 礼遇服务拥有独立季和奖励表，不借用premiumlist中的账号契约类型。
internal sealed class PrivilegeServiceCatalog
{
    public const string Path = "etc/chn_privilegeservice.etc";
    public int CurrentSeason { get; }
    public int TeleportCooldownSeconds { get; }
    public FrozenDictionary<int, CashShopBonus> DailyRewards { get; }
    public FrozenDictionary<int, ImmutableArray<PrivilegeServiceReward>> AccumulatedRewards { get; }
    public FrozenSet<int> PurchaseItems { get; }

    PrivilegeServiceCatalog(string text, IEnumerable<CashShopProduct> products)
    {
        CurrentSeason = Number(text, "now season");
        TeleportCooldownSeconds = Number(text, "teleport cooltime");
        if (CurrentSeason < 0 || TeleportCooldownSeconds <= 0)
            throw new InvalidDataException("礼遇服务季或传送冷却声明无效");
        int[] daily = PvfScript.Integers(PvfScript.Section(text, "daily login reward"));
        if (daily.Length == 0 || daily.Length % 3 != 0)
            throw new InvalidDataException("礼遇每日奖励声明不完整");
        var dailyRewards = new Dictionary<int, CashShopBonus>();
        for (int index = 0; index < daily.Length; index += 3)
            if (daily[index] < 0 || daily[index + 1] <= 0 || daily[index + 2] <= 0 ||
                !dailyRewards.TryAdd(daily[index], new(daily[index + 1], daily[index + 2])))
                throw new InvalidDataException("礼遇每日奖励季重复或奖励无效");
        DailyRewards = dailyRewards.ToFrozenDictionary();
        var accumulated = new Dictionary<int, ImmutableArray<PrivilegeServiceReward>>();
        foreach (Match block in Regex.Matches(text, @"\[accumulate login reward\](.*?)\[/accumulate login reward\]",
            RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            int season = Number(block.Groups[1].Value, "season");
            int[] values = PvfScript.Integers(PvfScript.Section(block.Groups[1].Value, "reward"));
            if (season < 0 || values.Length == 0 || values.Length % 3 != 0 || values.Length / 3 > 32)
                throw new InvalidDataException("礼遇累计奖励声明不完整");
            var rewards = ImmutableArray.CreateBuilder<PrivilegeServiceReward>();
            for (int index = 0; index < values.Length; index += 3)
            {
                if (values[index] <= 0 || values[index + 1] <= 0 || values[index + 2] <= 0 ||
                    index > 0 && values[index] <= values[index - 3])
                    throw new InvalidDataException("礼遇累计奖励天数或物品无效");
                rewards.Add(new(values[index], values[index + 1], values[index + 2]));
            }
            if (!accumulated.TryAdd(season, rewards.ToImmutable()))
                throw new InvalidDataException("礼遇累计奖励季重复");
        }
        AccumulatedRewards = accumulated.ToFrozenDictionary();
        if (!DailyRewards.ContainsKey(CurrentSeason) || !AccumulatedRewards.ContainsKey(CurrentSeason) ||
            !DailyRewards.Keys.Order().SequenceEqual(AccumulatedRewards.Keys.Order()))
            throw new InvalidDataException("礼遇每日与累计奖励季不一致");
        var purchaseProducts = products.Where(product => product.Section == "privilege service").ToArray();
        if (purchaseProducts.Length == 0 || purchaseProducts.Any(product => product.Count != 1 || product.DurationDays <= 0))
            throw new InvalidDataException("礼遇购买商品或期限无效");
        PurchaseItems = purchaseProducts.Select(product => product.ItemId).ToFrozenSet();
    }

    public static PrivilegeServiceCatalog Parse(string text, IEnumerable<CashShopProduct> products) => new(text, products);

    public static PrivilegeServiceCatalog Load(PvfArchive archive, CashShopCatalog shop, ItemCatalog items)
    {
        var result = Parse(archive.ReadText(Path), shop.Products.Values);
        foreach (int id in result.DailyRewards.Values.Select(row => row.ItemId)
            .Concat(result.AccumulatedRewards.Values.SelectMany(rows => rows.Select(row => row.ItemId)))
            .Concat(result.PurchaseItems).Distinct())
            if (!items.TryGet(id, out var item) || item.StackLimit < 1)
                throw new InvalidDataException("礼遇物品资源缺失或不可发放：" + id);
        return result;
    }

    static int Number(string text, string tag)
    {
        int[] values = PvfScript.Integers(PvfScript.Section(text, tag));
        return values.Length == 1 ? values[0] : throw new InvalidDataException("礼遇单值声明无效：" + tag);
    }
}
