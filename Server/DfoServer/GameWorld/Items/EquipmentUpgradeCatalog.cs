using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentUpgradeGroup(int Id, ImmutableArray<ItemMaterialCost> Materials,
    ImmutableArray<ItemMaterialCost> FusionMaterials, ImmutableArray<ItemMaterialCost> AdditionMaterials,
    int MinimumUpgrade, int MinimumSeparate, FrozenDictionary<int, decimal> UpgradeRates,
    FrozenDictionary<int, decimal> SeparateRates,
    FrozenDictionary<int, ImmutableArray<ItemMaterialCost>> CardMaterials,
    FrozenDictionary<int, ImmutableArray<ItemMaterialCost>> EmblemMaterials, bool InheritAbility);

internal sealed record EquipmentUpgradeRoute(int Group, int Source, ImmutableArray<int> Targets);
internal sealed record EquipmentFusionRoute(int Group, int Source, int UpgradedEquivalent, ImmutableArray<int> Targets);

// 升级、转换和刻印共用客户端同一张资源表；候选范围只来自表内映射。
internal sealed class EquipmentUpgradeCatalog
{
    public const string ResourcePath = "etc/equipmentupgradesystem.etc";
    public FrozenDictionary<int, EquipmentUpgradeGroup> Groups { get; }
    public FrozenDictionary<int, EquipmentUpgradeRoute> Upgrades { get; }
    public FrozenDictionary<int, EquipmentFusionRoute> Fusions { get; }
    public FrozenDictionary<int, int> WeaponImprints { get; }
    public ImmutableArray<ItemMaterialCost> WeaponImprintMaterials { get; }

    public EquipmentUpgradeCatalog(PvfArchive archive)
        : this(archive.Contains(ResourcePath) ? archive.ReadText(ResourcePath)
            : throw new InvalidDataException("PVF缺少装备升级表: " + ResourcePath)) { }

    internal EquipmentUpgradeCatalog(string text)
    {
        var groups = new Dictionary<int, EquipmentUpgradeGroup>();
        foreach (Match match in Regex.Matches(text, @"\[group\](.*?)\[/group\]", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string body = match.Groups[1].Value;
            int[] index = Integers(Section(body, "index"));
            if (index.Length != 1 || index[0] <= 0)
                throw new InvalidDataException("PVF装备升级组编号无效");
            var group = new EquipmentUpgradeGroup(index[0], Costs(Block(body, "need item")),
                Costs(Block(body, "fusion need item")), Costs(Block(body, "addition need item")),
                checked((int)Number(body, "min upgrade")), checked((int)Number(body, "min Separate")),
                Rates(Block(body, "add upgrade rate")), Rates(Block(body, "add separate rate")),
                EnchantCosts(Block(body, "card enchant need item")), EnchantCosts(Block(body, "emblem enchant need item")),
                Regex.IsMatch(body, @"\[inherit ability\]", RegexOptions.IgnoreCase));
            if (!groups.TryAdd(group.Id, group))
                throw new InvalidDataException("PVF装备升级组重复");
        }
        if (groups.Count == 0) throw new InvalidDataException("PVF装备升级表为空");
        Groups = groups.ToFrozenDictionary();

        var upgrades = new Dictionary<int, EquipmentUpgradeRoute>();
        int[] values = Integers(Block(text, "upgrade item list"));
        for (int offset = 0; offset < values.Length;)
        {
            CheckRemaining(values, offset, 3);
            int group = values[offset++], source = values[offset++], count = values[offset++];
            var targets = Targets(values, ref offset, count);
            if (!groups.ContainsKey(group) || source <= 0 || !upgrades.TryAdd(source, new(group, source, targets)))
                throw new InvalidDataException("PVF装备升级映射无效");
        }
        Upgrades = upgrades.ToFrozenDictionary();

        var fusions = new Dictionary<int, EquipmentFusionRoute>();
        values = Integers(Block(text, "fusion item list"));
        for (int offset = 0; offset < values.Length;)
        {
            CheckRemaining(values, offset, 4);
            int group = values[offset++], source = values[offset++], equivalent = values[offset++], count = values[offset++];
            var targets = Targets(values, ref offset, count);
            if (!groups.ContainsKey(group) || source <= 0 || equivalent < 0 ||
                !fusions.TryAdd(source, new(group, source, equivalent, targets)))
                throw new InvalidDataException("PVF装备刻印映射无效");
        }
        Fusions = fusions.ToFrozenDictionary();
        WeaponImprintMaterials = Costs(Block(text, "prey weapon imprint need item"));
        values = Integers(Block(text, "prey weapon imprint item list"));
        if (values.Length % 2 != 0) throw new InvalidDataException("PVF权能融合映射不完整");
        var weapons = new Dictionary<int, int>();
        for (int offset = 0; offset < values.Length; offset += 2)
            if (values[offset] <= 0 || values[offset + 1] <= 0 || !weapons.TryAdd(values[offset], values[offset + 1]))
                throw new InvalidDataException("PVF权能融合映射无效");
        WeaponImprints = weapons.ToFrozenDictionary();
    }

    static ImmutableArray<int> Targets(int[] values, ref int offset, int count)
    {
        if (count <= 0) throw new InvalidDataException("PVF装备升级目标数量无效");
        CheckRemaining(values, offset, count);
        var result = values.AsSpan(offset, count).ToArray().ToImmutableArray();
        offset += count;
        if (result.Any(id => id <= 0) || result.Distinct().Count() != count)
            throw new InvalidDataException("PVF装备升级目标无效");
        return result;
    }

    static void CheckRemaining(int[] values, int offset, int count)
    {
        if (count > values.Length - offset) throw new InvalidDataException("PVF装备升级映射被截断");
    }

    static ImmutableArray<ItemMaterialCost> Costs(string text)
    {
        int[] values = Integers(text);
        if (values.Length % 2 != 0) throw new InvalidDataException("PVF装备升级费用不完整");
        var result = ImmutableArray.CreateBuilder<ItemMaterialCost>(values.Length / 2);
        for (int offset = 0; offset < values.Length; offset += 2)
        {
            if (values[offset] < 0 || values[offset + 1] <= 0)
                throw new InvalidDataException("PVF装备升级费用无效");
            result.Add(new(values[offset], values[offset + 1]));
        }
        return result.MoveToImmutable();
    }

    static FrozenDictionary<int, decimal> Rates(string text)
    {
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length % 2 != 0) throw new InvalidDataException("PVF装备升级倍率不完整");
        var result = new Dictionary<int, decimal>();
        for (int offset = 0; offset < tokens.Length; offset += 2)
        {
            int level = int.Parse(tokens[offset], CultureInfo.InvariantCulture);
            decimal rate = decimal.Parse(tokens[offset + 1], CultureInfo.InvariantCulture);
            if (level <= 0 || rate <= 0 || !result.TryAdd(level, rate))
                throw new InvalidDataException("PVF装备升级倍率无效");
        }
        return result.ToFrozenDictionary();
    }

    static FrozenDictionary<int, ImmutableArray<ItemMaterialCost>> EnchantCosts(string text)
    {
        int[] values = Integers(text);
        if (values.Length % 3 != 0) throw new InvalidDataException("PVF打造继承费用不完整");
        var result = new Dictionary<int, ImmutableArray<ItemMaterialCost>>();
        for (int offset = 0; offset < values.Length; offset += 3)
        {
            if (values[offset] < 0 || values[offset + 1] < 0 || values[offset + 2] <= 0 ||
                !result.TryAdd(values[offset], [new(values[offset + 1], values[offset + 2])]))
                throw new InvalidDataException("PVF打造继承费用无效");
        }
        return result.ToFrozenDictionary();
    }
}
