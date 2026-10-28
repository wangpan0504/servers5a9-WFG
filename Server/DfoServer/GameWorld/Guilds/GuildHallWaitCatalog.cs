using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed class GuildHallWaitCatalog
{
    public const string ResourcePath = "etc/guild/guildranking.etc";
    public int DailyMaximum { get; }
    public int Points { get; }
    public int IntervalSeconds { get; }

    public GuildHallWaitCatalog(PvfArchive pvf) : this(pvf.ReadText(ResourcePath)) { }

    internal GuildHallWaitCatalog(string text)
    {
        var blocks = Regex.Matches(text,
            @"(?ms)^\[guild member ranking factor\]\s*$(.*?)^\[/guild member ranking factor\]\s*$");
        if (blocks.Count != 1 || Regex.Matches(text, @"(?m)^\[/?guild member ranking factor\]").Count != 2)
            throw new InvalidDataException("公会大厅等待贡献所属资源块缺失或重复");
        var fields = Regex.Matches(blocks[0].Groups[1].Value,
            @"(?ms)^\[guild agit wait\][ \t]*\r?\n([^\[]*)(?=\[|\z)");
        if (fields.Count != 1) throw new InvalidDataException("公会大厅等待贡献资源缺失或重复");
        string[] values = fields[0].Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 3 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int maximum) ||
            !int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out int points) ||
            !int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out int interval) ||
            maximum <= 0 || points <= 0 || points > maximum || interval <= 0 || interval > 86400)
            throw new InvalidDataException("公会大厅等待贡献资源必须为合法的日上限、分值与秒数");
        DailyMaximum = maximum;
        Points = points;
        IntervalSeconds = interval;
    }
}
