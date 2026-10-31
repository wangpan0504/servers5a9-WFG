using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Dungeons;

// 实验性（未在95客户端实证）：etc/itemdropinfo_monster_hell.etc —— 深渊派对专用的装备奖励规则（来源：参考项目 90 HellMonsterDropConfig）。
// [dungeon difficulty drop prob]：行 = 等级下限 等级上限 + 5 个概率（千分子）；[basis of rarity dicision]：首数=行数，之后每行 7 个稀有度阈值；
// [item drop ref table]：等级 v0 v1，装备等级区间 = [level-v0, level+v1-1]。
internal sealed class DungeonHellDropRules
{
    public const string Path = "etc/itemdropinfo_monster_hell.etc";
    readonly List<(int Min, int Max, int[] Values)> rates = [];
    readonly List<int[]> rarityRows = [];
    readonly Dictionary<int, (int V0, int V1)> refs = [];

    public static DungeonHellDropRules? TryLoad(PvfLib.PvfArchive pvf) => pvf.Contains(Path) ? Parse(pvf.ReadText(Path)) : null;

    public static DungeonHellDropRules Parse(string text)
    {
        var rules = new DungeonHellDropRules();
        var sections = new Dictionary<string, int[]>();
        foreach (Match match in Regex.Matches(text, @"\[([^\]/][^\]]*)\]([^\[]*)"))
            sections[match.Groups[1].Value.Trim().ToLowerInvariant()] = match.Groups[2].Value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : int.MinValue).ToArray();
        if (sections.TryGetValue("dungeon difficulty drop prob", out var prob))
            for (int at = 0; at + 6 < prob.Length; at += 7) rules.rates.Add((prob[at], prob[at + 1], prob[(at + 2)..(at + 7)]));
        if (sections.TryGetValue("basis of rarity dicision", out var rarity) && rarity.Length >= 1)
        {
            // 百万抽样之外的1000001/1000002是不可命中的哨兵，不应让有效分布超出分母。
            // 先归一到实际抽样范围再保持前缀单调；否则史诗药剂会把完整深渊分布误判为不支持。
            for (int row = 0; row < rarity[0] && 1 + (row + 1) * 7 <= rarity.Length; row++)
            {
                var values = rarity[(1 + row * 7)..(1 + (row + 1) * 7)];
                if (values.Any(value => value < 0)) throw new InvalidDataException("深渊稀有度阈值不能为负或包含非整数");
                for (int i = 0; i < values.Length; i++)
                    values[i] = Math.Max(Math.Min(values[i], 1000000), i == 0 ? 0 : values[i - 1]);
                rules.rarityRows.Add(values);
            }
        }
        if (sections.TryGetValue("item drop ref table", out var table))
            for (int at = 0; at + 2 < table.Length; at += 3) rules.refs.TryAdd(table[at], (table[at + 1], table[at + 2]));
        return rules;
    }

    // 概率下标：深渊档位（A=1..）夹到 0..4。行里 5 个值的含义参考项目也没给出命名；这里按"档位下标"取值（实验性）。
    public int? Rate(int level, int index)
    {
        foreach (var row in rates)
            if (level >= row.Min && level <= row.Max) return Math.Clamp(row.Values[Math.Clamp(index, 0, row.Values.Length - 1)], 0, 1000);
        return null;
    }

    // 组键：档位 2 取行 B，其余取行 A（参考项目 dungeonHellPartyGroupKey）。
    public int[]? Rarity(byte mode) => rarityRows.Count == 0 ? null : rarityRows[Math.Min(mode == 2 ? 1 : 0, rarityRows.Count - 1)];

    public (int Minimum, int Maximum)? GradeRange(int level) => refs.TryGetValue(level, out var row) ? (level - row.V0, level + row.V1 - 1) : null;
}
