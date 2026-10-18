using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record EmblemCompoundRewardDefinition(int ItemId, long Weight, int Count, int Rarity,
    ImmutableHashSet<byte> PlatinumJobs);
internal sealed record EmblemCompoundPool(int BoosterItemId, ImmutableArray<EmblemCompoundRewardDefinition> Rewards);

internal sealed class EmblemCompoundCatalog
{
    public const string RootPath = "etc/emblemcompound.etc";
    static readonly string[] JobNames =
    [
        "swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer",
        "at priest", "gun blader"
    ];

    readonly FrozenDictionary<(int Grade, int Count), EmblemCompoundPool> pools;

    public EmblemCompoundCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
        : this(archive.ReadText, stackable) { }

    internal EmblemCompoundCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> stackable)
    {
        var mappings = ParseMappings(read(RootPath));
        var loaded = new Dictionary<int, EmblemCompoundPool>();
        var rewardMetadata = new Dictionary<int, (int Rarity, ImmutableHashSet<byte> PlatinumJobs)>();
        var result = new Dictionary<(int Grade, int Count), EmblemCompoundPool>();
        foreach (var (key, boosterItemId) in mappings)
        {
            if (!loaded.TryGetValue(boosterItemId, out var pool))
            {
                if (!stackable.TryGetValue(boosterItemId, out string? boosterPath))
                    throw new InvalidDataException($"徽章合成booster不在stackable列表: {boosterItemId}");
                var rewards = ParsePool(read("stackable/" + boosterPath), boosterItemId, read, stackable, rewardMetadata);
                loaded.Add(boosterItemId, pool = new(boosterItemId, rewards));
            }
            result.Add(key, pool);
        }
        pools = result.ToFrozenDictionary();
    }

    public EmblemCompoundPool For(int grade, int count) =>
        pools.TryGetValue((grade, count), out var pool)
            ? pool
            : throw new InvalidDataException($"徽章合成缺少grade={grade} count={count}映射");

    public IEnumerable<KeyValuePair<(int Grade, int Count), EmblemCompoundPool>> Entries => pools;

    internal static FrozenDictionary<(int Grade, int Count), int> ParseMappings(string text)
    {
        int[] values = Integers(Section(text, "emblem compound info"));
        var mappings = new Dictionary<(int Grade, int Count), int>();
        int at = 0;
        while (at < values.Length)
        {
            if (at + 2 > values.Length)
                throw new InvalidDataException("PVF徽章合成映射头截断");
            int grade = values[at++], maximum = values[at++];
            if (grade <= 0 || maximum is < 2 or > 5)
                throw new InvalidDataException($"PVF徽章合成映射头无效: grade={grade} count={maximum}");
            int rowCount = maximum - 1;
            if (at + rowCount > values.Length)
                throw new InvalidDataException($"PVF徽章合成grade={grade}映射截断");
            for (int count = 2; count <= maximum; count++)
            {
                int booster = values[at++];
                if (booster <= 0 || !mappings.TryAdd((grade, count), booster))
                    throw new InvalidDataException($"PVF徽章合成映射无效或重复: grade={grade} count={count}");
            }
        }
        if (mappings.Count == 0)
            throw new InvalidDataException("PVF徽章合成映射为空");
        return mappings.ToFrozenDictionary();
    }

    internal static ImmutableArray<EmblemCompoundRewardDefinition> ParsePool(string text, int boosterItemId,
        Func<string, string> read, IReadOnlyDictionary<int, string> stackable) =>
        ParsePool(text, boosterItemId, read, stackable,
            new Dictionary<int, (int Rarity, ImmutableHashSet<byte> PlatinumJobs)>());

    static ImmutableArray<EmblemCompoundRewardDefinition> ParsePool(string text, int boosterItemId,
        Func<string, string> read, IReadOnlyDictionary<int, string> stackable,
        Dictionary<int, (int Rarity, ImmutableHashSet<byte> PlatinumJobs)> metadata)
    {
        string body = Block(text, "booster info");
        if (body.Length == 0)
            throw new InvalidDataException($"徽章合成booster缺少奖励段: {boosterItemId}");
        // S5A9原表把徽章写在[stackable]，部分参考资源使用[emblem]；两者都只在
        // 最终逐项确认为[avatar emblem]后接纳，不把任意booster堆叠奖励当徽章。
        foreach (Match match in Regex.Matches(body, @"\[(?<kind>emblem|stackable)\](?<rows>.*?)\[/\k<kind>\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string[] tokens = match.Groups["rows"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 ||
                !int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int draws) || draws != 1 ||
                tokens.Length != 1 && (tokens.Length < 4 || (tokens.Length - 1) % 3 != 0))
                throw new InvalidDataException($"徽章合成booster抽取次数或奖池行宽无效: {boosterItemId}");
            if (tokens.Length == 1)
                continue;
            var rows = new List<(int ItemId, decimal Weight, int Count)>();
            int scale = 0;
            for (int at = 1; at < tokens.Length; at += 3)
            {
                if (!int.TryParse(tokens[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int itemId) || itemId <= 0 ||
                    !decimal.TryParse(tokens[at + 1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal weight) || weight < 0 ||
                    !int.TryParse(tokens[at + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
                    throw new InvalidDataException($"徽章合成booster奖励项无效: {boosterItemId}");
                scale = Math.Max(scale, (decimal.GetBits(weight)[3] >> 16) & 0xff);
                rows.Add((itemId, weight, count));
            }
            try
            {
                decimal factor = 1;
                for (int digit = 0; digit < scale; digit++)
                    factor = checked(factor * 10);
                var rewards = ImmutableArray.CreateBuilder<EmblemCompoundRewardDefinition>();
                long total = 0;
                foreach (var row in rows)
                {
                    long weight = checked((long)(row.Weight * factor));
                    if (weight <= 0)
                        continue;
                    if (!stackable.TryGetValue(row.ItemId, out string? rewardPath))
                        throw new InvalidDataException($"徽章合成奖励不在stackable列表: {row.ItemId}");
                    if (!metadata.TryGetValue(row.ItemId, out var definition))
                    {
                        string rewardText = read("stackable/" + rewardPath);
                        string type = Regex.Match(Section(rewardText, "stackable type"), @"`\[([^\]]+)\]`",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups[1].Value;
                        if (!type.Equals("avatar emblem", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"徽章合成奖励不是avatar emblem: {row.ItemId}");
                        int rarity = Integers(Section(rewardText, "rarity")).FirstOrDefault();
                        definition = (rarity, PlatinumJobs(rewardText));
                        metadata.Add(row.ItemId, definition);
                    }
                    total = checked(total + weight);
                    rewards.Add(new(row.ItemId, weight, row.Count, definition.Rarity, definition.PlatinumJobs));
                }
                if (total > 0)
                    return rewards.ToImmutable();
            }
            catch (OverflowException error)
            {
                throw new InvalidDataException($"徽章合成booster权重溢出: {boosterItemId}", error);
            }
        }
        throw new InvalidDataException($"徽章合成booster没有正权重emblem奖池: {boosterItemId}");
    }

    internal static ImmutableHashSet<byte> PlatinumJobs(string text)
    {
        var jobs = ImmutableHashSet.CreateBuilder<byte>();
        foreach (Match enchant in Regex.Matches(text, @"\[enchant\](.*?)\[/enchant\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            foreach (Match levelup in Regex.Matches(enchant.Groups[1].Value,
                @"\[skill levelup\](.*?)\[/skill levelup\]",
                RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                foreach (Match tag in Regex.Matches(levelup.Groups[1].Value, @"`\[([^\]]+)\]`",
                    RegexOptions.CultureInvariant))
                {
                    int job = Array.IndexOf(JobNames, tag.Groups[1].Value.Trim().ToLowerInvariant());
                    if (job >= 0)
                        jobs.Add(checked((byte)job));
                }
        return jobs.ToImmutable();
    }
}
