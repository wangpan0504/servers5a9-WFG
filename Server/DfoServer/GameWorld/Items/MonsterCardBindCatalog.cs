using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record MonsterCardBindRewardDefinition(int ItemId, long Weight);
internal sealed record MonsterCardBindPool(int Rarity, decimal Coefficient, int CountHint,
    ImmutableArray<MonsterCardBindRewardDefinition> Rewards);
internal sealed record MonsterCardBindUpgradeRow(int Rarity, ImmutableArray<int> Weights);

internal sealed class MonsterCardBindCatalog
{
    public const string RootPath = "etc/monstercardcontents.etc";
    public const int UpgradeRollRange = 10_000;
    readonly FrozenDictionary<int, MonsterCardBindPool> bindPools;
    readonly FrozenDictionary<int, MonsterCardBindPool> spherePools;
    readonly FrozenDictionary<int, int> sphereUpgradeChances;
    readonly FrozenDictionary<int, MonsterCardBindUpgradeRow> upgradeRows;

    public MonsterCardBindCatalog(PvfArchive archive)
        : this(archive.ReadText(RootPath)) { }

    internal MonsterCardBindCatalog(Func<string, string> read)
        : this(read(RootPath)) { }

    internal MonsterCardBindCatalog(string text)
    {
        bindPools = ParsePools(Block(text, "bind result infos"), "bind");
        spherePools = ParsePools(Block(text, "bind shpere result infos"), "sphere");
        sphereUpgradeChances = ParseSphereUpgrade(Section(text, "bind shpere upgrade info"));
        upgradeRows = ParseUpgradeRows(Block(text, "upgrade infos"));
    }

    public IEnumerable<MonsterCardBindPool> BindPools => bindPools.Values;
    public IEnumerable<MonsterCardBindPool> SpherePools => spherePools.Values;
    public IReadOnlyDictionary<int, int> SphereUpgradeChances => sphereUpgradeChances;
    public IEnumerable<MonsterCardBindUpgradeRow> UpgradeRows => upgradeRows.Values;

    public static bool IsSphere(int bindType) => bindType is >= 3 and <= 5;

    public MonsterCardBindPool Pool(int bindType, int rarity)
    {
        var source = IsSphere(bindType) ? spherePools : bindPools;
        return source.TryGetValue(rarity, out var pool)
            ? pool
            : throw new InvalidDataException($"怪物卡片合成缺少bindType={bindType} rarity={rarity}奖池");
    }

    internal static FrozenDictionary<int, MonsterCardBindPool> ParsePools(string body, string name)
    {
        var pools = new Dictionary<int, MonsterCardBindPool>();
        foreach (Match match in Regex.Matches(body, @"\[bind result info\](.*?)\[/bind result info\]",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string[] tokens = match.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5 || (tokens.Length - 3) % 2 != 0)
                throw new InvalidDataException($"PVF怪物卡片{name}奖池行宽无效");
            if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rarity) || rarity < 0 ||
                !decimal.TryParse(tokens[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal coefficient) ||
                coefficient < 0 ||
                !int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hint) || hint < 0)
                throw new InvalidDataException($"PVF怪物卡片{name}奖池头无效");
            var rewards = ImmutableArray.CreateBuilder<MonsterCardBindRewardDefinition>();
            long total = 0;
            for (int at = 3; at < tokens.Length; at += 2)
            {
                if (!int.TryParse(tokens[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int itemId) || itemId <= 0 ||
                    !long.TryParse(tokens[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long weight) || weight < 0)
                    throw new InvalidDataException($"PVF怪物卡片{name}奖励项无效");
                if (weight == 0)
                    continue;
                total = checked(total + weight);
                rewards.Add(new(itemId, weight));
            }
            if (total <= 0 || rewards.Count == 0 || !pools.TryAdd(rarity, new(rarity, coefficient, hint, rewards.ToImmutable())))
                throw new InvalidDataException($"PVF怪物卡片{name}奖池重复或没有正权重: rarity={rarity}");
        }
        if (pools.Count == 0)
            throw new InvalidDataException($"PVF怪物卡片{name}奖池为空");
        return pools.ToFrozenDictionary();
    }

    internal static FrozenDictionary<int, int> ParseSphereUpgrade(string text)
    {
        int[] values = Integers(text);
        if (values.Length == 0 || values.Length % 2 != 0)
            throw new InvalidDataException("PVF五卡合成升级附加行宽无效");
        var chances = new Dictionary<int, int>();
        for (int at = 0; at < values.Length; at += 2)
        {
            int count = values[at], chance = values[at + 1];
            if (count is < 2 or > 5 || chance < 0 || !chances.TryAdd(count, chance))
                throw new InvalidDataException($"PVF五卡合成升级附加无效: count={count}");
        }
        return chances.ToFrozenDictionary();
    }

    internal static FrozenDictionary<int, MonsterCardBindUpgradeRow> ParseUpgradeRows(string body)
    {
        var rows = new Dictionary<int, MonsterCardBindUpgradeRow>();
        // 现场 Script.pvf 的 [upgrade info] 没有成对 [/upgrade info]，只靠下一标签切行。
        // 该块属于未注册的 CMD1122；缺省不得阻断 CMD414 目录加载。
        foreach (Match match in Regex.Matches(body, @"\[upgrade info\](.*?)(?:\[/upgrade info\]|(?=\[)|$)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int[] values = Integers(match.Groups[1].Value);
            if (values.Length < 2 || values[0] < 0)
                throw new InvalidDataException("PVF卡片升级权重行无效");
            var weights = values.Skip(1).ToImmutableArray();
            if (weights.Any(weight => weight < 0) || !rows.TryAdd(values[0], new(values[0], weights)))
                throw new InvalidDataException($"PVF卡片升级权重重复或越界: rarity={values[0]}");
        }
        return rows.ToFrozenDictionary();
    }

    public static int UpgradeChance(decimal coefficient)
    {
        long value = decimal.ToInt64(decimal.Truncate(checked(coefficient * 100m)));
        if (value < 0 || value > UpgradeRollRange)
            throw new InvalidDataException("怪物卡片升档系数超出万分比范围");
        return checked((int)value);
    }
}
