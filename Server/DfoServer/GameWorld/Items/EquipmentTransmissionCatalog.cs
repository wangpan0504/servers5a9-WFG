using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 赛贝琳使用独立的传承表，不复用普通装备升级或刻印的费用组。
internal sealed class EquipmentTransmissionCatalog
{
    public const string ResourcePath = "etc/chn_equipmentupgradesystem.etc";
    public FrozenSet<int> Targets { get; }
    public FrozenSet<int> DonorRarities { get; }
    public ImmutableArray<ItemMaterialCost> Materials { get; }

    public EquipmentTransmissionCatalog(PvfArchive archive)
        : this(archive.Contains(ResourcePath) ? archive.ReadText(ResourcePath)
            : throw new InvalidDataException("PVF缺少装备传承表: " + ResourcePath)) { }

    internal EquipmentTransmissionCatalog(string text)
    {
        Targets = Unique(Values(text, "target item list"), false).ToFrozenSet();
        DonorRarities = Unique(Values(text, "need item rarity"), true).ToFrozenSet();
        int[] values = Values(text, "material item");
        if (values.Length == 0 || values.Length % 3 != 0)
            throw new InvalidDataException("PVF装备传承材料行不完整");
        var materials = ImmutableArray.CreateBuilder<ItemMaterialCost>();
        for (int offset = 0; offset < values.Length; offset += 3)
        {
            int condition = values[offset], itemId = values[offset + 1], count = values[offset + 2];
            if (condition is < 0 or > 2 || itemId < 0 || count <= 0)
                throw new InvalidDataException("PVF装备传承材料无效");
            // 客户端00911DA0传入的两项选择均为0，只有条件0参与本请求的费用。
            if (condition == 0) materials.Add(new(itemId, count));
        }
        Materials = materials.ToImmutable();
    }

    static int[] Unique(int[] values, bool allowZero)
    {
        if (values.Length == 0 || values.Any(value => value < (allowZero ? 0 : 1)) ||
            values.Distinct().Count() != values.Length)
            throw new InvalidDataException("PVF装备传承候选无效或重复");
        return values;
    }

    static int[] Values(string text, string tag)
    {
        var matches = Regex.Matches(text, @"\[" + Regex.Escape(tag) + @"\](.*?)\[/" +
            Regex.Escape(tag) + @"\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matches.Count != 1) throw new InvalidDataException("PVF装备传承段缺失或重复: " + tag);
        return matches[0].Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value : throw new InvalidDataException("PVF装备传承数值无效")).ToArray();
    }
}
