using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

/// <summary>
/// 公会参数目录：数值全部来自 PVF，业务代码不得写死常数。
/// 建会花费与公会仓库格数读 `etc/guild.etc`，公会等级经验曲线读 `etc/guildreform.etc` 的 `guild level exp` 块。
/// 只暴露当前确实要用的项；`etc/guild.etc` 里其余段落（捐赠、印记、升级消耗、内容列表）在对应玩法接入时再读取。
/// </summary>
internal sealed partial class GuildParameterCatalog
{
    /// <summary>公会档位数量：low class / mid class / high class / guardian / conqueror。</summary>
    internal const int TierCount = 5;

    /// <summary>每档的公会等级数量。</summary>
    internal const int LevelPerTier = 10;

    readonly int[] levelExperience;

    public GuildParameterCatalog(PvfArchive pvf)
        : this(pvf.ReadText("etc/guild.etc"), pvf.ReadText("etc/guildreform.etc")) { }

    internal GuildParameterCatalog(string guildText, string reformText)
    {
        CreateCost = Scalar(guildText, "guild create cost", "建会花费");
        CargoSizeLimit = Scalar(guildText, "guild cargo size limit", "公会仓库格数");
        levelExperience = ParseLevelExperience(PvfScript.Block(reformText, "guild level exp"));
    }

    /// <summary>创建公会所需金币（`[guild create cost]`）。</summary>
    public int CreateCost { get; }

    /// <summary>公会仓库格数（`[guild cargo size limit]`）。</summary>
    public int CargoSizeLimit { get; }

    /// <summary>指定档位与等级所需的公会经验；档位 0–4、等级 1–10，越界即拒绝而不是夹取。</summary>
    public int LevelExperience(int tier, int level)
    {
        if (tier < 0 || tier >= TierCount || level < 1 || level > LevelPerTier)
            throw new ArgumentOutOfRangeException(nameof(level), "公会等级超出 PVF 声明范围");
        return levelExperience[tier * LevelPerTier + (level - 1)];
    }

    static int Scalar(string text, string tag, string what)
    {
        int[] values = PvfScript.Integers(PvfScript.Section(text, tag));
        if (values.Length == 0 || values[0] <= 0)
            throw new InvalidDataException("PVF 公会参数缺失或非正数: " + what);
        return values[0];
    }

    static int[] ParseLevelExperience(string block)
    {
        var table = new int[TierCount * LevelPerTier];
        var seen = new bool[table.Length];
        foreach (Match match in TierRow().Matches(block))
        {
            int tier = TierIndex(match.Groups["tier"].Value);
            if (!int.TryParse(match.Groups["level"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int level) ||
                !int.TryParse(match.Groups["exp"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int experience) ||
                tier < 0 || level < 1 || level > LevelPerTier)
                throw new InvalidDataException("PVF 公会等级经验表越界: " + match.Value);
            int slot = tier * LevelPerTier + (level - 1);
            if (seen[slot])
                throw new InvalidDataException("PVF 公会等级经验表重复行: " + match.Value);
            seen[slot] = true;
            table[slot] = experience;
        }
        if (Array.IndexOf(seen, false) >= 0)
            throw new InvalidDataException("PVF 公会等级经验表不完整");
        return table;
    }

    static int TierIndex(string name) => name.Trim().ToLowerInvariant() switch
    {
        "low class" => 0,
        "mid class" => 1,
        "high class" => 2,
        "guardian" => 3,
        "conqueror" => 4,
        _ => -1,
    };

    [GeneratedRegex(@"`(?<tier>[^`]+)`\s+(?<level>\d+)\s+(?<exp>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TierRow();
}
