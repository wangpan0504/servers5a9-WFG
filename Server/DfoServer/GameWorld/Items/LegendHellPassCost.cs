using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// freepassitem的[hell legend freepass item]整数二元组与活动2370签到星空裂缝通行证（490013043），进入星空裂缝时优先消耗1张并免除邀请函与灵石。
internal sealed record LegendHellPassCost(int ItemId, int Count)
{
    public static ImmutableArray<LegendHellPassCost> Load(PvfArchive archive) => archive.Contains(CosmoPassCost.Path)
        ? Parse(archive.ReadTokens(CosmoPassCost.Path), archive.ResolveString) : [];

    internal static ImmutableArray<LegendHellPassCost> Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var sections = document.Sections.Where(s => s.Name == "hell legend freepass item").ToArray();
        if (sections.Length == 0) return [];
        if (!document.Diagnostics.IsEmpty || sections is not [{ Children.Length: 0, IsBlock: true } section] ||
            section.Values.Length % 2 != 0 || section.Values.Any(v => v.Type != 0))
            throw new InvalidDataException("星空裂缝通行证声明须为唯一完整整数二元组表");
        var rows = ImmutableArray.CreateBuilder<LegendHellPassCost>();
        var ids = new HashSet<int>();
        for (int at = 0; at < section.Values.Length; at += 2)
        {
            var row = new LegendHellPassCost(section.Values[at].Value, section.Values[at + 1].Value);
            if (row.ItemId <= 0 || row.Count <= 0 || !ids.Add(row.ItemId))
                throw new InvalidDataException("星空裂缝通行证编号或费用无效");
            rows.Add(row);
        }
        if (!ids.Contains(CosmoPassCost.DailyAttendanceStarPass))
            rows.Add(new(CosmoPassCost.DailyAttendanceStarPass, 1));
        return rows.ToImmutable();
    }
}
