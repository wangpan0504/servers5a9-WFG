using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 租赁目录只解释 PVF 的价格、等级组和幸运星资源；物品模板仍由共享 ItemCatalog 负责。
internal sealed class RentalCatalog
{
    public const int LuckyStarItemId = 10_002_998;
    const string SystemPath = "etc/chnrentsystem/rentsysteminfo.etc";
    const string LuckyStarPath = "stackable/event/chn_rental_system/chn_stk_star_point_item.stk";
    static readonly string[] JobNames = ["swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter", "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer", "at priest", "gun blader"];
    readonly int[] minimumLevels;
    readonly IReadOnlyDictionary<(string Job, int Tier), IReadOnlyDictionary<int, int>> offers;

    internal RentalCatalog(int pointLimit, int goldPerPoint, int luckyStarStackLimit,
        int[] minimumLevels, IReadOnlyDictionary<(string Job, int Tier), IReadOnlyDictionary<int, int>> offers)
    {
        PointLimit = pointLimit;
        GoldPerPoint = goldPerPoint;
        LuckyStarStackLimit = luckyStarStackLimit;
        this.minimumLevels = minimumLevels;
        this.offers = offers;
    }

    public int PointLimit { get; }
    public int GoldPerPoint { get; }
    public int LuckyStarStackLimit { get; }

    public static RentalCatalog Load(PvfArchive pvf)
    {
        return Parse(pvf.ReadText);
    }

    internal static RentalCatalog Parse(Func<string, string> readText)
    {
        string system = readText(SystemPath);
        int limit = Scalar(system, "limit point");
        Match charge = Regex.Match(Section(system, "point charge"), @"`pdungeon`\s+1\s+`gold`\s+(?<gold>\d+)", RegexOptions.CultureInvariant);
        if (!charge.Success || !int.TryParse(charge.Groups["gold"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int gold) || gold <= 0)
            throw new InvalidDataException("租赁点兑换金币配置无效");
        int[] levels = ParseLevels(system);
        var parsedOffers = ParseOffers(system);
        int stackLimit = Scalar(readText(LuckyStarPath), "stack limit");
        string list = readText("stackable/stackable.lst");
        if (!Regex.IsMatch(list, $@"(?<!\d){LuckyStarItemId}\s+`event/chn_rental_system/chn_stk_star_point_item\.stk`", RegexOptions.CultureInvariant))
            throw new InvalidDataException("幸运星资源未映射到匹配 stackable.lst");
        return new(limit, gold, stackLimit, levels, parsedOffers);
    }

    public int TierForLevel(int level)
    {
        int tier = -1;
        for (int i = 0; i < minimumLevels.Length; i++)
        {
            if (minimumLevels[i] > level) break;
            tier = i;
        }
        return tier < 0 ? throw new InvalidOperationException("角色等级低于租赁目录最低等级") : tier;
    }

    public int Cost(byte job, int tier, int itemId)
    {
        string tag = JobTag(job);
        return offers.TryGetValue((tag, tier), out var items) && items.TryGetValue(itemId, out int cost)
            ? cost : throw new InvalidOperationException("物品不在当前职业和等级租赁组");
    }

    public static string JobTag(byte job) => job < JobNames.Length ? "[" + JobNames[job] + "]" : throw new InvalidOperationException("未知职业");

    static int[] ParseLevels(string text)
    {
        int[] values = Integers(Section(text, "section"));
        if (values.Length == 0 || values.Length % 7 != 0)
            throw new InvalidDataException("租赁等级 section 不是完整行");
        var result = new int[values.Length / 7];
        for (int i = 0; i < result.Length; i++)
        {
            int offset = i * 7;
            if (values[offset] != i || values[offset + 1] < 0 || i > 0 && values[offset + 1] <= result[i - 1])
                throw new InvalidDataException("租赁等级不连续");
            result[i] = values[offset + 1];
        }
        return result;
    }

    static Dictionary<(string Job, int Tier), IReadOnlyDictionary<int, int>> ParseOffers(string text)
    {
        var result = new Dictionary<(string Job, int Tier), IReadOnlyDictionary<int, int>>();
        foreach (Match group in Regex.Matches(text, @"\[group\]\s*`(?<job>[^`]+)`\s*(?<tier>\d+)\s*\[package selection\](?<items>.*?)\[/package selection\]", RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            int[] values = Integers(group.Groups["items"].Value);
            if (values.Length == 0 || values.Length % 2 != 0)
                throw new InvalidDataException("租赁商品组不是 ID/消耗对");
            var items = new Dictionary<int, int>();
            for (int i = 0; i < values.Length; i += 2)
                if (!items.TryAdd(values[i], values[i + 1]) || values[i] <= 0 || values[i + 1] <= 0)
                    throw new InvalidDataException("租赁商品组含重复或无效值");
            var key = (NormalizeJob(group.Groups["job"].Value), int.Parse(group.Groups["tier"].Value, CultureInfo.InvariantCulture));
            if (!result.TryAdd(key, items)) throw new InvalidDataException("租赁商品组重复");
        }
        if (result.Count == 0) throw new InvalidDataException("租赁商品组为空");
        return result;
    }

    static int Scalar(string text, string tag)
    {
        Match match = Regex.Match(Section(text, tag), @"(?<!\d)(?<value>\d+)(?!\d)", RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) : throw new InvalidDataException("缺少租赁字段: " + tag);
    }

    static int[] Integers(string text) => Regex.Matches(text, @"(?<![\w-])-?\d+(?![\w-])").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
    static string NormalizeJob(string job) => job.Trim().StartsWith("[", StringComparison.Ordinal) ? job.Trim().ToLowerInvariant() : "[" + job.Trim().ToLowerInvariant() + "]";
    static string Section(string text, string tag)
    {
        string[] lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        string header = "[" + tag + "]", close = "[/" + tag + "]";
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Trim() == header)
            {
                var body = new List<string>();
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string line = lines[j].Trim();
                    if (line == close) return string.Join("\n", body);
                    if (line.StartsWith("[", StringComparison.Ordinal)) break;
                    body.Add(lines[j]);
                }
                return string.Join("\n", body);
            }
        throw new InvalidDataException("缺少租赁 section: " + tag);
    }
}
