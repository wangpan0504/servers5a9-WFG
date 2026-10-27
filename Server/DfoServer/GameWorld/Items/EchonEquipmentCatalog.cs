using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EchonEquipmentDefinition(byte SetIndex, int ArmorType, EchonEquipmentCatalog Rules,
    byte InitialFirst = 0, byte InitialSecond = 0);
internal sealed record EchonSetRule(byte Index, int MinimumParts, int MaximumParts, int MaximumOptions,
    FrozenDictionary<(byte Grade, int Parts), int> Points, ImmutableArray<(int ItemId, float Rate)> Materials,
    float Discount);
internal sealed class EchonEquipmentCatalog
{
    public const string IndividualPath = "etc/customability/individualcustominfo.etc";
    public const string SetPath = "etc/customability/partsetcustominfo.etc";
    public ImmutableArray<byte> Options { get; }
    public ImmutableArray<(byte Grade, int Weight)> GradeWeights { get; }
    public FrozenDictionary<(byte Option, byte Grade), ImmutableArray<ItemMaterialCost>> OptionCosts { get; }
    public FrozenDictionary<byte, ImmutableArray<ItemMaterialCost>> UpgradeCosts { get; }
    public FrozenDictionary<(byte Option, byte Grade), ImmutableArray<ItemMaterialCost>> Disjoint { get; }
    public FrozenDictionary<byte, EchonSetRule> Sets { get; }
    public FrozenDictionary<byte, (string Group, int MinimumParts, int Points)> SetOptions { get; }
    public FrozenSet<int> DisabledDisjointItems { get; }
    public bool IsModern { get; }
    public FrozenSet<int> ReadOnlyOptions { get; }

    public EchonEquipmentCatalog(PvfArchive archive) : this(CustomAbilityFile.Parse(archive.ReadText(IndividualPath)),
        CustomAbilitySetFile.Parse(archive.ReadText(SetPath)),
        PvfScript.List(archive.ReadText("etc/customability/individualcustomability.lst")),
        PvfScript.List(archive.ReadText("etc/customability/partsetcustomability.lst"))) { }

    internal EchonEquipmentCatalog(CustomAbilityFile individual, CustomAbilitySetFile set,
        IReadOnlyDictionary<int, string>? individualList = null, IReadOnlyDictionary<int, string>? setList = null)
    {
        IsModern = individual.Type == "new warriors dungeon";
        if (!IsModern && individual.Type != "warriors dungeon") throw Invalid();
        var options = individual.Groups.SelectMany(row => row.Indexes).ToArray();
        if (options.Length == 0 || options.Any(id => id is <= 0 or > 255) || options.Distinct().Count() != options.Length ||
            individualList is not null && options.Any(id => !individualList.ContainsKey(id))) throw Invalid();
        Options = options.Select(id => (byte)id).ToImmutableArray();
        if (individual.OptionCategories.Any(row => row.Option is <= 0 or > 255 || !Options.Contains((byte)row.Option))) throw Invalid();
        ReadOnlyOptions = individual.OptionCategories.Where(row => IsModern && row.Category == 1).Select(row => row.Option).ToFrozenSet();
        if (individual.DisabledDisjointItems.Any(id => id <= 0)) throw Invalid();
        DisabledDisjointItems = individual.DisabledDisjointItems.ToFrozenSet();
        GradeWeights = individual.GradeWeights.Select(row => (Grade(row.Grade), row.Weight)).ToImmutableArray();
        int grades = IsModern ? 1 : 4;
        if (GradeWeights.Length != grades || GradeWeights.Select(row => row.Grade).Distinct().Count() != grades ||
            IsModern && GradeWeights[0].Grade != 0 ||
            GradeWeights.Any(row => row.Weight < 0) || GradeWeights.Sum(row => (long)row.Weight) is <= 0 or > int.MaxValue)
            throw Invalid();
        if (individual.Groups.Any(row => row.Key.Length == 0) ||
            individual.Groups.Select(row => row.Key).Distinct().Count() != individual.Groups.Length ||
            individual.Options.Length != Options.Length ||
            individual.Options.Select(row => row.Index).Distinct().Count() != individual.Options.Length ||
            individual.Options.Any(row => row.Index is <= 0 or > 255 || !Options.Contains((byte)row.Index) ||
                row.Materials.Length != grades || row.Materials.Select(material => Grade(material.Grade)).Distinct().Count() != grades) ||
            individual.Upgrades.Select(row => Grade(row.Grade)).Distinct().Count() != individual.Upgrades.Length)
            throw Invalid();
        OptionCosts = individual.Options.SelectMany(option => option.Materials.Select(row =>
            new KeyValuePair<(byte, byte), ImmutableArray<ItemMaterialCost>>((checked((byte)option.Index), Grade(row.Grade)), Costs(row.Items))))
            .ToFrozenDictionary();
        UpgradeCosts = individual.Upgrades.ToFrozenDictionary(row => Grade(row.Grade), row => Costs(row.Items));
        if (Options.Any(option => Enumerable.Range(0, grades).Any(grade => !OptionCosts.ContainsKey((option, (byte)grade)))) ||
            !IsModern && Enumerable.Range(1, 3).Any(grade => !UpgradeCosts.ContainsKey((byte)grade))) throw Invalid();
        var disjoint = new Dictionary<(byte, byte), ImmutableArray<ItemMaterialCost>>();
        foreach (var row in individual.Disjoint)
        {
            var group = individual.Groups.SingleOrDefault(group => group.Key == row.Group) ?? throw Invalid();
            foreach (int option in group.Indexes)
                if (!disjoint.TryAdd(((byte)option, Grade(row.Grade)), Costs(row.Items))) throw Invalid();
        }
        Disjoint = disjoint.ToFrozenDictionary();
        if (IsModern)
        {
            Sets = new Dictionary<byte, EchonSetRule>().ToFrozenDictionary();
            SetOptions = new Dictionary<byte, (string, int, int)>().ToFrozenDictionary();
            return;
        }
        if (set.Points.Any(row => row.Index is <= 0 or > 255) ||
            set.Points.Select(row => row.Index).Distinct().Count() != set.Points.Length ||
            set.Spend.Select(row => row.Option).Distinct().Count() != set.Spend.Length ||
            set.Groups.SelectMany(row => row.Indexes).Distinct().Count() != set.Groups.Sum(row => row.Indexes.Length))
            throw Invalid();
        Sets = set.Points.ToFrozenDictionary(row => (byte)row.Index, row =>
        {
            if (row.Index is <= 0 or > 255 || row.MinimumParts < 3 || row.MaximumParts > 6 || row.MinimumParts > row.MaximumParts ||
                row.MaximumOptions is < 1 or > 4 || row.Materials.IsEmpty ||
                row.Materials.Any(material => material.ItemId <= 0 || !float.IsFinite(material.Rate) || material.Rate <= 0) ||
                !float.IsFinite(row.DiscountRate) || row.DiscountRate is < 0 or > 1) throw Invalid();
            var points = row.MaximumPoints.ToFrozenDictionary(value => (Grade(value.Grade), value.Parts), value => value.Points);
            if (points.Any(value => value.Value <= 0 || value.Key.Parts < row.MinimumParts || value.Key.Parts > row.MaximumParts) || Enumerable.Range(0, 4).Any(grade =>
                Enumerable.Range(row.MinimumParts, row.MaximumParts - row.MinimumParts + 1).Any(parts => !points.ContainsKey(((byte)grade, parts)))))
                throw Invalid();
            if (row.MaximumPoints.Select(value => (Grade(value.Grade), value.Parts)).Distinct().Count() != row.MaximumPoints.Length)
                throw Invalid();
            return new EchonSetRule((byte)row.Index, row.MinimumParts, row.MaximumParts, row.MaximumOptions, points, row.Materials, row.DiscountRate);
        });
        var spend = set.Spend.ToDictionary(row => row.Option, row => row.Points);
        SetOptions = set.Groups.SelectMany(group => group.Indexes.Select(option =>
        {
            if (group.Key.Length == 0 || option is <= 0 or > 255 || !spend.TryGetValue(option, out int points) || points <= 0 ||
                setList is not null && !setList.ContainsKey(option)) throw Invalid();
            return new KeyValuePair<byte, (string, int, int)>((byte)option, (group.Key, group.MinimumParts, points));
        })).ToFrozenDictionary();
        if (Sets.Count == 0 || SetOptions.Count == 0) throw Invalid();
    }

    public (byte First, byte Second, byte Grade) Roll(Func<int, int>? draw = null, byte? grade = null)
    {
        draw ??= Random.Shared.Next;
        byte first = Options[draw(Options.Length)], second = Options[draw(Options.Length)];
        if (grade is { } fixedGrade) return fixedGrade <= 3 ? (first, second, fixedGrade) : throw Invalid();
        int roll = draw(GradeWeights.Sum(row => row.Weight));
        foreach (var row in GradeWeights)
        {
            if (roll < row.Weight) return (first, second, row.Grade);
            roll -= row.Weight;
        }
        throw Invalid();
    }

    public ImmutableArray<ItemMaterialCost>? SetCost(byte index, byte grade, int parts, IReadOnlyList<byte> options)
    {
        if (!Sets.TryGetValue(index, out var rule) || parts < rule.MinimumParts || parts > rule.MaximumParts ||
            !rule.Points.TryGetValue((grade, parts), out int available) ||
            options.Count < 1 || options.Count > rule.MaximumOptions) return null;
        var groups = new HashSet<string>();
        int remaining = available;
        foreach (byte option in options)
        {
            if (!SetOptions.TryGetValue(option, out var row) || parts < row.MinimumParts || !groups.Add(row.Group)) return null;
            int cost = checked((int)(row.Points * (remaining + 1000L) / (row.Points + 1000L)));
            if (cost <= 0 || cost > remaining) return null;
            remaining -= cost;
        }
        return rule.Materials.Select(row => new ItemMaterialCost(row.ItemId,
            checked((int)((available - remaining) * row.Rate)))).ToImmutableArray();
    }

    static ImmutableArray<ItemMaterialCost> Costs(ImmutableArray<(int ItemId, int Count)> rows)
    {
        if (rows.IsEmpty || rows.Any(row => row.ItemId <= 0 || row.Count <= 0) || rows.Select(row => row.ItemId).Distinct().Count() != rows.Length)
            throw Invalid();
        return rows.Select(row => new ItemMaterialCost(row.ItemId, row.Count)).ToImmutableArray();
    }
    internal static byte Grade(string grade) => grade switch { "D" => 0, "C" => 1, "B" => 2, "A" => 3, _ => throw Invalid() };
    static InvalidDataException Invalid() => new("艾肯装备资源合同无效");
}
