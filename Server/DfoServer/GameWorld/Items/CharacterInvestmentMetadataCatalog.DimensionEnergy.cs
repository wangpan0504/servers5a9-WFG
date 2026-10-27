using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record DimensionEnergyInvestmentMetadata(bool Supported, string Reason, int EffectiveLevel,
    ImmutableArray<int> SourceIds);

internal sealed partial class CharacterInvestmentMetadataCatalog
{
    // 仅解释本服已识别的次元卷轴，不扫描全部堆叠资源，也不证明实例的历史消费来源。
    static readonly int[] DimensionEnergySources = [10094062, 10094063, 10304573, 490000095];
    readonly ConcurrentDictionary<int, Lazy<DimensionEnergyDefinition>> dimensionEnergy = new();
    sealed record DimensionEnergyDefinition(int Rarity, int MinimumLevel, int MaximumLevel, int Step,
        ImmutableHashSet<int> IgnoredItems);

    public DimensionEnergyInvestmentMetadata ReadDimensionEnergy(ItemTemplate host, byte stages)
    {
        int level = host.MinimumLevel + stages;
        if (stages == 0) return new(true, "no_dimension_energy", level, []);
        if (host.Kind != ItemKind.Equipment || host.Id <= 0)
            return new(false, "unsupported_dimension_energy_host", level, []);
        var sources = ImmutableArray.CreateBuilder<int>();
        bool unavailable = false;
        foreach (int id in DimensionEnergySources)
        {
            try
            {
                var profile = dimensionEnergy.GetOrAdd(id, key => new(() => LoadDimensionEnergy(key))).Value;
                if (host.Rarity == profile.Rarity && host.MinimumLevel >= profile.MinimumLevel &&
                    level <= profile.MaximumLevel && !profile.IgnoredItems.Contains(host.Id)) sources.Add(id);
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
            {
                unavailable = true;
            }
        }
        return new(sources.Count != 0, sources.Count != 0 ? "current_resource_support_envelope" :
            unavailable ? "dimension_energy_metadata_unavailable" : "outside_current_dimension_energy_support_envelope",
            level, sources.ToImmutable());
    }

    DimensionEnergyDefinition LoadDimensionEnergy(int id)
    {
        if (!stackable.TryGetValue(id, out var relative)) throw new InvalidDataException("次元卷轴索引缺失");
        var doc = Document(Path("stackable", relative));
        if (One(doc, "import script") is not null) throw new InvalidDataException("次元卷轴尚未支持脚本继承");
        var kind = One(doc, "stackable type");
        if (kind is null || kind.IsBlock || !kind.Children.IsEmpty || kind.Values.Length != 2 ||
            kind.Values[0].Type != 6 || resolve(kind.Values[0].Value) != "[etc]" ||
            kind.Values[1].Type != 0 || kind.Values[1].Value != 1)
            throw new InvalidDataException("次元卷轴种类声明无效");
        // 实际入口是emancipate ticket 5；不是名称相近的action type 61。
        var ticket = One(doc, "emancipate ticket");
        if (ticket is null || ticket.IsBlock || !ticket.Children.IsEmpty || ticket.Values.Length != 1 ||
            ticket.Values[0].Type != 0 || ticket.Values[0].Value != 5 || One(doc, "action type") is not null)
            throw new InvalidDataException("次元卷轴动作声明无效");
        var block = One(doc, "equipment level emancipate");
        if (block is null || !block.IsBlock || !block.Values.IsEmpty || block.Children.Length != 4)
            throw new InvalidDataException("次元卷轴升级范围不完整");
        PvfTokenSection Child(PvfTokenSection parent, string name)
        {
            var rows = parent.Children.Where(row => row.Name == name).ToArray();
            return rows.Length == 1 ? rows[0] : throw new InvalidDataException("次元卷轴字段缺失或重复: " + name);
        }
        int Scalar(PvfTokenSection section)
        {
            if (section.IsBlock || !section.Children.IsEmpty || section.Values.Length != 1 || section.Values[0].Type != 0)
                throw new InvalidDataException("次元卷轴字段不是单整数");
            return section.Values[0].Value;
        }
        int step = Scalar(Child(block, "equipment upgrade level"));
        if (step is < 1 or > 255) throw new InvalidDataException("次元卷轴增量超出byte合同");
        var condition = Child(block, "equipment condition");
        if (!condition.IsBlock || !condition.Values.IsEmpty || condition.Children.Length != 3)
            throw new InvalidDataException("次元卷轴条件不完整");
        int rarity = Scalar(Child(condition, "rarity"));
        int min = Scalar(Child(condition, "minimum level")), max = Scalar(Child(condition, "maximum level"));
        if (rarity is < 0 or > 6 || min is < 1 or > 255 || max <= min || max > 255)
            throw new InvalidDataException("次元卷轴条件范围非法");
        var ignore = Child(block, "ignore index");
        if (!ignore.IsBlock || !ignore.Children.IsEmpty || ignore.Values.IsEmpty ||
            ignore.Values.Any(token => token.Type != 0 || token.Value <= 0) ||
            ignore.Values.Select(token => token.Value).Distinct().Count() != ignore.Values.Length)
            throw new InvalidDataException("次元卷轴排除清单无效");
        var probability = Child(block, "probability");
        if (!probability.IsBlock || !probability.Children.IsEmpty || probability.Values.IsEmpty || probability.Values.Length % 2 != 0 ||
            probability.Values.Any(token => token.Type != 0))
            throw new InvalidDataException("次元卷轴概率声明不完整");
        int previous = -1;
        for (int index = 0; index < probability.Values.Length; index += 2)
        {
            int key = probability.Values[index].Value, weight = probability.Values[index + 1].Value;
            if (key <= previous || key > 255 || weight < 0) throw new InvalidDataException("次元卷轴概率声明无效");
            previous = key;
        }
        // 概率单位/历史用券过程不参与本服包络分，不把权重换成百分比或建设分。
        foreach (string name in new[] { "emancipate grade max", "emancipate amplify max", "emancipate genuinegrade max" })
        {
            var maximum = One(doc, name) ?? throw new InvalidDataException("次元卷轴附加限制缺失");
            if (Scalar(maximum) != -1) throw new InvalidDataException("次元卷轴附加打造限制尚未支持");
        }
        return new(rarity, min, max, step, ignore.Values.Select(token => token.Value).ToImmutableHashSet());
    }
}
