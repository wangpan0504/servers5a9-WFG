using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentLevelTicket(int Increase, int Rarity, int Minimum, int Maximum,
    ImmutableHashSet<int> Excluded);

// 次元能量的材料倍率来自同版客户端资源；本目录不持有或解析装备实例。
internal sealed class EquipmentLevelUpgradeRules
{
    readonly FrozenDictionary<int, (int Item, int Count, double Weight)> rarity;
    readonly FrozenDictionary<string, double> slots;
    readonly (double Minimum, double Maximum, double First, double Last)[][] ranges;

    public EquipmentLevelUpgradeRules(string text)
    {
        string[] Tokens(string tag) => PvfScript.Block(text, tag).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // 原生表按float存储，插值在x87中展开；不能把文本小数直接当作精确double。
        double Number(string token) => float.Parse(token, CultureInfo.InvariantCulture);
        var rows = Tokens("consume item by rarity");
        if (rows.Length == 0 || rows.Length % 4 != 0) throw new InvalidDataException("次元能量材料表不完整");
        var rarities = new Dictionary<int, (int, int, double)>();
        for (int i = 0; i < rows.Length; i += 4)
        {
            int grade = int.Parse(rows[i], CultureInfo.InvariantCulture), id = int.Parse(rows[i + 1], CultureInfo.InvariantCulture),
                count = int.Parse(rows[i + 2], CultureInfo.InvariantCulture);
            double weight = Number(rows[i + 3]);
            if (grade < 0 || id <= 0 || count <= 0 || !double.IsFinite(weight) || weight <= 0 || !rarities.TryAdd(grade, (id, count, weight)))
                throw new InvalidDataException("次元能量材料无效");
        }
        rarity = rarities.ToFrozenDictionary();
        var slotWeights = new Dictionary<string, double>();
        var matches = Regex.Matches(PvfScript.Block(text, "weight by weapon"), @"`([^`]+)`\s+([0-9.]+)");
        foreach (Match match in matches)
        {
            double value = Number(match.Groups[2].Value);
            if (!double.IsFinite(value) || value <= 0 || !slotWeights.TryAdd(match.Groups[1].Value, value))
                throw new InvalidDataException("次元能量部位倍率无效");
        }
        slots = slotWeights.ToFrozenDictionary();
        ranges = new[] { "level", "upgrade", "amplify", "seperate" }.Select(tag =>
        {
            var values = Tokens("weight by " + tag);
            if (values.Length == 0 || values.Length % 4 != 0) throw new InvalidDataException("次元能量区间表不完整");
            var result = new List<(double, double, double, double)>();
            for (int i = 0; i < values.Length; i += 4)
            {
                double min = Number(values[i]), max = Number(values[i + 1]), first = Number(values[i + 2]), last = Number(values[i + 3]);
                if (!double.IsFinite(min + max + first + last) || min < 0 || max <= min || first <= 0 || last <= 0 ||
                    result.Count > 0 && result[^1].Item2 != min) throw new InvalidDataException("次元能量区间无效");
                result.Add((min, max, first, last));
            }
            return result.ToArray();
        }).ToArray();
    }

    public ItemMaterialCost? Cost(int grade, int slot, int level, int upgrade, int amplify, int refinement)
    {
        if (!rarity.TryGetValue(grade, out var row)) return null;
        string part = slot switch { 12 => "weapon", 14 => "coat", 15 => "shoulder", 16 => "pants", 17 => "shoes",
            18 => "waist", 19 => "amulet", 20 => "wrisat", 21 => "ring", 22 => "support", 23 => "magic stone", 25 => "earring", _ => "" };
        if (!slots.TryGetValue(part, out double slotWeight)) return null;
        double Weight(int table, int value)
        {
            foreach (var range in ranges[table])
                if (value >= range.Minimum && value <= range.Maximum)
                    return range.First + (range.Last - range.First) / (range.Maximum - range.Minimum) * (value - range.Minimum);
            return 1;
        }
        // 03FBA9C0在强化/增幅乘积及锻造合并之后分别写回float；保留这两个舍入点。
        float modified = (float)Weight(1, upgrade);
        modified = (float)(Weight(2, amplify) * modified);
        modified = (float)(Weight(3, refinement) + modified - 1);
        // 最终乘积使用更高精度后再截断：78级防具、强化17、锻造4应为189，
        // double的188.99999999999997不能错误少收一件材料；不对所有结果加任意epsilon。
        decimal Exact(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            decimal result = (bits & 0x7fffff) | 0x800000;
            int exponent = ((bits >> 23) & 255) - 150;
            while (exponent < 0) { result /= 2; exponent++; }
            while (exponent > 0) { result *= 2; exponent--; }
            return result;
        }
        decimal levelWeight = 1;
        foreach (var range in ranges[0])
            if (level >= range.Minimum && level <= range.Maximum)
            {
                levelWeight = Exact((float)range.First) + (Exact((float)range.Last) - Exact((float)range.First)) /
                    ((decimal)range.Maximum - (decimal)range.Minimum) * (level - (decimal)range.Minimum);
                break;
            }
        decimal count = levelWeight * Exact((float)row.Weight) * Exact((float)slotWeight) * Exact(modified) * row.Count;
        return count is > 0 and <= int.MaxValue ? new(row.Item, (int)count) : null;
    }

    public static EquipmentLevelTicket? Parse(PvfTokenDocument document)
    {
        var fields = document.Sections.Where(section => section.Name == "equipment level emancipate").ToArray();
        if (fields is not [{ IsBlock: true } block]) return null;
        int? Scalar(IEnumerable<PvfTokenSection> sections, string tag) => sections.Where(section => section.Name == tag).ToArray()
            is [{ Values: [{ Type: 0 } value] }] ? value.Value : null;
        var conditions = block.Children.Where(section => section.Name == "equipment condition").ToArray();
        if (conditions is not [{ IsBlock: true } condition]) return null;
        int? increase = Scalar(block.Children, "equipment upgrade level"), grade = Scalar(condition.Children, "rarity"),
            min = Scalar(condition.Children, "minimum level"), max = Scalar(condition.Children, "maximum level");
        var probability = block.Children.Where(section => section.Name == "probability").ToArray();
        var excluded = block.Children.Where(section => section.Name == "ignore index").ToArray();
        // 当前正式卷轴均为十万分之十万；未知概率保持拒绝，不能误当必成。
        if (increase is not (> 0 and <= 127) || grade is null || min is not > 0 || max is null || max <= min ||
            probability is not [{ Values.Length: > 0 } rates] || rates.Values.Length % 2 != 0 ||
            rates.Values.Where((token, index) => index % 2 == 0).Any(token => token.Type != 0 || token.Value < 0) ||
            rates.Values.Where((token, index) => index % 2 == 1).Any(token => token.Type != 0 || token.Value != 100000) ||
            excluded.Length > 1 || excluded.Length == 1 && excluded[0].Values.Any(token => token.Type != 0 || token.Value <= 0) ||
            condition.Children.Any(section => section.Name is not ("rarity" or "minimum level" or "maximum level")) ||
            block.Children.Any(section => section.Name is not ("equipment upgrade level" or "equipment condition" or "probability" or "ignore index"))) return null;
        return new(increase.Value, grade.Value, min.Value, max.Value,
            excluded.SelectMany(section => section.Values).Select(token => token.Value).ToImmutableHashSet());
    }
}
