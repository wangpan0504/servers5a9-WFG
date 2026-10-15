using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AvatarDyeDefinition(ushort Color);

internal sealed class AvatarDyeCatalog
{
    const string ColorPath = "etc/avatarcolorvariation.etc";
    readonly Func<string, string> read;
    readonly IReadOnlyDictionary<int, string> equipment, stackable;
    readonly FrozenSet<ushort> colors;
    readonly ConcurrentDictionary<int, AvatarDyeDefinition> sources = new();
    readonly ConcurrentDictionary<int, bool> targets = new();

    public AvatarDyeCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment, IReadOnlyDictionary<int, string> stackable)
        : this(archive.ReadText, equipment, stackable, archive.Contains(ColorPath) ? archive.ReadText(ColorPath) : null) { }

    internal AvatarDyeCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable, string? colorText)
    {
        this.read = read;
        this.equipment = equipment;
        this.stackable = stackable;
        colors = ParseColors(colorText);
    }

    public AvatarDyeDefinition? Get(int itemId)
    {
        if (sources.TryGetValue(itemId, out var known)) return known;
        if (!stackable.TryGetValue(itemId, out string? path)) return null;
        var script = new PvfDocument(read("stackable/" + path));
        if (!Regex.IsMatch(script.Section("stackable type"), @"^\s*`\[dye\]`\s+0\s*$") ||
            !script.TryGetInt32("dye type", out int type) || type != 1) return null;
        foreach (string field in new[] { "cooltime maintenance", "effect maintenance", "total usable count", "total weekly usable count",
            "total accumulated usable count", "use count limit all server item", "usable datetime", "usable first growtype",
            "action usable server type", "usable game server type", "usable server type", "check usable itemlevel" })
            if (script.Has(field)) return null;
        string[] values = script.Block("dye info").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // 已核对type1的固定颜色/权重对；不将未知类型或多行表当成取首项的随机染色。
        if (values.Length != 2 || !ushort.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort color) ||
            !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight) || weight <= 0 || !colors.Contains(color))
            return null;
        return sources.GetOrAdd(itemId, new AvatarDyeDefinition(color));
    }

    public bool CanDye(int itemId)
    {
        if (!equipment.TryGetValue(itemId, out string? path)) return false;
        return targets.GetOrAdd(itemId, _ => Regex.IsMatch(EquipmentFile.ReadScript(read, "equipment/" + path).Section("enable dye"),
            @"^\s*1\s+[01]\s*$", RegexOptions.CultureInvariant));
    }

    static FrozenSet<ushort> ParseColors(string? text)
    {
        if (text is null) return FrozenSet<ushort>.Empty;
        string[] values = Regex.Matches(new PvfDocument(text).Block("avatar color map"), "`[^`]*`|\\S+")
            .Select(match => match.Value).ToArray();
        if (values.Length == 0 || values.Length % 5 != 0) throw new InvalidDataException("装扮颜色表行宽无效");
        var result = new HashSet<ushort>();
        for (int at = 0; at < values.Length; at += 5)
        {
            if (!ushort.TryParse(values[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort color) || color > short.MaxValue ||
                !result.Add(color) || !byte.TryParse(values[at + 1], out _) || !byte.TryParse(values[at + 2], out _) ||
                !byte.TryParse(values[at + 3], out _) || !Regex.IsMatch(values[at + 4], "^`[^`]*`$"))
                throw new InvalidDataException("装扮颜色定义无效或重复");
        }
        return result.ToFrozenSet();
    }
}
