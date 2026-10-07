using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record RefinementCost(int ItemId, int Count, int SuccessWeight);

internal sealed class EquipmentRefinementCatalog
{
    readonly Dictionary<int, (int ItemId, int Count)> materials = new();
    readonly double[] rarityWeights;
    readonly (int SuccessWeight, double MaterialWeight)[] levels;
    public int MaximumLevel { get; }

    public EquipmentRefinementCatalog(PvfArchive pvf) : this(pvf.ReadText("etc/upgrade_separate.etc")) { }

    public EquipmentRefinementCatalog(string text)
    {
        var maximum = Integers(PvfScript.Section(text, "separate upgrade max"));
        var table = Numbers("table");
        var grades = Integers(PvfScript.Block(text, "item weights by grade"));
        rarityWeights = Numbers("item weights by rarity");
        if (maximum.Length != 1 || maximum[0] is < 1 or > 127 || table.Length % 4 != 0 ||
            table.Length / 4 < maximum[0] || grades.Length == 0 || grades.Length % 3 != 0 ||
            rarityWeights.Length == 0 || rarityWeights.Any(value => value <= 0))
            throw new InvalidDataException("PVF锻造表无效");
        MaximumLevel = maximum[0];
        levels = new (int, double)[table.Length / 4];
        for (int i = 0; i < levels.Length; i++)
        {
            double chance = table[i * 4 + 2], weight = table[i * 4 + 3];
            if (chance is < 0 or > 10000 || chance != Math.Truncate(chance) || weight <= 0)
                throw new InvalidDataException("PVF锻造概率或材料倍率无效");
            levels[i] = ((int)chance, weight);
        }
        for (int i = 0; i < grades.Length; i += 3)
            if (grades[i] < 0 || grades[i + 1] <= 0 || grades[i + 2] <= 0 ||
                !materials.TryAdd(grades[i], (grades[i + 1], grades[i + 2])))
                throw new InvalidDataException("PVF锻造材料等级表无效");

        double[] Numbers(string tag)
        {
            var values = PvfScript.Block(text, tag).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Any(value => !double.IsFinite(value)))
                throw new InvalidDataException("PVF锻造表包含非有限数值: " + tag);
            return values;
        }

        static int[] Integers(string values) => values.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
    }

    public bool TryCost(ItemTemplate template, byte currentLevel, out RefinementCost cost)
    {
        cost = new(0, 0, 0);
        if (currentLevel >= MaximumLevel || template.Rarity < 0 || template.Rarity >= rarityWeights.Length ||
            !materials.TryGetValue(template.Grade, out var material))
            return false;
        var level = levels[currentLevel];
        // 费用采用材料倍率；不能用独立攻击的stat倍率。微量补偿避免十进制倍率的浮点误差向下少扣一份。
        double count = material.Count * rarityWeights[template.Rarity] * level.MaterialWeight + 0.0000001;
        if (!double.IsFinite(count) || count > int.MaxValue)
            return false;
        cost = new(material.ItemId, Math.Max(1, (int)count), level.SuccessWeight);
        return true;
    }
}
