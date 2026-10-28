using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed class GuildAppearanceCatalog
{
    public int Marks { get; }
    public int FreeMarks { get; }
    public int MarkPrice { get; }
    public int AuraMarks { get; }
    public int AuraFrames { get; }
    readonly (byte Tier, byte Level)[] modernMarks;
    public int ModernMarks => modernMarks.Length;
    public const int ModernDesignFrames = 20;
    // 匹配客户端三种尺寸Background.img均为4帧；可视槽并非图案总数。
    public const int ModernBackgrounds = 4;

    public GuildAppearanceCatalog(PvfArchive pvf) : this(pvf.ReadText("etc/guild.etc"),
        pvf.ReadText("ui/guild/guildmark_cn.xui"), pvf.ReadText("etc/guild/insigniashapelist.etc"),
        pvf.ReadText("etc/guildreform.etc")) { }

    internal GuildAppearanceCatalog(string parameters, string icons, string insignia, string reform)
    {
        int[] fee = PvfScript.Integers(PvfScript.Block(parameters, "guild mark"));
        if (fee.Length != 2 || fee[0] < 0 || fee[1] <= 0)
            throw new InvalidDataException("PVF 公会会标费用无效");
        FreeMarks = fee[0];
        MarkPrice = fee[1];
        var ids = new HashSet<int>();
        foreach (var icon in XDocument.Parse(icons.TrimEnd('\0')).Descendants("IControl"))
        {
            string id = (string?)icon.Attribute("ID") ?? "";
            if (!id.StartsWith("STR:", StringComparison.Ordinal) ||
                !int.TryParse(id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
                value is < 1 or > 256 || !ids.Add(value))
                throw new InvalidDataException("PVF 公会会标索引无效");
        }
        Marks = ids.Count;
        if (Marks == 0 || Marks < FreeMarks || Enumerable.Range(1, Marks).Any(id => !ids.Contains(id)))
            throw new InvalidDataException("PVF 公会会标索引不连续");
        AuraMarks = AnimationCount(insignia, "insignia mark");
        AuraFrames = AnimationCount(insignia, "insignia frame");
        string requirements = PvfScript.Block(reform, "guild mark info");
        var entries = Regex.Matches(requirements, "`(?<tier>[^`]+)`\\s+(?<level>[0-9]+)");
        if (entries.Count != ModernDesignFrames || Regex.Replace(requirements, "`[^`]+`\\s+[0-9]+", "").Trim().Length != 0)
            throw new InvalidDataException("PVF 现代公会会标门槛无效");
        modernMarks = entries.Select(entry =>
        {
            byte tier = entry.Groups["tier"].Value switch
            {
                "low class" => 0, "mid class" => 1, "high class" => 2, "guardian" => 3, "conqueror" => 4,
                _ => throw new InvalidDataException("PVF 现代公会会标档位无效")
            };
            if (!byte.TryParse(entry.Groups["level"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out byte level) ||
                level is < 1 or > 10)
                throw new InvalidDataException("PVF 现代公会会标等级无效");
            return (tier, level);
        }).ToArray();
        for (int index = 1; index < modernMarks.Length; index++)
            if (modernMarks[index].CompareTo(modernMarks[index - 1]) < 0)
                throw new InvalidDataException("PVF 现代公会会标门槛未递增");
    }

    static int AnimationCount(string text, string section)
    {
        string block = PvfScript.Block(text, section);
        var strings = Regex.Matches(block, "`([^`]+)`");
        if (strings.Count == 0 || strings.Any(value => !value.Groups[1].Value.EndsWith(".ani", StringComparison.OrdinalIgnoreCase)) ||
            Regex.Replace(block, "`[^`]+`", "").Trim().Length != 0)
            throw new InvalidDataException("PVF 公会装饰动画索引无效");
        return strings.Count;
    }

    public bool ValidLegacyMark(uint mark) => mark < Marks;
    public bool ValidPackedMark(uint mark) => (mark & 0xFF00FF00u) == 0 &&
        (mark & 0xFF) < ModernMarks && (mark >> 16) < ModernBackgrounds;
    public bool ValidModernMark(uint mark, byte tier, byte level)
    {
        if (!ValidPackedMark(mark) || tier > 4 || level == 0) return false;
        var requirement = modernMarks[mark & 0xFF];
        return tier > requirement.Tier || tier == requirement.Tier && level >= requirement.Level;
    }
    public bool ValidAura(uint mark, uint frame) => mark == uint.MaxValue && frame == uint.MaxValue ||
        mark < AuraMarks && frame < AuraFrames;
    public int Price(uint mark) => ValidLegacyMark(mark) ? mark < FreeMarks ? 0 : MarkPrice :
        throw new ArgumentOutOfRangeException(nameof(mark));
}
