using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record RefinementTicketDefinition(byte Level, int SuccessWeight, bool Additional, int MaximumSourceLevel,
    ImmutableArray<int> AllowedItemIds, string? PendingRule = null)
{
    public bool Available => PendingRule is null;

    public bool TryTargetLevel(byte currentLevel, int maximumLevel, out byte targetLevel)
    {
        targetLevel = Level;
        if (!Available)
            return false;
        if (!Additional)
            return true;
        int limit = maximumLevel - Level;
        if (MaximumSourceLevel >= 0)
            limit = Math.Min(limit, MaximumSourceLevel);
        if (currentLevel > limit)
            return false;
        targetLevel = checked((byte)(currentLevel + Level));
        return true;
    }
    public static RefinementTicketDefinition? Parse(PvfDocument script)
    {
        if (!script.Has("equipment separate reinforcement ticket"))
            return null;
        var match = Regex.Match(script.Section("equipment separate reinforcement ticket"),
            @"^\s*(\d+)\s+(\d+)\s+`(fixed|additional)`(?:\s+(-?\d+))?\s*$", RegexOptions.IgnoreCase);
        int maximum = -1;
        if (!match.Success || !byte.TryParse(match.Groups[1].Value, out byte level) || level is 0 or > 127 ||
            !int.TryParse(match.Groups[2].Value, out int percent) || percent is < 0 or > 100 ||
            match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, out maximum))
            return Pending("锻造券定义无效或包含未支持的附加字段");
        if (script.Has("reinforcement usable item list") && !script.Has("/reinforcement usable item list"))
            return Pending("锻造券装备清单未闭合");
        var ids = ImmutableArray.CreateBuilder<int>();
        foreach (string value in script.Block("reinforcement usable item list").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
                return Pending("锻造券装备编号无效");
            ids.Add(id);
        }
        // 固定券的等级是覆盖值；增量券的最后一项限制使用前等级，负数表示仅受锻造表上限约束。
        return new(level, percent * 100, match.Groups[3].Value.Equals("additional", StringComparison.OrdinalIgnoreCase), maximum, ids.ToImmutable());
    }

    // 未接入的券仍能显示和存放，只拒绝使用；不能降级为普通材料路径。
    static RefinementTicketDefinition Pending(string reason) => new(0, 0, false, -1, [], reason);
}
