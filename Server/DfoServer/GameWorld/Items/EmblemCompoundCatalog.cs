using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
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
    public const string GuildRootPath = "etc/guildreform.etc";
    static readonly string[] JobNames =
    [
        "swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer",
        "at priest", "gun blader"
    ];

    readonly FrozenDictionary<(int Grade, int Count), EmblemCompoundPool> pools;
    readonly FrozenDictionary<(int Level, int Grade, int Count), EmblemCompoundPool> guildPools;

    public EmblemCompoundCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
        : this(archive.ReadText, stackable, archive.ReadText(GuildRootPath)) { }

    internal EmblemCompoundCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> stackable,
        string? guildText = null)
    {
        string text = read(RootPath);
        var mappings = ParseMappings(text);
        var guildMappings = ParseGuildMappings(guildText ?? text);
        var loaded = new Dictionary<int, EmblemCompoundPool>();
        var expanded = new Dictionary<int, ImmutableArray<EmblemCompoundRewardDefinition>>();
        var rewardMetadata = new Dictionary<int, (int Rarity, ImmutableHashSet<byte> PlatinumJobs)>();
        var result = new Dictionary<(int Grade, int Count), EmblemCompoundPool>();
        EmblemCompoundPool Load(int boosterItemId)
        {
            if (!loaded.TryGetValue(boosterItemId, out var pool))
            {
                if (!stackable.TryGetValue(boosterItemId, out string? boosterPath))
                    throw new InvalidDataException($"徽章合成booster不在stackable列表: {boosterItemId}");
                var rewards = ParsePool(read("stackable/" + boosterPath), boosterItemId, read, stackable, rewardMetadata, expanded);
                loaded.Add(boosterItemId, pool = new(boosterItemId, rewards));
            }
            return pool;
        }
        foreach (var (key, boosterItemId) in mappings) result.Add(key, Load(boosterItemId));
        pools = result.ToFrozenDictionary();
        guildPools = guildMappings.ToFrozenDictionary(row => row.Key, row => Load(row.Value));
    }

    public EmblemCompoundPool For(int grade, int count) =>
        pools.TryGetValue((grade, count), out var pool)
            ? pool
            : throw new InvalidDataException($"徽章合成缺少grade={grade} count={count}映射");

    public IEnumerable<KeyValuePair<(int Grade, int Count), EmblemCompoundPool>> Entries => pools;

    public EmblemCompoundPool? ForGuild(int level, int grade, int count) =>
        guildPools.GetValueOrDefault((level, grade, count));

    internal static FrozenDictionary<(int Level, int Grade, int Count), int> ParseGuildMappings(string text)
    {
        const string tag = "guild npc emblem compound info";
        var mappings = new Dictionary<(int Level, int Grade, int Count), int>();
        if (!text.Contains("[" + tag + "]", StringComparison.OrdinalIgnoreCase)) return mappings.ToFrozenDictionary();
        int[] values = Integers(Block(text, tag));
        int at = 0;
        while (at < values.Length)
        {
            if (values.Length - at < 3) throw new InvalidDataException("PVF公会徽章合成映射头截断");
            int level = values[at++], grade = values[at++], maximum = values[at++];
            if (level is < 1 or > 5 || grade <= 0 || maximum is < 2 or > 5)
                throw new InvalidDataException($"PVF公会徽章合成映射头无效: level={level} grade={grade} count={maximum}");
            if (values.Length - at < maximum - 1) throw new InvalidDataException("PVF公会徽章合成奖池映射截断");
            for (int count = 2; count <= maximum; count++)
            {
                int booster = values[at++];
                if (booster <= 0 || !mappings.TryAdd((level, grade, count), booster))
                    throw new InvalidDataException($"PVF公会徽章合成映射无效或重复: level={level} grade={grade} count={count}");
            }
        }
        if (mappings.Count == 0) throw new InvalidDataException("PVF公会徽章合成映射为空或未闭合");
        return mappings.ToFrozenDictionary();
    }

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
        Dictionary<int, (int Rarity, ImmutableHashSet<byte> PlatinumJobs)> metadata,
        Dictionary<int, ImmutableArray<EmblemCompoundRewardDefinition>>? expanded = null, HashSet<int>? visiting = null)
    {
        expanded ??= new(); visiting ??= new();
        if (expanded.TryGetValue(boosterItemId, out var cached)) return cached;
        if (visiting.Count >= 8 || !visiting.Add(boosterItemId))
            throw new InvalidDataException($"徽章合成booster循环引用或嵌套过深: {boosterItemId}");
        try
        {
            var result = ParsePoolContents(text, boosterItemId, read, stackable, metadata, expanded, visiting);
            expanded.Add(boosterItemId, result); return result;
        }
        finally { visiting.Remove(boosterItemId); }
    }

    static ImmutableArray<EmblemCompoundRewardDefinition> ParsePoolContents(string text, int boosterItemId,
        Func<string, string> read, IReadOnlyDictionary<int, string> stackable,
        Dictionary<int, (int Rarity, ImmutableHashSet<byte> PlatinumJobs)> metadata,
        Dictionary<int, ImmutableArray<EmblemCompoundRewardDefinition>> expanded, HashSet<int> visiting)
    {
        string body = Block(text, "booster info");
        if (body.Length == 0)
            throw new InvalidDataException($"徽章合成booster缺少奖励段: {boosterItemId}");
        var characterPools = Regex.Matches(body, @"\[charactor\](.*?)\[/charactor\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (characterPools.Count > 0)
        {
            var rewards = ImmutableArray.CreateBuilder<EmblemCompoundRewardDefinition>();
            var jobs = new HashSet<byte>();
            foreach (Match characterPool in characterPools)
            {
                var header = Regex.Match(characterPool.Groups[1].Value,
                    @"^\s*`\[([^\]]+)\]`\s+(\d+)\s+(\d+)\s+(\d+)\s+(.*)$", RegexOptions.Singleline);
                int job = Array.IndexOf(JobNames, header.Groups[1].Value);
                if (!header.Success || job < 0 || !jobs.Add((byte)job) ||
                    header.Groups[2].Value != "1" || header.Groups[3].Value != "99" || header.Groups[4].Value != "1")
                    throw new InvalidDataException($"徽章合成职业奖池资格或抽数不支持: {boosterItemId}");
                var candidates = ParsePoolContents("[booster info]\n[stackable]\n1 " + header.Groups[5].Value +
                    "\n[/stackable]\n[/booster info]", boosterItemId, read, stackable, metadata, expanded, visiting);
                foreach (var candidate in candidates)
                {
                    if (candidate.Rarity != 6) throw new InvalidDataException($"职业徽章池含非白金奖励: {boosterItemId}");
                    if (candidate.PlatinumJobs.Contains((byte)job)) rewards.Add(candidate with { PlatinumJobs = [(byte)job] });
                }
            }
            if (rewards.Count is 0 or > 20000) throw new InvalidDataException($"职业徽章池有效候选数量无效: {boosterItemId}");
            return rewards.ToImmutable();
        }
        // S5A9原表把徽章写在[stackable]，部分参考资源使用[emblem]；两者都只在
        // 最终逐项确认为[avatar emblem]后接纳，不把任意booster堆叠奖励当徽章。
        foreach (Match match in Regex.Matches(body, @"\[(?<kind>emblem|stackable|booster)\](?<rows>.*?)\[/\k<kind>\]",
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
                var branches = new List<(long Weight, ImmutableArray<EmblemCompoundRewardDefinition> Rewards)>();
                long total = 0;
                foreach (var row in rows)
                {
                    long weight = checked((long)(row.Weight * factor));
                    if (weight <= 0)
                        continue;
                    if (!stackable.TryGetValue(row.ItemId, out string? rewardPath))
                        throw new InvalidDataException($"徽章合成奖励不在stackable列表: {row.ItemId}");
                    if (match.Groups["kind"].Value.Equals("booster", StringComparison.OrdinalIgnoreCase))
                    {
                        if (row.Count != 1) throw new InvalidDataException($"嵌套徽章池必须只开一个子池: {boosterItemId}");
                        if (!expanded.TryGetValue(row.ItemId, out var child))
                        {
                            string childText = read("stackable/" + rewardPath);
                            if (!Regex.IsMatch(Section(childText, "stackable type"), @"^\s*`\[booster\]`\s+\d+\s*$"))
                                throw new InvalidDataException($"嵌套徽章池引用非booster: {row.ItemId}");
                            child = ParsePool(childText, row.ItemId, read, stackable, metadata, expanded, visiting);
                        }
                        branches.Add((weight, child)); continue;
                    }
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
                if (branches.Count > 0) return Flatten(branches, boosterItemId);
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

    static ImmutableArray<EmblemCompoundRewardDefinition> Flatten(
        List<(long Weight, ImmutableArray<EmblemCompoundRewardDefinition> Rewards)> branches, int boosterItemId)
    {
        var totals = branches.Select(branch => branch.Rewards.Aggregate(BigInteger.Zero, (sum, row) => sum + row.Weight)).ToArray();
        BigInteger scale = BigInteger.One;
        foreach (var total in totals)
        {
            if (total <= 0) throw new InvalidDataException($"嵌套徽章池无正权重: {boosterItemId}");
            scale = scale / BigInteger.GreatestCommonDivisor(scale, total) * total;
            if (scale.GetBitLength() > 4096) throw new InvalidDataException($"嵌套徽章池概率精度超过上限: {boosterItemId}");
        }
        var rows = new List<(EmblemCompoundRewardDefinition Reward, BigInteger Weight)>();
        for (int index = 0; index < branches.Count; index++)
            foreach (var reward in branches[index].Rewards)
            {
                // 必须除以各子池总权重；直接乘父子原权重会改变不同大小子池的概率。
                rows.Add((reward, branches[index].Weight * (BigInteger)reward.Weight * (scale / totals[index])));
                if (rows.Count > 20000) throw new InvalidDataException($"嵌套徽章池展开超过上限: {boosterItemId}");
            }
        BigInteger gcd = rows.Select(row => row.Weight).Aggregate(BigInteger.GreatestCommonDivisor);
        BigInteger sum = rows.Aggregate(BigInteger.Zero, (total, row) => total + row.Weight / gcd);
        if (sum > long.MaxValue) throw new InvalidDataException($"嵌套徽章池权重溢出: {boosterItemId}");
        return rows.Select(row => row.Reward with { Weight = (long)(row.Weight / gcd) }).ToImmutableArray();
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
