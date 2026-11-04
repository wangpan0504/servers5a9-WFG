using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Items;

internal enum AttributePairEntryKind { Booster, LimitCube }
internal enum AttributePairResolutionKind { NotRequired, RequiredUnavailable, RequiredValidated }
internal sealed record RequiredAttributePairSource(int ItemId, string Family, AttributePairEntryKind Entry);
internal sealed record AttributePairResolution(AttributePairResolutionKind Kind,
    IndependentAttributePairPolicy? Policy = null, string? Reason = null);
internal sealed record AttributePairBase(string Key, string Tag, ImmutableArray<int> Values);
internal sealed record AttributePairMapping(int First, int Second, int BeadId, int CardId, byte Index);
internal sealed record AttributePairSource(int ItemId, AttributePairEntryKind Entry);
internal sealed record AttributePairConfiguration(string Family, string PolicyIdentity, bool Enabled,
    ImmutableArray<AttributePairSource> Sources, ImmutableArray<AttributePairBase> Bases,
    ImmutableArray<AttributePairMapping> Pairs, ImmutableArray<int> LimitedPetIds);

internal sealed class IndependentAttributePairPolicy(string identity, ImmutableArray<int> orderedBeads,
    long maximumBeads)
{
    public const int Bound = 11;
    public string Identity { get; } = identity;
    public long MaximumBeads { get; } = maximumBeads;
    public int ResultItem(int first, int second) => orderedBeads[checked(first * Bound + second)];
}

// 必需身份与可缺失配置分离；任何配置失败都不能把必需来源降成普通加权来源。
internal sealed class IndependentAttributePairCatalog
{
    static readonly ImmutableArray<RequiredAttributePairSource> RequiredSourceRegistry =
    [
        new(490705394, "golden-pet-bead", AttributePairEntryKind.Booster),
        new(490705395, "golden-pet-bead", AttributePairEntryKind.LimitCube)
    ];
    static readonly FrozenDictionary<int, RequiredAttributePairSource> Required = BuildRequired();
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<AttributePairEntryKind>(allowIntegerValues: false) }
    };
    static long nextRevision;
    readonly FrozenDictionary<string, IndependentAttributePairPolicy> policies;
    readonly FrozenDictionary<string, string> errors;
    public static IndependentAttributePairCatalog RequiredOnly { get; } = Unavailable("configuration_missing");
    public long Revision { get; } = Interlocked.Increment(ref nextRevision);
    public int ValidatedFamilies => policies.Count;
    public string? UnavailableReason => errors.Values.FirstOrDefault();

    IndependentAttributePairCatalog(Dictionary<string, IndependentAttributePairPolicy> policies,
        Dictionary<string, string> errors)
    {
        this.policies = policies.ToFrozenDictionary(StringComparer.Ordinal);
        this.errors = errors.ToFrozenDictionary(StringComparer.Ordinal);
    }

    static FrozenDictionary<int, RequiredAttributePairSource> BuildRequired()
    {
        if (RequiredSourceRegistry.IsEmpty || RequiredSourceRegistry.Any(row => row.ItemId <= 0 ||
            string.IsNullOrEmpty(row.Family) || !Enum.IsDefined(row.Entry)))
            throw new InvalidOperationException("属性对必需来源注册表无效");
        return RequiredSourceRegistry.ToFrozenDictionary(row => row.ItemId);
    }

    static IndependentAttributePairCatalog Unavailable(string reason) => new([], Required.Values
        .Select(row => row.Family).Distinct().ToDictionary(family => family, _ => reason, StringComparer.Ordinal));

    public AttributePairResolution Resolve(int itemId, AttributePairEntryKind entry)
    {
        if (!Required.TryGetValue(itemId, out var source))
            return new(AttributePairResolutionKind.NotRequired);
        if (source.Entry != entry)
            return new(AttributePairResolutionKind.RequiredUnavailable, Reason: "entry_mismatch");
        return policies.TryGetValue(source.Family, out var policy)
            ? new(AttributePairResolutionKind.RequiredValidated, policy)
            : new(AttributePairResolutionKind.RequiredUnavailable, Reason: errors.GetValueOrDefault(source.Family, "family_missing"));
    }

    // 外置文件优先（运维直接改安装目录即可生效），文件缺失才回退同版本内嵌规则；坏配置仍拒绝。
    public static IndependentAttributePairCatalog Load(string path, ItemCatalog items, BoosterCatalog boosters,
        LimitCubeCatalog cubes, EnchantmentCatalog enchantments, EpicPieceCatalog pieces,
        PremiumContractCatalog contracts, Func<int, bool> isCurrencyReward, bool useBundledDefault = false)
    {
        try { return Parse(File.ReadAllText(path), items, boosters, cubes, enchantments, pieces, contracts, isCurrencyReward); }
        catch (Exception error) when (useBundledDefault && error is FileNotFoundException or DirectoryNotFoundException)
        { return LoadBundled(items, boosters, cubes, enchantments, pieces, contracts, isCurrencyReward); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Unavailable("configuration_unreadable"); }
    }

    // 单文件分发不携带Resources目录：同版本内嵌规则既是默认值，也是外部文件缺失时的回退；坏配置仍拒绝。
    static IndependentAttributePairCatalog LoadBundled(ItemCatalog items, BoosterCatalog boosters,
        LimitCubeCatalog cubes, EnchantmentCatalog enchantments, EpicPieceCatalog pieces,
        PremiumContractCatalog contracts, Func<int, bool> isCurrencyReward)
    {
        using var stream = typeof(IndependentAttributePairCatalog).Assembly
            .GetManifestResourceStream("DfoServer.Resources.independent-attribute-pairs.json");
        if (stream is null) return Unavailable("bundled_configuration_missing");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), items, boosters, cubes, enchantments, pieces, contracts, isCurrencyReward);
    }

    public static IndependentAttributePairCatalog Parse(string json, ItemCatalog items, BoosterCatalog boosters,
        LimitCubeCatalog cubes, EnchantmentCatalog enchantments, EpicPieceCatalog pieces,
        PremiumContractCatalog contracts, Func<int, bool> isCurrencyReward)
    {
        AttributePairConfiguration[] configurations;
        try
        {
            configurations = JsonSerializer.Deserialize<AttributePairConfiguration[]>(json, JsonOptions)
                ?? throw new JsonException();
            if (configurations.Any(row => row is null || string.IsNullOrEmpty(row.Family) || row.Sources.IsDefault ||
                    row.Sources.Any(source => source is null)) ||
                configurations.Select(row => row.Family).Distinct(StringComparer.Ordinal).Count() != configurations.Length ||
                configurations.SelectMany(row => row.Sources).GroupBy(row => row.ItemId).Any(group => group.Count() != 1))
                throw new JsonException();
        }
        catch (JsonException) { return Unavailable("configuration_invalid"); }
        var policies = new Dictionary<string, IndependentAttributePairPolicy>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var requiredFamily in Required.Values.GroupBy(row => row.Family))
        {
            try
            {
                var configuration = configurations.SingleOrDefault(row => row.Family == requiredFamily.Key);
                if (configuration is null || !configuration.Enabled)
                    throw new InvalidDataException("family_missing_or_disabled");
                policies.Add(requiredFamily.Key, Validate(configuration, requiredFamily.ToArray(), items,
                    boosters, cubes, enchantments, pieces, contracts, isCurrencyReward));
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException or ArgumentException or IOException)
            { errors.Add(requiredFamily.Key, "family_validation_failed"); }
        }
        return new(policies, errors);
    }

    static IndependentAttributePairPolicy Validate(AttributePairConfiguration config, RequiredAttributePairSource[] sources,
        ItemCatalog items, BoosterCatalog boosters, LimitCubeCatalog cubes, EnchantmentCatalog enchantments,
        EpicPieceCatalog pieces, PremiumContractCatalog contracts, Func<int, bool> isCurrencyReward)
    {
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition)
        {
            if (!condition) throw new InvalidDataException("属性对族不完整或与资源不一致");
        }
        Require(Regex.IsMatch(config.PolicyIdentity, @"\A[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant));
        Require(config.Sources.Length == sources.Length && config.Sources.All(source =>
            sources.Any(required => required.ItemId == source.ItemId && required.Entry == source.Entry)));
        Require(!config.Bases.IsDefault && config.Bases.Length == IndependentAttributePairPolicy.Bound &&
            config.Bases.All(row => row is not null && !string.IsNullOrEmpty(row.Key) && !string.IsNullOrEmpty(row.Tag) &&
                !row.Values.IsDefaultOrEmpty && row.Values.All(value => value > 0)) &&
            config.Bases.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count() == 11 &&
            config.Bases.Select(row => row.Tag).Distinct(StringComparer.Ordinal).Count() == 11);
        Require(!config.Pairs.IsDefault && config.Pairs.Length == 66 && config.Pairs.All(row => row is not null) &&
            config.Pairs.Select(row => (row.First, row.Second)).Distinct().Count() == 66 &&
            config.Pairs.Select(row => row.BeadId).Distinct().Count() == 66 &&
            config.Pairs.Select(row => row.CardId).Distinct().Count() == 66);
        Require(!config.LimitedPetIds.IsDefault && config.LimitedPetIds.Length == 4 &&
            config.LimitedPetIds.ToHashSet().SetEquals(new[] { 400990444, 400990445, 400990446, 400990447 }));
        int[] ordered = new int[121];
        long maximumBeads = 0;
        foreach (var row in config.Pairs)
        {
            Require(row.First >= 0 && row.First <= row.Second && row.Second < 11 && row.BeadId > 0 && row.CardId > 0 && row.Index == 0);
            var bead = items.Get(row.BeadId);
            Require(bead.Id == row.BeadId && (bead.Kind is ItemKind.Consumable or ItemKind.Material or ItemKind.Quest or ItemKind.Profession or ItemKind.Emblem) &&
                bead.StackLimit == 1000 && !isCurrencyReward(row.BeadId) && !pieces.ByPiece.ContainsKey(row.BeadId) &&
                !contracts.Recognizes(row.BeadId) && !boosters.DirectActivationItems.Contains(row.BeadId) &&
                bead.AcquisitionContract is null && !bead.IsPremiumService);
            var definition = enchantments.Get(row.BeadId);
            Require(definition is not null && definition.CardItemId == row.CardId && definition.Upgrade == row.Index &&
                definition.Targets.SetEquals(new[] { (ItemKind.Creature, (byte)26) }) &&
                definition.LimitedItems.SetEquals(config.LimitedPetIds));
            var first = config.Bases[row.First];
            var second = config.Bases[row.Second];
            var expected = row.First == row.Second
                ? ImmutableArray.Create(new EnchantmentEffect(first.Tag, first.Values.Select(value => checked(value * 2)).ToImmutableArray()))
                : ImmutableArray.Create(new EnchantmentEffect(first.Tag, first.Values), new EnchantmentEffect(second.Tag, second.Values));
            var actual = enchantments.ReadEffects(row.CardId, row.Index);
            Require(actual.Length == expected.Length && actual.Zip(expected).All(pair =>
                pair.First.Tag == pair.Second.Tag && pair.First.IntegerValues.SequenceEqual(pair.Second.IntegerValues)));
            ordered[row.First * 11 + row.Second] = ordered[row.Second * 11 + row.First] = row.BeadId;
            maximumBeads = checked(maximumBeads + bead.StackLimit);
        }
        Require(ordered.All(id => id > 0));
        var beads = config.Pairs.Select(row => row.BeadId).ToHashSet();
        foreach (var source in sources)
        {
            var template = items.Get(source.ItemId);
            Require(template.Id == source.ItemId && template.Kind == ItemKind.Consumable);
            if (source.Entry == AttributePairEntryKind.Booster)
            {
                var definition = boosters.Get(source.ItemId);
                Require(template.StackLimit == 1000 && definition.Available && definition.Pools.Length == 1 &&
                    definition.Selection is null && definition.PackageRewards.IsDefaultOrEmpty && !definition.Lottery &&
                    !definition.NpcPocketPot && !definition.SetsDisjointInstanceType && definition.DailyUseLimit is null);
                var pool = definition.Pools[0];
                Require(pool.Draws == 1 && pool.JobTag is null && pool.GrowType == -1 && pool.MinimumLevel == 0 &&
                    pool.MaximumLevel == int.MaxValue && pool.RequiredKind is null && pool.Rewards.Length == 66 &&
                    pool.Rewards.All(row => row.Count == 1 && row.Weight > 0) &&
                    pool.Rewards.Select(row => row.ItemId).ToHashSet().SetEquals(beads));
            }
            else
            {
                var definition = cubes.Get(source.ItemId);
                Require(definition is not null && definition.ItemId == source.ItemId && definition.Results.Length == 66 &&
                    definition.Results.All(row => row.Count == 1 && row.Weight > 0) &&
                    definition.Results.Select(row => row.ItemId).ToHashSet().SetEquals(beads) &&
                    definition.Targets.Length == 66 && definition.Targets.All(row => row.Count == 1) &&
                    definition.Targets.Select(row => row.ItemId).ToHashSet().SetEquals(beads) &&
                    definition.Materials is [{ ItemId: 3037, Count: 10 }]);
            }
        }
        return new(config.PolicyIdentity, [.. ordered], maximumBeads);
    }
}
